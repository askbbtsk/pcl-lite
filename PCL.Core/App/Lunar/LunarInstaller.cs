using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.App.Lunar;

/// <summary>
///     Lunar 索引文件里的一行。
/// </summary>
/// <param name="RelativePath">相对物料根目录的路径，用 <c>/</c> 分隔。</param>
/// <param name="Hash">内容 SHA-1，同时也是 CDN 上的文件名。</param>
internal readonly record struct LunarIndexEntry(string RelativePath, string Hash);

/// <summary>
///     Lunar 索引文件（<c>texturesIndex.txt</c>、<c>uiIndex-&lt;sha1&gt;.txt</c>）的解析。
///     <para>
///         格式是纯文本，每行空格分隔若干列：
///         <c>&lt;相对路径&gt; &lt;sha1&gt; [字节数] [mtime]</c>。
///         官方启动器只取前两列（<c>split(' ')</c> 后取 <c>[0]</c>、<c>[1]</c>），
///         后面那两列是给增量判断用的，这里也用不上。
///     </para>
///     <para>
///         路径必须是相对路径，且不能含 <c>..</c> —— 官方启动器把这类条目直接判成
///         <c>INDEX_UNSAFE_PATH</c> 并抛异常，我们退一步：跳过这一行，别让整份索引废掉。
///     </para>
/// </summary>
internal static class LunarIndexParser
{
    private const int HashLength = 40;

    /// <summary>
    ///     解析索引内容。格式不对的行直接丢掉，不抛异常。
    /// </summary>
    internal static List<LunarIndexEntry> Parse(string? content)
    {
        var result = new List<LunarIndexEntry>();
        if (string.IsNullOrEmpty(content)) return result;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var separator = line.IndexOf(' ');
            if (separator <= 0) continue;

            var path = line[..separator];
            var rest = line[(separator + 1)..];

            // 后面的列是可选的，取到第一个空格为止
            var next = rest.IndexOf(' ');
            var hash = next < 0 ? rest : rest[..next];

            if (hash.Length != HashLength || !IsHex(hash)) continue;
            if (!IsSafeRelativePath(path)) continue;

            result.Add(new LunarIndexEntry(path, hash));
        }

        return result;
    }

    /// <summary>
    ///     相对路径安全检查：拒绝绝对路径、盘符和 <c>..</c>。
    /// </summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path[0] is '/' or '\\') return false;
        if (path.Length >= 2 && path[1] == ':') return false;

        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment == "..") return false;
        }

        return true;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        return true;
    }
}

/// <summary>
///     安装进度。界面按 <see cref="Stage" /> 换提示语，<see cref="Total" /> 为 0 表示这一步没有条数概念。
/// </summary>
/// <param name="Stage">阶段名，见 <see cref="LunarInstaller" /> 里的常量。</param>
/// <param name="Done">已完成条数。</param>
/// <param name="Total">总条数。</param>
public sealed record LunarInstallProgress(string Stage, int Done, int Total);

/// <summary>
///     把 Lunar Client 的物料从零补齐。
///     <para>
///         这一份是照着官方启动器的 stage 列表搬过来的，顺序也一致：
///         <c>licenses → textures → assets → artifacts(+natives) → jre → ui → logs</c>。
///         之前只做了 <c>artifacts</c>、<c>natives</c>、<c>jre</c> 和 <c>ui</c> 的源码包，
///         另外几个阶段全靠「用户装过 Lunar 启动器」这个前提，所以在全新机器上
///         必然卡住。补齐之后 <c>~/.lunarclient</c> 不存在也能一路装完。
///     </para>
///     <para>
///         所有步骤都是「缺什么补什么」：文件已经存在且 SHA-1 对得上就跳过，
///         所以装过 Lunar 启动器的机器上这一步几乎不产生流量。
///     </para>
/// </summary>
public static class LunarInstaller
{
    /// <summary>许可文件。</summary>
    public const string StageLicenses = "licenses";

    /// <summary>外观资源（皮肤 / 披风 / 表情）。</summary>
    public const string StageTextures = "textures";

    /// <summary>原版资源索引与对象。</summary>
    public const string StageAssets = "assets";

    /// <summary>客户端 jar 与原生库。</summary>
    public const string StageArtifacts = "artifacts";

    /// <summary>Java 运行时。</summary>
    public const string StageJre = "jre";

    /// <summary>游戏内界面资源。</summary>
    public const string StageUi = "ui";

    /// <summary>日志配置。</summary>
    public const string StageLogs = "logs";

    /// <summary>
    ///     按官方启动器的顺序把物料补齐。
    /// </summary>
    /// <param name="data">启动物料清单。</param>
    /// <param name="lunarRoot">Lunar 数据根目录。</param>
    /// <param name="javaPathOverride">设置里指定的 Java；给了就不再定位 / 下载 JRE。</param>
    /// <param name="verifyFiles">是否按 SHA-1 校验。关掉后只判断文件在不在。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="progress">进度回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可用的 <c>javaw.exe</c> 路径；拿不到时为 <see langword="null" />。</returns>
    public static async Task<string?> EnsureInstalledAsync(
        LunarLaunchData data,
        string lunarRoot,
        string? javaPathOverride,
        bool verifyFiles,
        Action<string>? log = null,
        Action<LunarInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(LunarClientService.LauncherCacheDirectory(lunarRoot));

        await EnsureLicensesAsync(data, lunarRoot, log, progress, cancellationToken).ConfigureAwait(false);
        await EnsureTexturesAsync(data, lunarRoot, log, progress, cancellationToken).ConfigureAwait(false);
        await EnsureAssetsAsync(data, lunarRoot, verifyFiles, log, progress, cancellationToken)
            .ConfigureAwait(false);
        await EnsureArtifactsAsync(data, lunarRoot, verifyFiles, log, progress, cancellationToken)
            .ConfigureAwait(false);

        var javaPath = await EnsureJavaAsync(data, lunarRoot, javaPathOverride, log, progress, cancellationToken)
            .ConfigureAwait(false);

        await EnsureUiAsync(data, lunarRoot, log, progress, cancellationToken).ConfigureAwait(false);
        EnsureLog4jConfig(lunarRoot, log);

        return javaPath;
    }

    #region 各阶段

    /// <summary>
    ///     许可文件。官方启动器每次启动都对一遍，缺了不影响进游戏，
    ///     但界面里「第三方许可」那一页会是空的。
    /// </summary>
    private static async Task EnsureLicensesAsync(
        LunarLaunchData data,
        string lunarRoot,
        Action<string>? log,
        Action<LunarInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (data.Licenses.Count == 0) return;

        var directory = LunarClientService.LicensesDirectory(lunarRoot);
        Directory.CreateDirectory(directory);
        progress?.Invoke(new LunarInstallProgress(StageLicenses, 0, data.Licenses.Count));

        var done = 0;
        foreach (var license in data.Licenses)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = Path.Combine(directory, license.File);
            var missing = !await LunarClientService.IsUpToDateAsync(destination, license.Sha1, cancellationToken)
                .ConfigureAwait(false);

            if (missing)
            {
                try
                {
                    await LunarClientService.DownloadAsync(license.Url, destination, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    log?.Invoke($"许可文件 {license.File} 下载失败：{ex.Message}");
                }
            }

            progress?.Invoke(new LunarInstallProgress(StageLicenses, ++done, data.Licenses.Count));
        }

        log?.Invoke($"许可文件：{data.Licenses.Count} 份");
    }

    /// <summary>
    ///     外观资源。索引式物料源，落在 <c>~/.lunarclient/textures/</c>，
    ///     也就是 <c>--texturesDir</c>。缺了不影响启动，只是别人看你 / 你看别人
    ///     都是默认皮肤。
    /// </summary>
    private static async Task EnsureTexturesAsync(
        LunarLaunchData data,
        string lunarRoot,
        Action<string>? log,
        Action<LunarInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (data.Textures is not { } textures) return;

        var count = await EnsureIndexedAsync(
            textures,
            lunarRoot,
            LunarClientService.TexturesDirectory(lunarRoot),
            "texturesIndex.txt",
            StageTextures,
            log,
            progress,
            cancellationToken).ConfigureAwait(false);

        log?.Invoke($"外观资源：索引 {count} 项");
    }

    /// <summary>
    ///     原版资源。先补索引文件，再按索引补 <c>objects/</c>。
    ///     <para>
    ///         这是整条流程里唯一可能几百 MB 的一步（全新机器约 300 MB）。
    ///         失败不当成致命错误：没贴图没声音总比起不来强。
    ///     </para>
    /// </summary>
    private static async Task EnsureAssetsAsync(
        LunarLaunchData data,
        string lunarRoot,
        bool verifyFiles,
        Action<string>? log,
        Action<LunarInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (data.AssetIndexInfo is not { } index)
        {
            log?.Invoke("没有拿到资源索引，游戏可能缺贴图与音效");
            return;
        }

        var assetsDirectory = LunarClientService.AssetsDirectory(lunarRoot);

        try
        {
            await LunarClientService.EnsureAssetIndexAsync(index, assetsDirectory, cancellationToken)
                .ConfigureAwait(false);
            log?.Invoke($"资源索引：{index.Id}");

            var report = progress is null
                ? null
                : new Progress<(int Done, int Total)>(p =>
                    progress(new LunarInstallProgress(StageAssets, p.Done, p.Total)));

            await LunarClientService
                .EnsureAssetObjectsAsync(index, assetsDirectory, report, verifyFiles, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            // 资源补齐失败不该拦住启动
            log?.Invoke("原版资源补齐失败，游戏可能缺贴图与音效：" + ex.Message);
        }
    }

    /// <summary>
    ///     客户端 jar 与原生库。落在 <c>offline/multiver/</c>，与 Lunar 启动器共用同一份。
    /// </summary>
    private static async Task EnsureArtifactsAsync(
        LunarLaunchData data,
        string lunarRoot,
        bool verifyFiles,
        Action<string>? log,
        Action<LunarInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = LunarClientService.MultiverDirectory(lunarRoot);
        var nativesDirectory = LunarClientService.NativesDirectory(lunarRoot);

        var report = progress is null
            ? null
            : new Progress<(int Done, int Total)>(p =>
                progress(new LunarInstallProgress(StageArtifacts, p.Done, p.Total)));

        await LunarClientService
            .EnsureArtifactsAsync(data, cacheDirectory, report, verifyFiles, cancellationToken)
            .ConfigureAwait(false);
        await LunarClientService
            .ExtractNativesAsync(data, cacheDirectory, nativesDirectory, cancellationToken)
            .ConfigureAwait(false);

        log?.Invoke($"物料：{data.Artifacts.Count} 个附件已就位");
    }

    /// <summary>
    ///     Java 运行时。设置里指定了就直接用，否则按 <c>jre.folderChecksum</c> 找 / 下。
    /// </summary>
    private static async Task<string?> EnsureJavaAsync(
        LunarLaunchData data,
        string lunarRoot,
        string? javaPathOverride,
        Action<string>? log,
        Action<LunarInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(javaPathOverride))
        {
            log?.Invoke($"Java：{javaPathOverride}（设置里指定的，已跳过 Lunar 自带 JRE 的定位）");
            return javaPathOverride;
        }

        progress?.Invoke(new LunarInstallProgress(StageJre, 0, 0));

        var javaPath = await LunarClientService
            .EnsureJavaAsync(data, lunarRoot, new Progress<string>(message => log?.Invoke(message)),
                cancellationToken)
            .ConfigureAwait(false);

        log?.Invoke(string.IsNullOrWhiteSpace(javaPath)
            ? "Java：没找到也没能下载成功"
            : "Java：" + javaPath);

        return javaPath;
    }

    /// <summary>
    ///     游戏内界面资源。先解 <c>source.zip</c>，再跑一遍索引式物料源
    ///     （<c>ui.assets</c>）把运行时资源补齐。
    /// </summary>
    private static async Task EnsureUiAsync(
        LunarLaunchData data,
        string lunarRoot,
        Action<string>? log,
        Action<LunarInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(data.UiSha1))
        {
            log?.Invoke("没有拿到界面资源版本号，游戏内菜单可能不显示");
            return;
        }

        progress?.Invoke(new LunarInstallProgress(StageUi, 0, 0));

        var uiDirectory = await LunarClientService.EnsureUiAsync(data, lunarRoot, cancellationToken)
            .ConfigureAwait(false);
        log?.Invoke("界面资源：" + uiDirectory);

        if (data.UiAssets is not { } uiAssets) return;

        // 这些物料必须落到 <ui>/assets/ 下，不能落到 <ui>/ 根目录。
        // Lunar 的 index.html 里写的是 href="/assets/index.css"、src="/static/index.js"，
        // 而 ui.assets 索引里的相对路径是平的（bg.png、index.css、DMSans-*.ttf…），
        // 官方启动器就是把这份索引下载到 ui/<sha1>/assets/（反编译 main.js：
        // destination = join(uiDir, "assets")）。写进根目录的话 assets/ 永远不存在，
        // WebOSR 加载不到 CSS 和背景图，主界面就成了透明的一片。
        var uiAssetsDirectory = Path.Combine(uiDirectory, "assets");

        var count = await EnsureIndexedAsync(
            uiAssets,
            lunarRoot,
            uiAssetsDirectory,
            $"uiIndex-{data.UiSha1}.txt",
            StageUi,
            log,
            progress,
            cancellationToken).ConfigureAwait(false);

        log?.Invoke($"界面资源索引：{count} 项");
    }

    #endregion

    #region 索引式下载

    /// <summary>
    ///     索引式物料源的通用下载流程，对应官方启动器的 <c>indexed-downloader</c>。
    ///     <para>
    ///         ① 把索引落到 <c>launcher-cache/&lt;cacheName&gt;</c>，SHA-1 对得上就复用；
    ///         ② 逐行解析出 <c>相对路径 + sha1</c>；
    ///         ③ 目标文件缺失或哈希不符时，从 <c>baseUrl + sha1</c> 拉。
    ///     </para>
    ///     <para>
    ///         官方启动器还会顺手删掉索引里不再引用的文件。这里刻意不做 ——
    ///         删错了没法恢复，多留几个文件最多是浪费几百 KB。
    ///     </para>
    /// </summary>
    /// <returns>索引里的条目数。</returns>
    private static async Task<int> EnsureIndexedAsync(
        LunarIndexedSource source,
        string lunarRoot,
        string destinationDirectory,
        string cacheName,
        string stage,
        Action<string>? log,
        Action<LunarInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        var cacheDirectory = LunarClientService.LauncherCacheDirectory(lunarRoot);
        Directory.CreateDirectory(cacheDirectory);
        var indexPath = Path.Combine(cacheDirectory, cacheName);

        if (!await LunarClientService.IsUpToDateAsync(indexPath, source.IndexSha1, cancellationToken)
                .ConfigureAwait(false))
        {
            await LunarClientService.DownloadAsync(source.IndexUrl, indexPath, cancellationToken)
                .ConfigureAwait(false);
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(indexPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var entries = LunarIndexParser.Parse(content);
        if (entries.Count == 0) return 0;

        var done = 0;
        var failures = new ConcurrentBag<string>();
        progress?.Invoke(new LunarInstallProgress(stage, 0, entries.Count));

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = LunarClientService.DownloadParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(entries, options, async (entry, ct) =>
        {
            var target = Path.Combine(destinationDirectory,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                if (!await LunarClientService.IsUpToDateAsync(target, entry.Hash, ct).ConfigureAwait(false))
                {
                    await LunarClientService
                        .DownloadAsync(source.BaseUrl + entry.Hash, target, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // 单个文件失败不打断整段：外观/界面资源缺一两个不影响进游戏
                failures.Add($"{entry.RelativePath}（{ex.Message}）");
            }

            var current = Interlocked.Increment(ref done);
            progress?.Invoke(new LunarInstallProgress(stage, current, entries.Count));
        }).ConfigureAwait(false);

        if (!failures.IsEmpty)
        {
            log?.Invoke($"{stage}：{failures.Count} 个文件没下来" +
                        $"（{string.Join("、", failures.Take(3))}…），下次启动会接着补");
        }

        return entries.Count;
    }

    #endregion

    #region 日志配置

    /// <summary>
    ///     官方启动器的 log4j 配置模板，一字未改。
    ///     <para>
    ///         <c>BuildStartInfo</c> 会把 <c>-Dlog4j.configurationFile</c> 指到
    ///         <c>logs/game/config.xml</c>。这个文件以前只有 Lunar 启动器会写，
    ///         没装启动器时它不存在 —— log4j2 找不到配置会退回默认配置，
    ///         结果是 <c>latest.log</c> 里一条游戏日志都没有。
    ///     </para>
    /// </summary>
    private const string Log4jTemplate = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Configuration status="WARN">
            <Appenders>
                <Console name="SysOut" target="SYSTEM_OUT">
                    <PatternLayout pattern="[%d{HH:mm:ss}] [%t/%level]: %msg{nolookups}%n"/>
                </Console>
                <Queue name="ServerGuiConsole">
                    <PatternLayout pattern="[%d{HH:mm:ss} %level]: %msg{nolookups}%n"/>
                </Queue>
                <RollingRandomAccessFile name="File" fileName="absolute-path-here" filePattern="gz-absolute-path-here">
                    <PatternLayout pattern="[%d{HH:mm:ss}] [%t/%level]: %msg{nolookups}%n"/>
                    <Policies>
                        <TimeBasedTriggeringPolicy/>
                        <OnStartupTriggeringPolicy/>
                    </Policies>
                </RollingRandomAccessFile>
                <Listener name="Tracy">
                    <PatternLayout pattern="(%F:%L): %msg{nolookups}%n"/>
                </Listener>
            </Appenders>
            <Loggers>
                <Root level="info">
                    <filters>
                        <MarkerFilter marker="NETWORK_PACKETS" onMatch="DENY" onMismatch="NEUTRAL"/>
                    </filters>
                    <AppenderRef ref="SysOut"/>
                    <AppenderRef ref="File"/>
                    <AppenderRef ref="ServerGuiConsole"/>
                    <AppenderRef ref="Tracy"/>
                </Root>
            </Loggers>
        </Configuration>
        """;

    /// <summary>
    ///     写 <c>logs/game/config.xml</c>。内容固定，直接覆盖 ——
    ///     用户不需要改它，改坏了反而看不到日志。
    /// </summary>
    /// <returns>写好的配置文件路径。</returns>
    public static string EnsureLog4jConfig(string lunarRoot, Action<string>? log = null)
    {
        var directory = LunarClientService.GameLogsDirectory(lunarRoot);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "config.xml");

        // 顺序不能反：`absolute-path-here` 是 `gz-absolute-path-here` 的子串，
        // 而 C# 的 string.Replace 会替换**全部**出现位置（JS 的 String.replace
        // 只替第一个，所以官方启动器反着写没事）。先替长的那个，短的才不会误伤。
        var content = Log4jTemplate
            .Replace("gz-absolute-path-here", Path.Combine(directory, "%d{yyyy-MM-dd}-%i.log.gz"))
            .Replace("absolute-path-here", Path.Combine(directory, "latest.log"));

        try
        {
            // 只在内容真的不一样时才写，避免每次启动都碰一次磁盘时间戳
            if (!File.Exists(path) || File.ReadAllText(path) != content)
                File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("写日志配置失败（不影响启动）：" + ex.Message);
        }

        return path;
    }

    #endregion
}
