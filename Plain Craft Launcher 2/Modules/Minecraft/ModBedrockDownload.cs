using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Link.Bedrock;

namespace PCL;

/// <summary>
///     基岩版安装包的下载与本地存放。
///     <para>
///         目录结构（默认在启动器数据目录下，删启动器时一起走）：
///         <c>Bedrock/packages</c> 存下载到的原包，<c>Bedrock/versions</c> 存解包后的 GDK 版本，
///         <c>Bedrock/extracted</c> 存 UWP 包解出来的、等待注册的目录。
///     </para>
///     <para>
///         根目录可以用 <c>Config.Launch.BedrockDownloadDirectory</c> 换位置
///         （GDK 解包后 2 GB 起，放系统盘容易撑爆），留空时用 <c>%LOCALAPPDATA%\PCLCE\Bedrock</c>。
///     </para>
/// </summary>
public static class ModBedrockDownload
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private static IReadOnlyList<BedrockVersionEntry> _cache = [];

    public static string RootDirectory
    {
        get
        {
            var custom = Config.Launch.BedrockDownloadDirectory;
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Paths.SharedLocalData, "Bedrock")
                : custom.Trim();
        }
    }
    public static string PackagesDirectory => Path.Combine(RootDirectory, "packages");
    public static string VersionsDirectory => Path.Combine(RootDirectory, "versions");
    public static string ExtractedDirectory => Path.Combine(RootDirectory, "extracted");

    /// <summary>最近一次拉到的版本列表（可能为空）。</summary>
    public static IReadOnlyList<BedrockVersionEntry> Cached => _cache;

    /// <summary>拉取版本列表并缓存。force 为 false 且已有缓存时直接返回。</summary>
    public static async Task<IReadOnlyList<BedrockVersionEntry>> LoadAsync(bool force = false,
        CancellationToken token = default)
    {
        if (!force && _cache.Count > 0) return _cache;

        _cache = await BedrockVersionCatalog.FetchAsync(token).ConfigureAwait(false);
        ModBase.Log($"[Bedrock] 版本列表已更新，共 {_cache.Count} 条");
        return _cache;
    }

    /// <summary>这个版本的原包是否已经在本地。</summary>
    public static bool IsDownloaded(BedrockVersionEntry entry) => File.Exists(GetPackagePath(entry));

    /// <summary>原包在本地应该叫什么。扩展名从地址推断，推断不出时按形态给默认值。</summary>
    public static string GetPackagePath(BedrockVersionEntry entry)
    {
        Directory.CreateDirectory(PackagesDirectory);

        var extension = entry.Kind == BedrockPackageKind.Gdk ? ".msixvc" : ".appxbundle";
        if (entry.HasDirectUrl)
        {
            var fromUrl = Path.GetExtension(new Uri(entry.Meta).AbsolutePath);
            if (fromUrl is ".msixvc" or ".appx" or ".appxbundle" or ".msix" or ".msixbundle")
                extension = fromUrl;
        }

        // 键里可能带括号等字符，统一清洗成安全文件名
        var safeKey = string.Concat(entry.Key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(PackagesDirectory, $"{safeKey}{extension}");
    }

    /// <summary>把版本解析成真实下载地址。GDK 直接用列表里的地址，UWP 走 FE3。</summary>
    public static async Task<string?> ResolveUrlAsync(BedrockVersionEntry entry)
    {
        if (entry.HasDirectUrl) return entry.Meta;

        var url = await ModBedrockUwp.ResolveDownloadUrlAsync(entry.Meta).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(url))
            ModBase.Log($"[Bedrock] 未能解析 {entry.Key} 的下载地址（UpdateID {entry.Meta}）",
                ModBase.LogLevel.Normal);
        return url;
    }

    /// <summary>下载到本地，返回文件路径。已有同名文件且大小一致时直接复用。</summary>
    public static async Task<string> DownloadAsync(BedrockVersionEntry entry, string url,
        IProgress<(long Received, long Total)>? progress = null, CancellationToken token = default)
    {
        var target = GetPackagePath(entry);
        var temp = target + ".downloading";

        using var response = await Client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        long received = 0;

        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var destination = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                    progress?.Report((received, total));
                }
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);

            ModBase.Log($"[Bedrock] 已下载 {entry.Key}（{received / 1024d / 1024d:0.0} MB）");
            return target;
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响结果
            }
        }
    }

    /// <summary>下载后按官方 MD5 校验。列表没给 MD5 时直接放行。</summary>
    public static bool VerifyMd5(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;

        try
        {
            using var stream = File.OpenRead(path);
            var hash = System.Security.Cryptography.MD5.HashData(stream);
            var actual = Convert.ToHexString(hash);
            var ok = string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!ok)
                ModBase.Log($"[Bedrock] MD5 不匹配：期望 {expected}，实际 {actual}", ModBase.LogLevel.Normal);
            return ok;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Bedrock] MD5 校验失败", ModBase.LogLevel.Normal);
            return false;
        }
    }
}
