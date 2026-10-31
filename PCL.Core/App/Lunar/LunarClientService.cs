using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;

namespace PCL.Core.App.Lunar;

/// <summary>
///     Lunar Client 启动物料里的一条附件。
/// </summary>
/// <param name="Name">文件名，落到目标目录时用这个。</param>
/// <param name="Sha1">服务端给的 SHA-1，用于校验与增量判断。</param>
/// <param name="Url">下载地址。</param>
/// <param name="Type"><c>CLASS_PATH</c> / <c>EXTERNAL_FILE</c> / <c>NATIVES</c>。</param>
/// <param name="Size">字节数，仅用于显示进度。</param>
public sealed record LunarArtifact(string Name, string Sha1, string Url, string Type, long Size)
{
    public bool IsClassPath => string.Equals(Type, "CLASS_PATH", StringComparison.OrdinalIgnoreCase);
    public bool IsNatives => string.Equals(Type, "NATIVES", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     某个子版本对应的原版资源索引。
///     <para>
///         它**不在** <c>/launcher/launch</c> 的响应里，而是在
///         <c>/launcher/metadata/versions/lunar</c> 的
///         <c>versions[].subversions[].assets</c> 中。启动器把它塞进
///         <c>--assetIndex</c>，Genesis 再转成原版的 <c>--assets_index_name</c>。
///     </para>
/// </summary>
/// <param name="Id">索引名，1.8.9 是 <c>1.8</c>。</param>
/// <param name="Url">Mojang 的索引文件地址。</param>
/// <param name="Sha1">索引文件的 SHA-1。</param>
public sealed record LunarAssetIndex(string Id, string Url, string Sha1);

/// <summary>
///     一份「索引 + 文件 CDN」形式的物料源。
///     <para>
///         官方启动器里 <c>textures</c> 与 <c>ui.assets</c> 都是这个形状：
///         先去 <paramref name="IndexUrl" /> 下一个纯文本索引（每行
///         <c>&lt;相对路径&gt; &lt;sha1&gt; [大小] [mtime]</c>），
///         再按 <paramref name="BaseUrl" /> + sha1 逐个取文件。
///     </para>
/// </summary>
/// <param name="IndexUrl">索引文件地址。</param>
/// <param name="IndexSha1">索引文件的 SHA-1，用来判断本地那份还能不能用。</param>
/// <param name="BaseUrl">文件 CDN 前缀，拼上 sha1 就是完整地址。</param>
public sealed record LunarIndexedSource(string IndexUrl, string IndexSha1, string BaseUrl);

/// <summary>
///     <c>licenses</c> 目录里的一份许可文件。
/// </summary>
/// <param name="File">相对 <c>~/.lunarclient/licenses/</c> 的路径。</param>
/// <param name="Sha1">内容 SHA-1。</param>
/// <param name="Url">下载地址。</param>
/// <param name="Size">字节数，仅用于进度显示。</param>
public sealed record LunarLicense(string File, string Sha1, string Url, long Size);

/// <summary>
///     <c>POST /launcher/launch</c> 的解析结果。
/// </summary>
public sealed class LunarLaunchData
{
    public required string MainClass { get; init; }
    public required IReadOnlyList<LunarArtifact> Artifacts { get; init; }

    /// <summary>防篡改/灰度令牌，原样透传给游戏。</summary>
    public string CanaryToken { get; init; } = string.Empty;

    /// <summary>
    ///     JRE 目录的校验和，同时也是 <c>~/.lunarclient/jre/</c> 下的子目录名。
    ///     有了它就不必自己决定该用哪个 Java。
    /// </summary>
    public string JreFolderChecksum { get; init; } = string.Empty;

    /// <summary>JRE 压缩包地址，本机没装过对应版本时用它补齐。</summary>
    public string JreDownloadUrl { get; init; } = string.Empty;

    /// <summary>JRE 压缩包的备用地址，官方 CDN 拉不动时用。</summary>
    public string JreDownloadFallbackUrl { get; init; } = string.Empty;

    /// <summary>压缩包内 <c>javaw.exe</c> 的相对路径，仅用于确认解压结果。</summary>
    public IReadOnlyList<string> JreExecutablePath { get; init; } = [];

    /// <summary>
    ///     Lunar 官方启动器额外传给 JVM 的参数。
    ///     <para>
    ///         1.21+ 与 26.x 必需：<c>--add-opens java.base/java.io=ALL-UNNAMED</c>、
    ///         <c>--enable-native-access=ALL-UNNAMED</c>、<c>--add-modules jdk.naming.dns</c>
    ///         这些缺了会在启动早期就报模块访问错误。1.8.9 也有几条
    ///         （<c>-Dlunar.webosr.url</c> 就是游戏内 UI 的入口），照抄最稳。
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> JvmExtraArguments { get; init; } = [];

    /// <summary>资源索引名，1.8.9 是 <c>1.8</c>。</summary>
    public string AssetIndex { get; init; } = string.Empty;

    /// <summary>资源索引的完整信息（含下载地址），拿不到时为 <c>null</c>。</summary>
    public LunarAssetIndex? AssetIndexInfo { get; init; }

    /// <summary>
    ///     界面资源的 SHA-1。Lunar 把界面资源放在 <c>ui/&lt;sha1&gt;/</c> 下，
    ///     <c>--uiDir</c> 必须指到那一层——只给 <c>ui/</c> 的话游戏内菜单整块不显示。
    /// </summary>
    public string UiSha1 { get; init; } = string.Empty;

    /// <summary>界面资源压缩包地址。</summary>
    public string UiUrl { get; init; } = string.Empty;

    /// <summary>
    ///     界面资源的索引式物料源（<c>ui.assets</c>）。
    ///     <para>
    ///         官方启动器解压完 <c>source.zip</c> 之后还会跑一遍这个索引下载，
    ///         把 HUD、模组菜单那些运行时才用到的资源补齐。只解 zip 也能进游戏，
    ///         但界面里会缺图标。
    ///     </para>
    /// </summary>
    public LunarIndexedSource? UiAssets { get; init; }

    /// <summary>
    ///     皮肤 / 披风 / 表情这些外观资源的索引式物料源。
    ///     落在 <c>~/.lunarclient/textures/</c>，缺了游戏内其他玩家是默认皮肤。
    /// </summary>
    public LunarIndexedSource? Textures { get; init; }

    /// <summary>许可文件清单，落在 <c>~/.lunarclient/licenses/</c>。</summary>
    public IReadOnlyList<LunarLicense> Licenses { get; init; } = [];

    public IReadOnlyList<LunarArtifact> ClassPathArtifacts => Artifacts.Where(x => x.IsClassPath).ToList();
    public IReadOnlyList<LunarArtifact> NativesArtifacts => Artifacts.Where(x => x.IsNatives).ToList();
    public IReadOnlyList<LunarArtifact> ExternalArtifacts =>
        Artifacts.Where(x => !x.IsClassPath && !x.IsNatives).ToList();
}

/// <summary>
///     拼接 Lunar Client 启动命令所需的全部外部输入。
/// </summary>
public sealed class LunarLaunchOptions
{
    /// <summary>Lunar 数据根目录，通常是 <c>%USERPROFILE%\.lunarclient</c>。</summary>
    public required string LunarRoot { get; init; }

    /// <summary>游戏实例目录（<c>--gameDir</c>）。</summary>
    public required string GameDirectory { get; init; }

    public required string VersionName { get; init; }
    public required string PlayerName { get; init; }
    public required string PlayerUuid { get; init; }

    /// <summary>Minecraft 的 accessToken，正版验证就靠它。</summary>
    public required string AccessToken { get; init; }

    public string XboxUid { get; init; } = string.Empty;
    public int AllocatedMemoryMb { get; init; } = 2048;
    public int Width { get; init; } = 854;
    public int Height { get; init; } = 480;
    public bool Fullscreen { get; init; }
    public string? Server { get; init; }
    public string LauncherVersion { get; init; } = "3.3.0";
    public string InstallationId { get; init; } = string.Empty;

    /// <summary>
    ///     本地 IPC 端口（<c>--ipcPort</c>）。
    ///     <para>
    ///         0 表示不启用：游戏会跳过社交面板与实时设置同步，账号过期也不会来要续期。
    ///         非 0 时游戏会连 <c>ws://127.0.0.1:&lt;port&gt;</c>，
    ///         由 <see cref="LunarIpcServer" /> 或 Lunar 启动器应答。
    ///     </para>
    /// </summary>
    public int IpcPort { get; init; }

    /// <summary>
    ///     额外 JVM 参数，会被插在<b>所有</b>内置与官方参数之后。
    ///     <para>
    ///         JVM 对重复参数取最后一次，所以放最后才能让用户写的 <c>-Xmx</c>、
    ///         <c>-XX:+UseZGC</c> 盖掉前面那些默认值。
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];
}

/// <summary>
///     Lunar Client 的物料获取与启动命令拼装。
///     <para>
///         这里刻意**不做**下载以外的任何决策：版本、模块、分支由调用方给定，
///         物料清单完全以服务端返回为准。协议细节见 <c>docs/LUNAR.md</c>。
///     </para>
/// </summary>
public static class LunarClientService
{
    private const string ApiBase = "https://api.lunarclientprod.com/launcher";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Lunar 数据根目录。</summary>
    public static string DefaultLunarRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lunarclient");

    /// <summary>
    ///     游戏真正读的那份设置目录，**恒为 <c>%USERPROFILE%\.lunarclient</c>**。
    ///     <para>
    ///         物料根（<see cref="DefaultLunarRoot" />，或设置里自定义的目录）只决定
    ///         <c>ui/</c>、<c>offline/multiver/</c>、<c>textures/</c> 这些资源从哪来；
    ///         <c>settings/game/accounts.json</c>、<c>settings/launcher.json</c>、
    ///         <c>launcher-cache/</c> 一律在用户目录下，跟物料根没有关系。
    ///         游戏日志里的 <c>[Bridge] Found JIT dir: C:\Users\xxx\.lunarclient\jit</c>
    ///         就是证据 —— 资源走 <c>--workingDirectory</c>，设置走用户目录。
    ///     </para>
    ///     <para>
    ///         踩过的坑：物料根设成 <c>D:\pcllitelunar</c> 之后，账户库的读写还跟着物料根走，
    ///         于是 PCL 一直在改一个游戏根本不看的文件。续期「成功」写进了 D 盘那份，
    ///         游戏读的 <c>~/.lunarclient</c> 那份 token 永远是过期的，它每秒重试一次，
    ///         界面就卡在 <c>Connecting...</c>。
    ///     </para>
    /// </summary>
    public static string SettingsRoot => DefaultLunarRoot;

    /// <summary>物料缓存目录，与 Lunar 启动器自己用的位置一致，可复用已下好的文件。</summary>
    public static string MultiverDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "offline", "multiver");

    public static string NativesDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "offline", "multiver", "natives");

    public static string AssetsDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "shared", "assets");

    /// <summary>外观资源目录（<c>--texturesDir</c>），索引式下载的落点。</summary>
    public static string TexturesDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "textures");

    /// <summary>许可文件目录，官方启动器每次启动都会对齐一遍。</summary>
    public static string LicensesDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "licenses");

    /// <summary>
    ///     启动器缓存目录。索引文件（<c>texturesIndex.txt</c>、
    ///     <c>uiIndex-&lt;sha1&gt;.txt</c>）和 <c>installation-id</c> 都在这儿。
    /// </summary>
    public static string LauncherCacheDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "launcher-cache");

    /// <summary>游戏日志目录，<c>config.xml</c> 与 <c>latest.log</c> 的落点。</summary>
    public static string GameLogsDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "logs", "game");

    /// <summary>游戏设置目录，<c>accounts.json</c> 就在这一层下面。</summary>
    public static string GameSettingsDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "settings", "game");

    /// <summary>JRE 根目录，下面按 <c>folderChecksum</c> 分版本。</summary>
    public static string JreDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "jre");

    /// <summary>
    ///     界面资源目录。
    ///     <para>
    ///         Lunar 把界面资源按 SHA-1 分目录放在 <c>ui/&lt;sha1&gt;/</c> 下，
    ///         <c>--uiDir</c> 必须指到这一层。只给 <c>ui/</c> 的话 Genesis 找不到
    ///         <c>index.html</c>，游戏内菜单整块不显示（HUD 和模组都还在，很容易误判）。
    ///     </para>
    ///     <para>
    ///         服务端换了 ui 版本、而本地那份还没重下时，<c>ui/&lt;sha1&gt;</c> 是不存在的，
    ///         指过去等于把背景、按钮贴图全丢掉（界面会退化成纯文字）。这种情况下退回到
    ///         <c>ui/</c> 下唯一的那份：资源版本对不上顶多是细节有出入，比整块没有强。
    ///     </para>
    /// </summary>
    public static string UiDirectory(string lunarRoot, string uiSha1)
    {
        var root = Path.Combine(lunarRoot, "ui");
        if (!string.IsNullOrWhiteSpace(uiSha1))
        {
            var exact = Path.Combine(root, uiSha1);
            if (Directory.Exists(exact)) return exact;
        }

        try
        {
            if (Directory.Exists(root))
            {
                var candidates = Directory.GetDirectories(root);
                if (candidates.Length == 1) return candidates[0];
                if (candidates.Length > 1)
                    return candidates.OrderByDescending(Directory.GetLastWriteTimeUtc).First();
            }
        }
        catch (IOException)
        {
            // 目录读不了就走下面的老路，让游戏自己决定怎么退化
        }

        return root;
    }

    /// <summary>
    ///     本机是否装过 Lunar Client。
    /// </summary>
    public static bool IsInstalled(string? lunarRoot = null)
    {
        var root = lunarRoot ?? DefaultLunarRoot;
        return Directory.Exists(Path.Combine(root, "offline", "multiver"))
               || Directory.Exists(Path.Combine(root, "shared", "assets"));
    }

    /// <summary>
    ///     向 Lunar 服务端要一份启动物料清单。
    ///     <para>
    ///         这个接口**不需要鉴权**——正版验证发生在游戏进程内，用的是命令行里的
    ///         Minecraft accessToken。所以这里不带任何 Authorization 头。
    ///     </para>
    /// </summary>
    public static async Task<LunarLaunchData> FetchLaunchDataAsync(
        string subVersion,
        string module = "lunar",
        string branch = "master",
        string? installationId = null,
        string? profileId = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        // 同一次启动里两个接口要看到同一个 installation_id，否则服务端会当成两次安装
        var resolvedInstallationId = installationId ?? Guid.NewGuid().ToString();

        var body = new JsonObject
        {
            ["installation_id"] = resolvedInstallationId,
            ["os"] = "win32",
            ["os_release"] = Environment.OSVersion.Version.ToString(),
            ["arch"] = Environment.Is64BitOperatingSystem ? "x64" : "ia32",
            ["launcher_version"] = "3.3.0",
            ["version"] = subVersion,
            ["branch"] = branch,
            ["args"] = new JsonArray(),
            ["module"] = module,
            // 服务端用 zod 校验，这三个字段写错会直接 400
            ["canary_preference"] = "NEUTRAL",
            ["profile"] = new JsonObject
            {
                ["id"] = profileId ?? Guid.Empty.ToString(),
                ["name"] = profileName ?? "Player"
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/launch")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "Lunar Client Launcher v3.3.0");

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Lunar 启动接口返回 {(int)response.StatusCode}：{Truncate(text, 300)}");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Lunar 启动接口返回的不是合法 JSON：{Truncate(text, 200)}", ex);
        }

        if (root?["success"]?.GetValue<bool>() != true)
            throw new InvalidOperationException($"Lunar 启动接口拒绝请求：{Truncate(text, 300)}");

        var launchTypeData = root["launchTypeData"]
                             ?? throw new InvalidOperationException("Lunar 响应里缺少 launchTypeData。");

        var mainClass = launchTypeData["mainClass"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(mainClass))
            throw new InvalidOperationException("Lunar 响应里缺少 mainClass。");

        var artifacts = new List<LunarArtifact>();
        if (launchTypeData["artifacts"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is not JsonObject item) continue;
                var name = item["name"]?.GetValue<string>();
                var url = item["url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;

                artifacts.Add(new LunarArtifact(
                    name,
                    item["sha1"]?.GetValue<string>() ?? string.Empty,
                    url,
                    item["type"]?.GetValue<string>() ?? "EXTERNAL_FILE",
                    item["size"]?.GetValue<long>() ?? 0));
            }
        }

        if (artifacts.Count == 0)
            throw new InvalidOperationException("Lunar 响应里没有任何附件，无法启动。");

        // --assetIndex 的值来自版本元数据，不在 launch 响应里；拿不到就留空
        var assetIndex = await FetchAssetIndexAsync(subVersion, branch, resolvedInstallationId, cancellationToken)
            .ConfigureAwait(false);

        return new LunarLaunchData
        {
            MainClass = mainClass,
            Artifacts = artifacts,
            CanaryToken = root["canaryToken"]?.GetValue<string>() ?? string.Empty,
            JreFolderChecksum = root["jre"]?["folderChecksum"]?.GetValue<string>() ?? string.Empty,
            JreDownloadUrl = root["jre"]?["download"]?["url"]?.GetValue<string>() ?? string.Empty,
            JreDownloadFallbackUrl = root["jre"]?["download"]?["fallbackUrl"]?.GetValue<string>() ?? string.Empty,
            JreExecutablePath = ReadStringArray(root["jre"]?["executablePathInArchive"]),
            JvmExtraArguments = ReadStringArray(root["jre"]?["extraArguments"]),
            AssetIndex = assetIndex?.Id ?? string.Empty,
            AssetIndexInfo = assetIndex,
            UiSha1 = root["ui"]?["sourceSha1"]?.GetValue<string>() ?? string.Empty,
            UiUrl = root["ui"]?["sourceUrl"]?.GetValue<string>() ?? string.Empty,
            UiAssets = ReadIndexedSource(root["ui"]?["assets"]),
            Textures = ReadIndexedSource(root["textures"]),
            Licenses = ReadLicenses(root["licenses"])
        };
    }

    /// <summary>
    ///     读一份「索引 + CDN」形式的物料源。三个字段缺任何一个都当成没有。
    /// </summary>
    private static LunarIndexedSource? ReadIndexedSource(JsonNode? node)
    {
        if (node is null) return null;

        var indexUrl = node["indexUrl"]?.GetValue<string>();
        var indexSha1 = node["indexSha1"]?.GetValue<string>();
        var baseUrl = node["baseUrl"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(indexUrl)
            || string.IsNullOrWhiteSpace(indexSha1)
            || string.IsNullOrWhiteSpace(baseUrl))
            return null;

        return new LunarIndexedSource(indexUrl, indexSha1, baseUrl);
    }

    private static IReadOnlyList<LunarLicense> ReadLicenses(JsonNode? node)
    {
        if (node is not JsonArray array) return [];

        var result = new List<LunarLicense>();
        foreach (var item in array)
        {
            if (item is not JsonObject entry) continue;

            var file = entry["file"]?.GetValue<string>();
            var url = entry["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(url)) continue;

            result.Add(new LunarLicense(
                file,
                entry["sha1"]?.GetValue<string>() ?? string.Empty,
                url,
                entry["size"]?.GetValue<long>() ?? 0));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array) return [];
        return array
            .Select(x => x?.GetValue<string>())
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToList();
    }

    /// <summary>
    ///     拉版本元数据，从中取出某个子版本的原版资源索引。
    ///     <para>
    ///         <c>/launcher/launch</c> 只给物料，<c>--assetIndex</c> 要的值在
    ///         <c>/launcher/metadata/versions/lunar</c> 里：每个 subversion 带一份
    ///         <c>assets = { id, url, sha1 }</c>。这个接口要求 <c>installation_id</c>
    ///         查询参数，缺了会 400。
    ///     </para>
    /// </summary>
    /// <returns>拿不到就返回 <c>null</c>，调用方按「没有资源索引」处理。</returns>
    public static async Task<LunarAssetIndex?> FetchAssetIndexAsync(
        string subVersion,
        string branch = "master",
        string? installationId = null,
        CancellationToken cancellationToken = default)
    {
        var id = installationId ?? Guid.NewGuid().ToString();
        var url = $"{ApiBase}/metadata/versions/lunar?branch={Uri.EscapeDataString(branch)}" +
                  $"&installation_id={Uri.EscapeDataString(id)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Lunar Client Launcher v3.3.0");

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var versions = JsonNode.Parse(text)?["versions"] as JsonArray;
            if (versions is null) return null;

            // 结构是 versions[].subversions[]，同名子版本只出现在一个父版本下
            foreach (var version in versions)
            {
                if (version?["subversions"] is not JsonArray subversions) continue;

                foreach (var subversion in subversions)
                {
                    if (subversion?["id"]?.GetValue<string>() != subVersion) continue;

                    var assets = subversion["assets"];
                    var assetId = assets?["id"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(assetId)) return null;

                    return new LunarAssetIndex(
                        assetId,
                        assets?["url"]?.GetValue<string>() ?? string.Empty,
                        assets?["sha1"]?.GetValue<string>() ?? string.Empty);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // 资源索引拿不到不致命：游戏照样能起，只是可能缺贴图与音效
        }

        return null;
    }

    /// <summary>
    ///     确保 <c>assets/indexes/&lt;id&gt;.json</c> 存在。
    ///     <para>
    ///         只补这一个索引文件，资源对象由 <see cref="EnsureAssetObjectsAsync" /> 处理。
    ///     </para>
    ///     <para>
    ///         索引本身在 <c>launchermeta.mojang.com</c> 上，走
    ///         <see cref="DownloadSource.LauncherOrMeta" /> 按「文件下载源」设置决定
    ///         官方 / BMCLAPI 的先后。
    ///     </para>
    /// </summary>
    public static async Task EnsureAssetIndexAsync(
        LunarAssetIndex index,
        string assetsDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(index.Url)) return;

        var indexesDirectory = Path.Combine(assetsDirectory, "indexes");
        Directory.CreateDirectory(indexesDirectory);

        var destination = Path.Combine(indexesDirectory, index.Id + ".json");
        if (await IsUpToDateAsync(destination, index.Sha1, cancellationToken).ConfigureAwait(false)) return;

        await DownloadFirstAsync(DownloadSource.LauncherOrMeta(index.Url), destination, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     原版资源对象的 CDN。启动器里写死成 <c>uze</c> 这个常量。
    /// </summary>
    private const string MinecraftResourcesBase = "https://resources.download.minecraft.net";

    /// <summary>
    ///     资源对象的候选地址。走 <see cref="DownloadSource" />，跟「下载 → 文件下载源」
    ///     设置完全一致：优先镜像时 BMCLAPI 在前，优先官方时官方在前，自动模式看探测结果。
    ///     <para>
    ///         以前这里硬编码了一份 <c>bmclapi2</c> 地址，既不跟设置走，
    ///         改镜像域名时还得记住改两处。
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> AssetObjectUrls(string prefix, string hash) =>
        DownloadSource.Assets($"{MinecraftResourcesBase}/{prefix}/{hash}");

    /// <summary>
    ///     按资源索引补齐 <c>assets/objects/</c> 下的文件。
    ///     <para>
    ///         这一步照抄官方启动器的 <c>assets</c> stage：
    ///         索引里每个对象按 <c>&lt;hash 前两位&gt;/&lt;hash&gt;</c> 落在 objects 下，
    ///         地址是 <c>https://resources.download.minecraft.net/&lt;前两位&gt;/&lt;hash&gt;</c>。
    ///         少了这一步，游戏进去是没贴图没声音的。
    ///     </para>
    ///     <para>
    ///         已经存在的文件按 SHA-1 判断，命中就跳过 —— 装过 Lunar 启动器的机器上
    ///         这份数据本来就是齐的，这里只会补真正缺的那几个。
    ///     </para>
    /// </summary>
    /// <param name="index">资源索引。必须已经通过 <see cref="EnsureAssetIndexAsync" /> 落盘。</param>
    /// <param name="assetsDirectory"><c>--assetsDir</c> 指向的目录。</param>
    /// <param name="progress">进度回调，参数是「已完成 / 总数」。</param>
    /// <param name="verify">
    ///     是否校验 SHA-1。这一个索引动辄两三千个文件、好几百 MB，
    ///     全量算哈希要十几秒；关掉后只判断文件在不在。
    /// </param>
    public static async Task EnsureAssetObjectsAsync(
        LunarAssetIndex index,
        string assetsDirectory,
        IProgress<(int Done, int Total)>? progress = null,
        bool verify = true,
        CancellationToken cancellationToken = default)
    {
        var indexFile = Path.Combine(assetsDirectory, "indexes", index.Id + ".json");
        if (!File.Exists(indexFile)) return;

        // 索引里同一个 hash 可能被多个路径引用，去重后才是真正要下的文件数
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(indexFile, cancellationToken)
                .ConfigureAwait(false));
            if (root?["objects"] is not JsonObject objects) return;

            foreach (var (_, value) in objects)
            {
                if (value?["hash"]?.GetValue<string>() is { Length: > 0 } hash) hashes.Add(hash);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 索引坏了就当没有，别让启动流程跟着挂
            return;
        }

        if (hashes.Count == 0) return;

        var total = hashes.Count;
        var done = 0;
        var failures = new ConcurrentBag<string>();
        progress?.Report((0, total));

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = DownloadParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(hashes, options, async (hash, ct) =>
        {
            var prefix = hash[..2];
            var destination = Path.Combine(assetsDirectory, "objects", prefix, hash);

            var upToDate = verify
                ? await IsUpToDateAsync(destination, hash, ct).ConfigureAwait(false)
                : File.Exists(destination);

            if (!upToDate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await DownloadFirstAsync(AssetObjectUrls(prefix, hash), destination, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    failures.Add($"{hash[..8]}（{ex.Message}）");
                }
            }

            var current = Interlocked.Increment(ref done);
            progress?.Report((current, total));
        }).ConfigureAwait(false);

        ThrowIfAnyFailed(failures, "原版资源");
    }

    /// <summary>
    ///     确保界面资源就位，返回应该传给 <c>--uiDir</c> 的路径。
    ///     <para>
    ///         已经存在的直接复用（Lunar 启动器下载过的话就在这儿了），
    ///         没有才下载并解压。
    ///     </para>
    /// </summary>
    public static async Task<string> EnsureUiAsync(
        LunarLaunchData data,
        string lunarRoot,
        CancellationToken cancellationToken = default)
    {
        var target = UiDirectory(lunarRoot, data.UiSha1);
        if (File.Exists(Path.Combine(target, "index.html"))) return target;

        if (string.IsNullOrWhiteSpace(data.UiUrl))
        {
            // 没地址也没现成资源，只能把旧路径交出去，至少不会崩
            return target;
        }

        Directory.CreateDirectory(target);
        var archive = target + ".lunardl.zip";

        try
        {
            await DownloadAsync(data.UiUrl, archive, cancellationToken).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true);
        }
        catch (InvalidDataException)
        {
            // 解不开就算了，让游戏照常起，界面缺失比启动失败强
        }
        finally
        {
            try
            {
                if (File.Exists(archive)) File.Delete(archive);
            }
            catch (IOException)
            {
                // 删不掉留到下次覆盖
            }
        }

        return target;
    }

    /// <summary>
    ///     把附件补齐到本地。
    ///     <para>
    ///         并发跑：61 个附件里有几个几十 MB 的 jar，串行算 SHA-1 要好几秒，
    ///         而且大部分时间都耗在等磁盘上。并发数按核数取，封顶 8 ——
    ///         再高也不会更快，只会把磁盘队列塞满。
    ///     </para>
    /// </summary>
    /// <param name="targetDirectory">CLASS_PATH 与 EXTERNAL_FILE 的落点。</param>
    /// <param name="progress">已完成的条数与总条数。</param>
    /// <param name="verify">
    ///     是否校验 SHA-1。关掉时只判断文件在不在，不算哈希——
    ///     省掉大文件的哈希计算，代价是文件损坏了发现不了。
    /// </param>
    public static async Task EnsureArtifactsAsync(
        LunarLaunchData data,
        string targetDirectory,
        IProgress<(int Done, int Total)>? progress = null,
        bool verify = true,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDirectory);

        var files = data.Artifacts.Where(x => !x.IsNatives).ToList();
        var done = 0;
        var failures = new ConcurrentBag<string>();
        progress?.Report((0, files.Count));

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = DownloadParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(files, options, async (artifact, ct) =>
        {
            var destination = Path.Combine(targetDirectory, artifact.Name);

            var needsDownload = verify
                ? !await IsUpToDateAsync(destination, artifact.Sha1, ct).ConfigureAwait(false)
                : !File.Exists(destination);

            if (needsDownload)
            {
                try
                {
                    await DownloadAsync(artifact.Url, destination, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    // 单个文件失败不中断整段：全部试完之后再一起报，
                    // 下次启动会接着补没下好的那些
                    failures.Add($"{artifact.Name}（{ex.Message}）");
                }
            }

            var current = Interlocked.Increment(ref done);
            progress?.Report((current, files.Count));
        }).ConfigureAwait(false);

        ThrowIfAnyFailed(failures, "物料");
    }

    /// <summary>
    ///     解压原生库到 natives 目录。Lunar 的 natives 是 zip，解出来的 dll 直接放在根下。
    /// </summary>
    public static async Task ExtractNativesAsync(
        LunarLaunchData data,
        string cacheDirectory,
        string nativesDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(cacheDirectory);
        Directory.CreateDirectory(nativesDirectory);

        var failures = new ConcurrentBag<string>();

        foreach (var artifact in data.NativesArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = Path.Combine(cacheDirectory, artifact.Name);

            try
            {
                if (!await IsUpToDateAsync(archive, artifact.Sha1, cancellationToken).ConfigureAwait(false))
                    await DownloadAsync(artifact.Url, archive, cancellationToken).ConfigureAwait(false);

                ZipFile.ExtractToDirectory(archive, nativesDirectory, overwriteFiles: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                           or TaskCanceledException or InvalidDataException)
            {
                failures.Add($"{artifact.Name}（{ex.Message}）");
            }
        }

        ThrowIfAnyFailed(failures, "原生库");
    }

    /// <summary>
    ///     把并发下载攒下来的失败一次性抛出来。
    ///     <para>
    ///     只报前 5 个：几百个文件同时失败时，全列出来用户也看不完，
    ///     而且真正的原因（断网、代理挂了）看一个就够了。
    ///     </para>
    /// </summary>
    private static void ThrowIfAnyFailed(ConcurrentBag<string> failures, string what)
    {
        if (failures.IsEmpty) return;

        var sample = string.Join("、", failures.Take(5));
        var more = failures.Count > 5 ? $"……等 {failures.Count} 个" : string.Empty;
        throw new InvalidOperationException(
            $"{what}下载失败：{sample}{more}。已下好的不会重下，检查网络后重试即可。");
    }

    /// <summary>
    ///     按 <c>jre.folderChecksum</c> 找到 Lunar 自带的 Java。找不到就返回 <c>null</c>。
    /// </summary>
    public static string? FindBundledJava(string lunarRoot, string folderChecksum)
    {
        if (string.IsNullOrWhiteSpace(folderChecksum)) return null;

        var directory = Path.Combine(lunarRoot, "jre", folderChecksum);
        if (!Directory.Exists(directory)) return null;

        // 目录里只有一层 <发行版>-<版本>/bin/javaw.exe
        foreach (var candidate in Directory.EnumerateFiles(directory, "javaw.exe", SearchOption.AllDirectories))
        {
            if (candidate.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    /// <summary>
    ///     确保 <c>jre/&lt;checksum&gt;/</c> 下有可用的 Java，返回 <c>javaw.exe</c> 的完整路径。
    ///     <para>
    ///         不同 Minecraft 版本要的 JRE 不一样：1.8.9～1.20.1 是 Java 17，
    ///         1.20.6～1.21.11 是 Java 21，26.x 是 Java 25。Lunar 官方启动器只下载
    ///         你玩过的那个版本对应的 JRE，所以「在 PCL 里换了个没玩过的版本」
    ///         大概率本机缺这一份 —— 这里按响应里的地址补上。
    ///     </para>
    /// </summary>
    /// <returns>找不到也下不下来时返回 <c>null</c>，由调用方决定怎么报错。</returns>
    public static async Task<string?> EnsureJavaAsync(
        LunarLaunchData data,
        string lunarRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var existing = FindBundledJava(lunarRoot, data.JreFolderChecksum);
        if (existing is not null) return existing;
        if (string.IsNullOrWhiteSpace(data.JreFolderChecksum)) return null;

        var urls = new[] { data.JreDownloadUrl, data.JreDownloadFallbackUrl }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
        if (urls.Count == 0) return null;

        var target = Path.Combine(lunarRoot, "jre", data.JreFolderChecksum);
        Directory.CreateDirectory(target);
        var archive = target + ".pcljre.zip";

        try
        {
            Exception? lastError = null;
            foreach (var url in urls)
            {
                try
                {
                    progress?.Report($"正在下载 Java 运行时（{data.JreFolderChecksum[..Math.Min(8, data.JreFolderChecksum.Length)]}）…");
                    await DownloadAsync(url, archive, cancellationToken).ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    lastError = ex;
                }
            }

            if (lastError is not null) throw lastError;

            progress?.Report("正在解压 Java 运行时…");
            ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            // 下不下来就返回 null，让上层给出「先用 Lunar 启动器跑一次」的提示
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(archive)) File.Delete(archive);
            }
            catch (IOException)
            {
                // 删不掉留到下次覆盖
            }
        }

        return FindBundledJava(lunarRoot, data.JreFolderChecksum);
    }

    /// <summary>
    ///     拼出完整的启动命令。
    ///     <para>
    ///         Lunar 不是原版那套「classpath 全塞 -cp」的玩法：它由 Genesis 引导层
    ///         自己建类加载器，真正的类路径通过 <c>--ichorClassPath</c> 传进去，
    ///         额外的资源文件走 <c>--ichorExternalFiles</c>。<c>-cp</c> 只负责让
    ///         JVM 能找到 Genesis 主类。
    ///     </para>
    ///     <para>
    ///         <c>--accessToken</c> 就是正版验证的落点，传 PCL 登录拿到的那个即可。
    ///     </para>
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(
        LunarLaunchData data,
        LunarLaunchOptions options,
        string javaPath,
        string cacheDirectory,
        string nativesDirectory)
    {
        var classPath = data.ClassPathArtifacts.ToList();
        var externalFiles = data.ExternalArtifacts.ToList();
        var logsDirectory = Path.Combine(options.LunarRoot, "logs", "game");
        Directory.CreateDirectory(logsDirectory);

        var arguments = new List<string>
        {
            $"-Xmx{options.AllocatedMemoryMb}m",
            $"-Djava.library.path={nativesDirectory}",
            $"-Dlog4j.configurationFile={Path.Combine(logsDirectory, "config.xml")}",
            $"-Dichor.logsFile={Path.Combine(logsDirectory, "ichor-boot.log")}",
            "-Dichor.usingIsolatedProfiles=true",
            "-XX:+DisableAttachMechanism",
            "-XX:-CreateCoredumpOnCrash",
            "-XX:-CreateMinidumpOnCrash"
        };

        // 官方启动器按版本下发的 JVM 参数：1.21+ 的模块访问、26.x 的 ZGC 都在这儿，
        // 漏了会在启动早期直接报错（--enable-native-access 之类）
        if (data.JvmExtraArguments.Count > 0)
            arguments.AddRange(data.JvmExtraArguments);

        // 代理必须在官方参数之后：这些是 -D 系统属性，不冲突，但保持「后写覆盖」的语义
        arguments.AddRange(BuildProxyArguments());

        // 用户自己写的参数放在最末：JVM 对重复参数取最后一次，这样 -Xmx、-XX:+UseZGC
        // 之类的设置才能盖掉上面那些默认值和官方值
        if (options.ExtraJvmArguments.Count > 0)
            arguments.AddRange(options.ExtraJvmArguments);

        // Genesis 主类必须在 JVM 的真实 classpath 上，否则起不来
        arguments.Add("-cp");
        arguments.Add(string.Join(';', classPath.Select(x => Path.Combine(cacheDirectory, x.Name))));
        arguments.Add(data.MainClass);

        void Add(string key, string? value)
        {
            if (value is null) return;
            arguments.Add(key);
            arguments.Add(value);
        }

        // Genesis 用它自己拼类加载器；名字是相对 cacheDirectory 的文件名
        Add("--ichorClassPath", string.Join(',', classPath.Select(x => x.Name)));
        Add("--ichorExternalFiles", string.Join(',', externalFiles.Select(x => x.Name)));

        Add("--version", options.VersionName);
        Add("--launcherVersion", options.LauncherVersion);
        // 必须是 {enabled:[],disabled:[]}：Genesis 会直接 get("enabled").getAsJsonArray()，
        // 传 {} 会在 Main.parse 里抛 NullPointerException
        Add("--launcherFeatureFlags", "{\"enabled\":[],\"disabled\":[]}");
        Add("--installationId", options.InstallationId);
        Add("--overwolfMuid", "not specified");
        Add("--sentryTraceId", "not specified");
        Add("--launchId", Guid.NewGuid().ToString());
        Add("--canaryToken", data.CanaryToken);

        // 正版账号信息：游戏进程靠这几个参数完成验证
        Add("--username", options.PlayerName);
        Add("--uuid", options.PlayerUuid.Replace("-", string.Empty));
        Add("--xuid", options.XboxUid);
        Add("--accessToken", options.AccessToken);
        Add("--userProperties", "{}");

        Add("--assetIndex", string.IsNullOrWhiteSpace(data.AssetIndex) ? null : data.AssetIndex);
        Add("--gameDir", options.GameDirectory);
        Add("--assetsDir", AssetsDirectory(options.LunarRoot));
        Add("--texturesDir", Path.Combine(options.LunarRoot, "textures"));
        Add("--uiDir", UiDirectory(options.LunarRoot, data.UiSha1));
        Add("--webosrDir", nativesDirectory);
        Add("--workingDirectory", ".");
        Add("--classpathDir", cacheDirectory);
        Add("--width", options.Width.ToString());
        Add("--height", options.Height.ToString());
        if (options.Fullscreen) Add("--fullscreen", "true");
        Add("--server", options.Server);

        // 0 表示「没有 IPC 服务」，游戏侧会跳过社交与实时设置同步；
        // 非 0 时游戏会连上来要账号续期（AuthService/RefreshAccount）
        Add("--ipcPort", options.IpcPort.ToString());

        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = options.GameDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    /// <summary>
    ///     把 PCL 当前生效的代理翻成 JVM 系统属性。
    ///     <para>
    ///         <b>这一步不能省。</b>实测 1.17.1（OptiFine 和 Fabric 都一样）：Genesis 打印完
    ///         <c>Launching Minecraft 1.17.1</c> 之后，会在 <c>ClientGameBootstrap</c> 里
    ///         自己下一次东西。它用的是 Java 的 <c>HttpClient</c>，**完全不读系统代理**，
    ///         没有 <c>-Dhttps.proxyHost</c> 就是直连。目标被墙时 TCP 能连上、HTTP 应答永远不来，
    ///         主线程就永久阻塞在 <c>HttpClientImpl.send()</c> 里 ——
    ///         表现是「进程活着、窗口永远不出来、日志停在 Launching Minecraft」。
    ///     </para>
    ///     <para>
    ///         1.8.9 / 1.16.5 不需要这一步（它们的物料已经在本地），所以这个坑只在
    ///         部分版本上出现，很容易被当成「某个版本坏了」。
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> BuildProxyArguments()
    {
        var result = new List<string>();
        try
        {
            // 拿一个真实目标问代理：这样 NoProxy / SystemProxy / CustomProxy 三种模式都覆盖到
            var probe = new Uri("https://api.lunarclientprod.com");
            var proxy = HttpProxyManager.Instance.GetProxy(probe);

            // WebProxy.GetProxy 在没有代理时会把目标原样返回，这种情况不能当成代理
            if (proxy is null || string.IsNullOrWhiteSpace(proxy.Host) ||
                (string.Equals(proxy.Host, probe.Host, StringComparison.OrdinalIgnoreCase) &&
                 proxy.Port == probe.Port))
            {
                // 没有显式代理时退一步：让 JVM 自己去读 Windows 的代理设置
                result.Add("-Djava.net.useSystemProxies=true");
                return result;
            }

            var port = proxy.Port > 0 ? proxy.Port : 80;
            result.Add($"-Dhttp.proxyHost={proxy.Host}");
            result.Add($"-Dhttp.proxyPort={port}");
            result.Add($"-Dhttps.proxyHost={proxy.Host}");
            result.Add($"-Dhttps.proxyPort={port}");
            // 本机回环必须绕过代理：游戏要连 127.0.0.1 的 IPC 端口做会话续期
            result.Add("-Dhttp.nonProxyHosts=localhost|127.*|[::1]");

            LogWrapper.Info("Lunar", $"已把代理 {proxy.Host}:{port} 注入游戏 JVM 参数");
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Lunar", "读取代理设置失败，游戏将直连");
        }

        return result;
    }

    /// <summary>
    ///     文件是否已经是要的那一份。不给 <paramref name="expectedSha1" /> 时只判断存不存在。
    /// </summary>
    internal static async Task<bool> IsUpToDateAsync(string path, string expectedSha1,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrWhiteSpace(expectedSha1)) return true;

        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     并发下载的并行度。
    ///     <para>
    ///         Lunar 的物料是「几百个小文件」，瓶颈在往返延迟而不是带宽，
    ///         所以按核数×2 取，比纯按核数快一倍以上。封顶 16 ——
    ///         再高对端会开始限流，反而更慢。
    ///     </para>
    /// </summary>
    internal static int DownloadParallelism => Math.Clamp(Environment.ProcessorCount * 2, 4, 16);

    /// <summary>
    ///     下一个文件到指定位置，带重试。
    ///     <para>
    ///         物料是几百个小文件并发拉，中间夹一次 TLS 握手失败太正常了
    ///         （实测 <c>multiver-releases.lunarclientprod.com</c> 偶发
    ///         <c>unexpected EOF</c>）。没有重试的话，一个文件的抖动会让
    ///         整段物料下载失败。
    ///     </para>
    /// </summary>
    internal static async Task DownloadAsync(string url, string destination,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1;; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, destination, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts
                                       && !cancellationToken.IsCancellationRequested
                                       && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // 退避 300ms / 1200ms，给对端一点喘息
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     下一个文件到指定位置。先写 <c>.lunardl</c> 临时文件再换名，
    ///     半截文件不会被下一次启动当成「已经有了」。
    /// </summary>
    private static async Task DownloadOnceAsync(string url, string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // 先写临时文件再换名，避免半截文件被下次启动当成「已存在」
        var temporary = destination + ".lunardl";
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var target = File.Create(temporary))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
                // 删不掉就算了，下次会覆盖
            }

            throw;
        }
    }

    /// <summary>
    ///     按顺序试一组地址，第一个成功的算数。全失败就把最后一个异常抛出去。
    ///     <para>
    ///         <c>jre.download</c> 本身就给了 <c>url</c> + <c>fallbackUrl</c> 两条，
    ///         官方 CDN 在国内经常拉不动，得留后路。
    ///     </para>
    /// </summary>
    internal static async Task DownloadFirstAsync(IReadOnlyList<string> urls, string destination,
        CancellationToken cancellationToken)
    {
        if (urls.Count == 0) throw new ArgumentException("没有可用的下载地址。", nameof(urls));

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadAsync(url, destination, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new HttpRequestException("下载失败。");
    }

    private static string Truncate(string text, int length)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= length ? flat : flat[..length] + "…";
    }
}
