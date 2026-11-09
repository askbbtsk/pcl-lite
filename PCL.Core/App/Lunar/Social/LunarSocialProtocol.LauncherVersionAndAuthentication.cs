using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.App.Lunar.Social;

internal static partial class LunarSocialProtocol
{

    /// <summary>
    ///     本机官方启动器的版本号，没装就是 <see langword="null" />。
    ///     <para>形如 <c>3.7.27-ow</c>，只留数字和点。</para>
    /// </summary>
    private static string? ResolveLocalLauncherVersion()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = System.IO.Path.Combine(local, "Programs", "Lunar Client", "Lunar Client.exe");
            if (!System.IO.File.Exists(path)) return null;

            var raw = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var cleaned = new string(raw.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
            var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? cleaned : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }


    /// <summary>官方启动器的 UA：<c>Lunar Client Launcher v{最新正式版}</c>。</summary>
    public static string LauncherUserAgent => "Lunar Client Launcher v" + ResolveLauncherVersion();


    /// <summary>
    ///     取 Lunar 官方启动器的最新正式版本号（形如 <c>3.7.29</c>），拿不到返回 <see langword="null" />。
    ///     <para>
    ///         命中缓存（6 小时内）直接返回；否则去官方更新源查一次。
    ///         查失败时保留上一次的好值，不会把缓存冲成空。
    ///     </para>
    /// </summary>
    public static async Task<string?> FetchLatestLauncherVersionAsync(
        CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _onlineVersion);
        if (cached is not null && Environment.TickCount64 < Volatile.Read(ref _onlineVersionExpiresAt))
            return cached;

        await OnlineVersionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _onlineVersion);
            if (cached is not null && Environment.TickCount64 < _onlineVersionExpiresAt) return cached;

            var fetched = await FetchLatestLauncherVersionCoreAsync(cancellationToken).ConfigureAwait(false);
            if (fetched is null) return cached;

            Volatile.Write(ref _onlineVersion, fetched);
            Volatile.Write(ref _onlineVersionExpiresAt,
                Environment.TickCount64 + (long)OnlineVersionTtl.TotalMilliseconds);
            return fetched;
        }
        finally
        {
            OnlineVersionGate.Release();
        }
    }


    private static async Task<string?> FetchLatestLauncherVersionCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (feedUrl, channel) = await ResolveUpdateFeedAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{feedUrl}/{channel}.yml");
            request.Headers.TryAddWithoutValidation("User-Agent", BootstrapUserAgent);

            using var response = await VersionHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var yaml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseChannelVersion(yaml);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or IOException or InvalidOperationException)
        {
            return null;
        }
    }


    /// <summary>
    ///     问 <c>/metadata/misc</c> 要当前的更新源；拿不到就用官方启动器里写死的那一对。
    /// </summary>
    private static async Task<(string FeedUrl, string Channel)> ResolveUpdateFeedAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var url = LunarApiBase + "/metadata/misc" +
                      "?installation_id=00000000-0000-0000-0000-000000000000";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BootstrapUserAgent);

            using var response = await VersionHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var updater = JsonNode.Parse(text)?["updater"];
                var feedUrl = updater?["feedUrl"]?.GetValue<string>();
                var channel = updater?["channel"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(feedUrl) && !string.IsNullOrWhiteSpace(channel))
                    return (feedUrl, channel);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException)
        {
            // 断网 / 超时 / 结构变了：退回默认值
        }

        return (DefaultUpdateFeedUrl, DefaultUpdateChannel);
    }


    /// <summary>
    ///     从 electron-builder 的频道清单里抠 <c>version</c>，剪掉 <c>-ow</c> 之类的后缀。
    ///     <para>
    ///         清单长这样，只关心第一行：
    ///         <code>
    ///         version: 3.7.29-ow
    ///         files:
    ///           - url: Lunar Client v3.7.29-ow.exe
    ///         </code>
    ///     </para>
    /// </summary>
    internal static string? ParseChannelVersion(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("version:", StringComparison.Ordinal)) continue;

            var value = line["version:".Length..].Trim().Trim('\'', '"');
            var end = 0;
            while (end < value.Length && (char.IsDigit(value[end]) || value[end] == '.')) end++;

            var cleaned = value[..end].Trim('.');
            return cleaned.Contains('.') ? cleaned : null;
        }

        return null;
    }


    /// <summary>
    ///     首选的上报版本：<b>在线最新正式版</b> → 本机官方启动器 → 兜底。
    ///     <para>
    ///         在线版本优先于本机版本：本机那个可能已经落后好几版，而服务端抬下限
    ///         抬的是「官方最新」那条线。
    ///     </para>
    ///     <para>
    ///         <b>不会发网络请求</b>——只读缓存。调用方应先在启动早期 await 一次
    ///         <see cref="FetchLatestLauncherVersionAsync" /> 把缓存预热。
    ///     </para>
    /// </summary>
    public static string ResolveLauncherVersion() =>
        Volatile.Read(ref _onlineVersion) ?? ResolveLocalLauncherVersion() ?? FallbackLauncherVersion;


    /// <summary>
    ///     同上，但会主动去查一次（带 6 小时缓存）。能 await 的地方优先用这个。
    /// </summary>
    public static async Task<string> ResolveLauncherVersionAsync(CancellationToken cancellationToken = default)
    {
        var online = await FetchLatestLauncherVersionAsync(cancellationToken).ConfigureAwait(false);
        return online ?? ResolveLocalLauncherVersion() ?? FallbackLauncherVersion;
    }


    /// <summary>
    ///     按优先级排列的候选版本。握手被服务端以「版本不受支持」拒掉时依次往后试。
    ///     <para>
    ///         长度最多 2：首选版本 + <see cref="EscalatedLauncherVersion" />。
    ///         每次重试都要重连一次，多列几个只会白白撞上服务端的连接限流。
    ///     </para>
    /// </summary>
    /// <param name="preferred">首选版本；留空则用 <see cref="ResolveLauncherVersion" />。</param>
    public static IReadOnlyList<string> GetLauncherVersionCandidates(string? preferred = null)
    {
        var first = string.IsNullOrWhiteSpace(preferred) ? ResolveLauncherVersion() : preferred;
        return first == EscalatedLauncherVersion
            ? [first]
            : [first, EscalatedLauncherVersion];
    }

}
