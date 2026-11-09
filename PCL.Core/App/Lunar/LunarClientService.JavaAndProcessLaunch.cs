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
        Add("--launcherVersion", options.GameLauncherVersion);
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
    /// <summary>
    ///     下载一个文件。注册了 <see cref="DownloadDispatcher" /> 就整包交给宿主的
    ///     下载管线（多地址 + 重试由它统一处理），否则用内置的重试下载。
    /// </summary>
    internal static Task DownloadAsync(string url, string destination,
        CancellationToken cancellationToken)
        => DownloadAsync([url], destination, cancellationToken);


    /// <inheritdoc cref="DownloadAsync(string, string, CancellationToken)" />
    internal static async Task DownloadAsync(IReadOnlyList<string> urls, string destination,
        CancellationToken cancellationToken)
    {
        if (DownloadDispatcher is { } dispatch)
        {
            await dispatch(urls, destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        const int maxAttempts = 3;

        for (var attempt = 1;; attempt++)
        {
            try
            {
                await DownloadOnceAsync(urls[0], destination, cancellationToken).ConfigureAwait(false);
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
    ///     <para>
    ///         多地址的取舍直接交给下载通道：宿主管线（FileDownloader）本来就支持
    ///         多地址轮试；内置兜底沿用原来的逐个尝试。
    ///     </para>
    /// </summary>
    internal static async Task DownloadFirstAsync(IReadOnlyList<string> urls, string destination,
        CancellationToken cancellationToken)
    {
        if (urls.Count == 0) throw new ArgumentException("没有可用的下载地址。", nameof(urls));

        if (DownloadDispatcher is not null)
        {
            await DownloadAsync(urls, destination, cancellationToken).ConfigureAwait(false);
            return;
        }

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
