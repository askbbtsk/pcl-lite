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
using PCL.Core.App.Lunar.Social;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;

namespace PCL.Core.App.Lunar;

public static partial class LunarClientService
{

    /// <summary>
    ///     下载通道。宿主（启动器）在启动时注册：给了就走启动器自带的下载管线
    ///     （代理、HTTP 模式、限速、分段下载、统一重试全都生效），没给就退回
    ///     本类内置的裸 <see cref="HttpClient" /> 重试下载。
    ///     <para>
    ///         Core 层引用不了应用层的下载器，只能反过来让宿主把通道注入进来。
    ///     </para>
    /// </summary>
    /// <param name="urls">候选下载地址，第一个成功的算数。</param>
    /// <param name="destination">目标文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static Func<IReadOnlyList<string>, string, CancellationToken, Task>? DownloadDispatcher { get; set; }


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
    ///     界面资源的落点：服务端给了版本号就是 <c>ui/&lt;sha1&gt;/</c>，否则退回 <c>ui/</c>。
    ///     <para>
    ///         下载和解压**只认这个目录**，不要拿 <see cref="UiDirectory" /> 的结果去写 ——
    ///         那个方法会为了「尽量能用」去猜现有目录，猜中 <c>ui/assets/</c> 这种数据目录时，
    ///         压缩包就被解到错误的位置，下一次启动的 <c>--uiDir</c> 跟着一起错。
    ///     </para>
    /// </summary>
    public static string UiTargetDirectory(string lunarRoot, string uiSha1)
    {
        var root = Path.Combine(lunarRoot, "ui");
        return string.IsNullOrWhiteSpace(uiSha1) ? root : Path.Combine(root, uiSha1);
    }


    /// <summary>
    ///     界面资源目录。
    ///     <para>
    ///         Lunar 把界面资源按 SHA-1 分目录放在 <c>ui/&lt;sha1&gt;/</c> 下，
    ///         <c>--uiDir</c> 必须指到这一层。只给 <c>ui/</c> 的话 Genesis 找不到
    ///         <c>index.html</c>，游戏内菜单整块不显示（HUD 和模组都还在，很容易误判）。
    ///     </para>
    ///     <para>
    ///         退回逻辑**只认有 <c>index.html</c> 的目录**：<c>ui/assets/</c> 是放 css 和字体的
    ///         数据目录，指过去等于让 Genesis 去一个没有入口页的地方找 index.html，
    ///         菜单（主界面的按钮）就整块不渲染。以前按「最新的子目录」挑，正好会挑中它。
    ///     </para>
    /// </summary>
    public static string UiDirectory(string lunarRoot, string uiSha1)
    {
        var root = Path.Combine(lunarRoot, "ui");

        // ① 版本目录里真的有入口页才算数
        if (!string.IsNullOrWhiteSpace(uiSha1))
        {
            var exact = Path.Combine(root, uiSha1);
            if (File.Exists(Path.Combine(exact, "index.html"))) return exact;
        }

        // ② 老布局（资源直接摊在 ui/ 根下）或官方启动器留下的其它目录
        if (File.Exists(Path.Combine(root, "index.html"))) return root;

        try
        {
            if (Directory.Exists(root))
            {
                var usable = Directory.GetDirectories(root)
                    .Where(x => File.Exists(Path.Combine(x, "index.html")))
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (usable is not null) return usable;
            }
        }
        catch (IOException)
        {
            // 目录读不了就走下面的兜底
        }

        // ③ 一个能用的都没有：把下载目标交出去，让调用方去补
        return UiTargetDirectory(lunarRoot, uiSha1);
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
            // 官方启动器这里填的是自己的壳版本（app.getVersion()），
            // 也就是最新正式版 —— 不是 --launcherVersion 那个 3.3.0
            ["launcher_version"] = LunarSocialProtocol.ResolveLauncherVersion(),
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
        request.Headers.TryAddWithoutValidation("User-Agent", LunarSocialProtocol.LauncherUserAgent);

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
            request.Headers.TryAddWithoutValidation("User-Agent", LunarSocialProtocol.LauncherUserAgent);

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
        // 下载目标恒为 ui/<sha1>/（版本号缺失时就是 ui/），不能跟着 UiDirectory 的猜测走
        var target = UiTargetDirectory(lunarRoot, data.UiSha1);

        // 版本目录里已经有入口页就直接复用；uiSha1 为空时上面这行等价于判断 ui/ 根下
        if (File.Exists(Path.Combine(target, "index.html"))) return target;

        if (string.IsNullOrWhiteSpace(data.UiUrl))
        {
            // 没地址也没现成资源，把猜测结果交出去，至少不会崩
            return UiDirectory(lunarRoot, data.UiSha1);
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
        var unavailableNatives = new ConcurrentBag<string>();

        foreach (var artifact in data.NativesArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = Path.Combine(cacheDirectory, artifact.Name);

            try
            {
                if (!await IsUpToDateAsync(archive, artifact.Sha1, cancellationToken).ConfigureAwait(false))
                    await DownloadAsync(artifact.Url, archive, cancellationToken).ConfigureAwait(false);

                await LunarNativeArchiveExtractor.ExtractChangedFilesAsync(archive, nativesDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LunarNativeFilesUnavailableException ex)
            {
                foreach (var path in ex.Files) unavailableNatives.Add(path);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                           or TaskCanceledException or InvalidDataException)
            {
                failures.Add($"{artifact.Name}（{ex.Message}）");
            }
        }

        if (!unavailableNatives.IsEmpty)
            throw new LunarNativeFilesUnavailableException(unavailableNatives
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray());

        ThrowIfAnyFailed(failures, "原生库");
    }

}
