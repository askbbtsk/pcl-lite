using System;
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

    /// <summary>额外 JVM 参数，会被插在内存参数之后。</summary>
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

    /// <summary>物料缓存目录，与 Lunar 启动器自己用的位置一致，可复用已下好的文件。</summary>
    public static string MultiverDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "offline", "multiver");

    public static string NativesDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "offline", "multiver", "natives");

    public static string AssetsDirectory(string lunarRoot) =>
        Path.Combine(lunarRoot, "shared", "assets");

    /// <summary>
    ///     界面资源目录。
    ///     <para>
    ///         Lunar 把界面资源按 SHA-1 分目录放在 <c>ui/&lt;sha1&gt;/</c> 下，
    ///         <c>--uiDir</c> 必须指到这一层。只给 <c>ui/</c> 的话 Genesis 找不到
    ///         <c>index.html</c>，游戏内菜单整块不显示（HUD 和模组都还在，很容易误判）。
    ///     </para>
    /// </summary>
    public static string UiDirectory(string lunarRoot, string uiSha1) =>
        string.IsNullOrWhiteSpace(uiSha1)
            ? Path.Combine(lunarRoot, "ui")
            : Path.Combine(lunarRoot, "ui", uiSha1);

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
            UiUrl = root["ui"]?["sourceUrl"]?.GetValue<string>() ?? string.Empty
        };
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
    ///         只补这一个索引文件，不去拉几万个资源对象——那是 Lunar 启动器
    ///         「更新资源」的活，这里只保证游戏能找到索引。
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

        await DownloadAsync(index.Url, destination, cancellationToken).ConfigureAwait(false);
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
        progress?.Report((0, files.Count));

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(files, options, async (artifact, ct) =>
        {
            var destination = Path.Combine(targetDirectory, artifact.Name);

            var needsDownload = verify
                ? !await IsUpToDateAsync(destination, artifact.Sha1, ct).ConfigureAwait(false)
                : !File.Exists(destination);

            if (needsDownload)
                await DownloadAsync(artifact.Url, destination, ct).ConfigureAwait(false);

            var current = Interlocked.Increment(ref done);
            progress?.Report((current, files.Count));
        }).ConfigureAwait(false);
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

        foreach (var artifact in data.NativesArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = Path.Combine(cacheDirectory, artifact.Name);

            if (!await IsUpToDateAsync(archive, artifact.Sha1, cancellationToken).ConfigureAwait(false))
                await DownloadAsync(artifact.Url, archive, cancellationToken).ConfigureAwait(false);

            try
            {
                ZipFile.ExtractToDirectory(archive, nativesDirectory, overwriteFiles: true);
            }
            catch (InvalidDataException)
            {
                // 不是 zip 就跳过，别让一个坏包拖垮整次启动
            }
        }
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

        if (options.ExtraJvmArguments.Count > 0)
            arguments.AddRange(options.ExtraJvmArguments);

        // 官方启动器按版本下发的 JVM 参数：1.21+ 的模块访问、26.x 的 ZGC 都在这儿，
        // 漏了会在启动早期直接报错（--enable-native-access 之类）
        if (data.JvmExtraArguments.Count > 0)
            arguments.AddRange(data.JvmExtraArguments);

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

        // 0 表示「没有 IPC 服务」，游戏侧会跳过社交与实时设置同步
        Add("--ipcPort", "0");

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

    private static async Task<bool> IsUpToDateAsync(string path, string expectedSha1,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrWhiteSpace(expectedSha1)) return true;

        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadAsync(string url, string destination,
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

    private static string Truncate(string text, int length)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= length ? flat : flat[..length] + "…";
    }
}
