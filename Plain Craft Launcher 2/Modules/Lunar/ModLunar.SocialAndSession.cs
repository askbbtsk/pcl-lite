using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;
using PCL.Core.App.Lunar.Social;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Network;

namespace PCL;

public static partial class ModLunar
{

    /// <summary>当前连接。没连上时是 <see langword="null" />。</summary>
    public static LunarSocialClient? Social => _social;


    /// <summary>正在连接。</summary>
    public static bool IsSocialBusy { get; private set; }


    public static bool IsSocialConnected => _social?.IsConnected == true;


    /// <summary>
    ///     连上 Lunar 后端并拉一次好友列表。
    ///     <para>
    ///         账号走的是启动时那套解析顺序（Lunar 账户库 → PCL 登录 → 兜底），
    ///         因为 Lunar 后端只认「这个账号的 Minecraft 令牌」，和游戏用哪个账号是同一件事。
    ///     </para>
    /// </summary>
    public static async Task ConnectSocialAsync()
    {
        if (IsSocialBusy) return;
        IsSocialBusy = true;
        SocialStateChanged?.Invoke();

        try
        {
            var (name, uuidText, token) = await ResolveCredentialsAsync().ConfigureAwait(false);
            var uuid = LunarUuid.Parse(uuidText);
            if (uuid.IsEmpty) throw new LunarSocialException("拿不到账号 UUID，没法登录 Lunar 后端");

            // 握手 semver 会被服务端校验下限，连接前刷成官方最新正式版
            var reportedVersion = await LunarLauncherVersion.ResolveAsync().ConfigureAwait(false);

            LunarSocialClient client;
            lock (SocialLock)
            {
                if (_social is null)
                {
                    _social = new LunarSocialClient(ReadOrCreateInstallationId(), reportedVersion);
                    _social.StateChanged += () => SocialStateChanged?.Invoke();
                    _social.FriendsChanged += () => SocialFriendsChanged?.Invoke();
                    _social.MessageReceived += (id, message) => SocialMessageReceived?.Invoke(id, message);
                }
                else
                {
                    // 实例是复用的，版本可能已经过期（Lunar 发了新正式版）
                    _social.LauncherVersion = reportedVersion;
                }

                client = _social;
            }

            Log($"正在登录 Lunar 后端（账号 {name}）…");
            await client.ConnectAsync(token, uuid, name).ConfigureAwait(false);
            Log($"Lunar 好友系统已连上，好友 {client.Friends.Count} 个");
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException)
        {
            Log("登录 Lunar 后端失败：" + ex.Message);
            throw;
        }
        finally
        {
            IsSocialBusy = false;
            SocialStateChanged?.Invoke();
        }
    }


    /// <summary>断开并释放连接。</summary>
    public static async Task DisconnectSocialAsync()
    {
        LunarSocialClient? client;
        lock (SocialLock)
        {
            client = _social;
            _social = null;
        }

        if (client is null) return;
        await client.DisposeAsync().ConfigureAwait(false);
        SocialStateChanged?.Invoke();
    }


    /// <summary>
    ///     Lunar 的「安装 ID」。官方启动器装一次生成一次，服务端拿它区分设备。
    ///     <para>PCL 没有这个文件，就在共享数据目录里自己存一份，删了会重新生成。</para>
    /// </summary>
    private static string ReadOrCreateInstallationId()
    {
        try
        {
            var path = Path.Combine(Paths.SharedLocalData, "lunar-installation.id");
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length == 32) return existing;
            }

            var generated = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Paths.SharedLocalData);
            File.WriteAllText(path, generated);
            return generated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModBase.Log(ex, "[Lunar] 读写安装 ID 失败，改用临时值", ModBase.LogLevel.Debug);
            return Guid.NewGuid().ToString("N");
        }
    }



    /// <summary>
    ///     这次启动写进哪个目录。设置里指定过就用指定的，否则用当前 MC 根目录。
    ///     <para>
    ///         刻意不跟实例的版本隔离目录走：Lunar 的版本（比如 1.12.2）和主页面选中的实例
    ///         （比如某个 1.21 Fabric 模组包）根本不是一个东西，用隔离目录会写坏人家：
    ///         <c>options.txt</c> 被低版本覆盖、存档和资源包混进去、模组包的设置全乱。
    ///     </para>
    /// </summary>
    public static string ResolveGameDirectory()
    {
        var custom = Config.Preference.Lunar.GameDir;
        return string.IsNullOrWhiteSpace(custom) ? ModFolder.mcFolderSelected : custom;
    }


    /// <summary>
    ///     把 Lunar 需要的原版客户端补到 <see cref="VanillaRoot" />。
    ///     <para>
    ///         走 PCL 自己的下载任务，进度会出现在下载任务栏里。以前是让 Genesis 在后台
    ///         闷头下，界面上什么都没有，玩家只能看着「进程活着但没窗口」猜。
    ///     </para>
    ///     <para>
    ///         <c>repairOnly</c> 是关键：只缺 <c>libraries</c> 时 jar 和 json 都在，
    ///         普通模式下会被当成「实例已存在」直接跳过，于是又回到 Genesis 闷头下。
    ///     </para>
    ///     <para>
    ///         下载失败不阻断启动：Genesis 自己还会再试一次，只是慢、且没进度。
    ///     </para>
    /// </summary>
    private static async Task EnsureVanillaAsync(string version, CancellationToken cancellationToken)
    {
        if (IsVanillaReady(version)) return;

        try
        {
            Log($"原版 {version} 的文件不齐，交给 PCL 补齐到 {VanillaRoot}");

            ModLoader.LoaderCombo<string>? loader = null;
            ModBase.RunInUiWait(() => loader = ModDownloadLib.McDownloadClient(
                NetPreDownloadBehaviour.ExitWhileExistsOrDownloading, version, null, VanillaRoot,
                repairOnly: true));

            if (loader is null)
            {
                Log("原版下载任务没能启动（可能被功能限制拦下了），改由 Lunar 自己下载");
                return;
            }

            StatusText = Lang.Text("Lunar.Status.VanillaDownload", version);
            DetailText = Lang.Text("Lunar.Status.VanillaDownloadDesc");
            RaiseStateChanged();

            var input = Path.Combine(VanillaRoot, "versions", version);
            await Task.Run(() => loader.WaitForExit(input), cancellationToken).ConfigureAwait(false);

            Log(IsVanillaReady(version)
                ? $"原版 {version} 已就绪"
                : $"原版 {version} 下载完了但文件还是不齐，剩下的交给 Lunar 自己补");
        }
        catch (Exception ex)
        {
            // 下不下来也别拦着启动，Genesis 会自己再下一次
            ModBase.Log(ex, "[Lunar] 原版客户端下载失败，改由 Lunar 自己下载", ModBase.LogLevel.Debug);
            Log("原版客户端下载失败：" + ex.Message + "，改由 Lunar 自己下载");
        }
    }


    private static Task LaunchCoreAsync(string gameDirectory)
    {
        var session = GameSessions.TryBegin(() => { _isBusy = true; _stopRequested = false; });
        return session == 0 ? Task.CompletedTask : LaunchCoreAsync(gameDirectory, session);
    }

    private static async Task LaunchCoreAsync(string gameDirectory, long launchSession)
    {
        try
        {
            if (!GameSessions.IsCurrent(launchSession)) return;
            _stopRequested = false;
            var subVersion = SelectedSubVersion;
            GameDirectory = gameDirectory;

            // 内存 / JVM 参数 / Java 覆盖都在设置页里配，这里只负责解析出最终值
            var settings = LunarLaunchProfile.Resolve(subVersion, ResolvePclGlobalMemoryMb());
            LogSettings(settings);

            StatusText = Lang.Text("Lunar.Status.PreparingVersion", subVersion);
            DetailText = "";
            InstallPercent = -1;
            RaiseStateChanged();

            var (playerName, playerUuid, accessToken) = await ResolveCredentialsAsync().ConfigureAwait(false);

            var module = SelectedModule;
            Log($"目标版本：{subVersion}");
            Log(module is null
                ? $"Loader：{SelectedModuleId}（还没拿到版本元数据，按兜底值走）"
                : $"Loader：{module.DisplayName}（{module.Id}，该版本共 {Modules.Count} 种可选）");
            Log($"游戏目录：{gameDirectory}");

            // 首次用某个版本时 Genesis 要先把原版客户端下到 %APPDATA%\.minecraft，
            // 这一步以前是它自己在后台闷头下，现在改成由 PCL 下（有进度、能看见）
            await EnsureVanillaAsync(subVersion, CancellationToken.None).ConfigureAwait(false);

            StatusText = Lang.Text("Lunar.Status.FetchManifest");
            RaiseStateChanged();

            var installationId = ReadInstallationId();

            // 先把「上报给 Lunar 的启动器版本」查好并缓存：后面 /launch 请求体、
            // 握手 semver、User-Agent 全都读这份缓存。查不到会退回本机官方启动器
            // 的 FileVersion，再不行才用内置兜底值，不会挡住启动。
            var reportedVersion = await LunarLauncherVersion.ResolveAsync().ConfigureAwait(false);
            Log($"上报启动器版本：{reportedVersion}");

            var data = await LunarClientService.FetchLaunchDataAsync(
                subVersion, SelectedModuleId,
                installationId: installationId,
                profileId: playerUuid,
                profileName: playerName).ConfigureAwait(false);

            Log($"物料清单：{data.Artifacts.Count} 个附件，主类 {data.MainClass}");

            // 全新机器上账户库压根不存在，而游戏进程只认 accounts.json 里的激活账号。
            // 先按这次解析出的凭据建一份，再往下走（已有内容一律不动）
            EnsureAccountLibrary(playerName, playerUuid, accessToken);

            var cacheDirectory = LunarClientService.MultiverDirectory(LunarRoot);
            var nativesDirectory = LunarClientService.NativesDirectory(LunarRoot);

            // 校验可以在设置里关掉：关掉后只判断文件在不在，不算 SHA-1
            var verifyFiles = Config.Preference.Lunar.VerifyFiles;
            _lastProgressStage = "";
            _lastProgressPercent = -1;

            // 物料补齐全交给 LunarInstaller：licenses → textures → assets →
            // artifacts/natives → jre → ui → logs，顺序与官方启动器一致
            var acceptInstallProgress = 1;
            var javaPath = await LunarInstaller.EnsureInstalledAsync(
                    data, LunarRoot, settings.JavaPathOverride, verifyFiles,
                    message => LogSession(launchSession, message),
                    progress => QueueSessionNotification(launchSession, () =>
                    {
                        if (Volatile.Read(ref acceptInstallProgress) == 1) ReportInstallProgress(progress);
                    }))
                .ConfigureAwait(false);
            Interlocked.Exchange(ref acceptInstallProgress, 0);

            if (string.IsNullOrWhiteSpace(javaPath))
                throw new InvalidOperationException(
                    $"没有找到 {subVersion} 需要的 Java（期望目录 jre/{data.JreFolderChecksum}），" +
                    "自动下载也没成功。可以在「设置 → Lunar」里手动指定一个 Java，或者检查网络。");

            JavaPath = javaPath;
            RaiseStateChanged();

            // 游戏会连 127.0.0.1:<ipcPort> 要账号续期。端口空闲就 PCL 自己接管；
            // 被占用说明 Lunar 启动器正开着，让它处理
            var ipc = await StartIpcAsync(installationId, launchSession).ConfigureAwait(false);
            Log(ipc.Message);

            var options = new LunarLaunchOptions
            {
                LunarRoot = LunarRoot,
                GameDirectory = gameDirectory,
                VersionName = subVersion,
                PlayerName = playerName,
                PlayerUuid = playerUuid,
                AccessToken = accessToken,
                // Genesis 会把这些原样转给游戏，给个数字比空串安全
                XboxUid = "0",
                AllocatedMemoryMb = settings.MemoryMb,
                ExtraJvmArguments = settings.JvmArguments,
                InstallationId = installationId,
                IpcPort = ipc.GamePort
            };

            var startInfo = LunarClientService.BuildStartInfo(
                data, options, javaPath, cacheDirectory, nativesDirectory);

            // 换成 java.exe 并接管输出，否则启动失败时什么都看不到
            startInfo.FileName = Path.Combine(Path.GetDirectoryName(javaPath)!, "java.exe");
            if (!File.Exists(startInfo.FileName)) startInfo.FileName = javaPath;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;

            LastCommandLine = startInfo.FileName + " " + string.Join(' ', startInfo.ArgumentList);

            StatusText = Lang.Text("Lunar.Status.Launching");
            InstallPercent = -1;
            RaiseStateChanged();

            lock (_recentOutputLock) _recentOutput.Clear();
            var startedAt = DateTime.Now;
            // 上一轮的时间戳留着会让看门狗以为「刚吐过字」，必须归零
            Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);
            _lastCpuTime = TimeSpan.Zero;

            // 必须在 Start 之前问：Genesis 一起来就会开始写 versions/<ver>，事后再问就永远是「已就绪」
            var vanillaReady = IsVanillaReady(subVersion);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => CaptureSessionOutput(launchSession, e.Data ?? "");
            process.ErrorDataReceived += (_, e) => CaptureSessionOutput(launchSession, e.Data ?? "");
            process.Exited += (_, _) =>
            {
                LunarIpcServer? retiredIpc = null;
                var exitCode = -1;
                var stopped = false;
                var tail = new List<string>();
                try { exitCode = process.ExitCode; }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
                var elapsed = DateTime.Now - startedAt;
                var completed = GameSessions.Complete(launchSession, () =>
                {
                    stopped = _stopRequested;
                    _stopRequested = false;
                    _process = null;
                    retiredIpc = _ipcServer;
                    _ipcServer = null;
                    tail = SnapshotGameOutput();
                    var forcedExit = stopped || exitCode == -1;
                    StatusText = forcedExit ? Lang.Text("Lunar.Status.Stopped")
                        : exitCode == 0 ? Lang.Text("Lunar.Status.Exited") : Lang.Text("Lunar.Status.Crashed", exitCode);
                    DetailText = forcedExit || exitCode == 0 ? "" : Lang.Text("Lunar.Status.CrashedDesc");
                    if (!forcedExit && exitCode != 0 && PCL.Core.App.Lunar.LunarCrashDiagnostics.IsNativeMemoryFailure(tail))
                        DetailText = Lang.Text("Lunar.Status.NativeMemoryFailure");
                    // Publish availability only after final state is complete. No UI callbacks under the gate.
                    _isBusy = false;
                });
                if (!completed) return;
                try { retiredIpc?.Dispose(); }
                catch (Exception ex) { ModBase.Log(ex, "[Lunar] 释放已退出会话的 IPC 失败", ModBase.LogLevel.Debug); }
                LogSession(launchSession, $"游戏进程已退出（PID {process.Id}，退出码 {exitCode}{(stopped ? "，由 PCL 结束" : "")}，运行 {elapsed.TotalSeconds:F0} 秒）", completed: true);
                var forced = stopped || exitCode == -1;
                if (!forced && (exitCode != 0 || elapsed < TimeSpan.FromMinutes(1)))
                {
                    if (tail.Count == 0)
                    {
                        ModBase.Log("[Lunar] 子进程没有输出任何内容，说明它在 JVM 起来之前就结束了");
                    }
                    else
                    {
                        ModBase.Log($"[Lunar] 下面是子进程最后的 {tail.Count} 行输出：");
                        foreach (var line in tail) ModBase.Log("[Lunar][out] " + line);
                    }
                }

                QueueSessionNotification(launchSession, () => StateChanged?.Invoke(), completed: true);
            };

            GameSessions.RunIfCurrent(launchSession, () =>
            {
            _process = process;
            Interlocked.Exchange(ref _processSession, launchSession);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            ObserveSuccessfulGame(process, launchSession);
            WatchFirstWindow(process, gameDirectory, startedAt, subVersion, vanillaReady, launchSession);

            // 首次启动某个版本时 Genesis 要先把原版客户端下下来，这期间不会有窗口，
            // 所以状态文案得说实话，不然用户只会看到「游戏运行中」却什么都没有。
            // 进程可能已经在这几行之前就退了（Exited 是线程池回调），那就别再盖回去
            if (!process.HasExited)
            {
                StatusText = vanillaReady
                    ? Lang.Text("Lunar.Status.Running")
                    : Lang.Text("Lunar.Status.VanillaPending", subVersion);
                DetailText = vanillaReady
                    ? Lang.Text("Lunar.Status.RunningDesc")
                    : Lang.Text("Lunar.Status.VanillaPendingDesc", VanillaRoot);
                QueueSessionNotification(launchSession, () => StateChanged?.Invoke());
            }
            });
        }
        catch (Exception ex)
        {
            LunarIpcServer? retiredIpc = null;
            var completed = GameSessions.Complete(launchSession, () =>
            {
            _process = null;
            retiredIpc = _ipcServer;
            _ipcServer = null;
            StatusText = Lang.Text("Lunar.Status.Failed");
            DetailText = GetLaunchFailureMessage(ex);
            _isBusy = false;
            });
            if (!completed) return;
            try { retiredIpc?.Dispose(); }
            catch (Exception disposeError) { ModBase.Log(disposeError, "[Lunar] 释放失败会话的 IPC 失败", ModBase.LogLevel.Debug); }
            QueueSessionNotification(launchSession, () =>
            {
                StateChanged?.Invoke();
                ModBase.Log(ex, "[Lunar] 启动失败", ModBase.LogLevel.Msgbox, userSummary: GetLaunchFailureMessage(ex));
            }, completed: true);
        }
    }

    private static string GetLaunchFailureMessage(Exception error) =>
        error is LunarNativeFilesUnavailableException unavailable
            ? Lang.Text("Lunar.Error.NativesInUse",
                string.Join("、", unavailable.Files.Take(6).Select(Path.GetFileName))
                + (unavailable.Files.Count > 6 ? $"……另有 {unavailable.Files.Count - 6} 个文件" : string.Empty))
            : error.Message;


    /// <summary>
    ///     看门狗：启动后一直没窗口时，判断到底是「在正常启动」还是「网络卡死」。
    ///     <para>
    ///         以前只看 <c>logs/latest.log</c> 和原版目录体积，于是踩了两个坑：
    ///         ① Lunar 自带的 log4j 配置会让文件 appender 起不来（日志里那几行
    ///         <c>Unable to locate appender</c>），<c>latest.log</c> 根本不写，游戏都进世界了
    ///         还在报「疑似卡死」；
    ///         ② 物料齐了以后体积本来就不再涨，于是「没涨」被当成「卡住」。
    ///     </para>
    ///     <para>
    ///         现在按四个信号判断活着，任意一个成立就继续等：<b>窗口出来了</b>、
    ///         <b>最近 90 秒吐过字</b>、<b>原版目录还在变大</b>、<b>进程还在烧 CPU</b>。
    ///         四个同时熄火连续三轮（约 60 秒）才提示，也就是说真卡死时大约两分半报出来。
    ///     </para>
    /// </summary>
    private static void WatchFirstWindow(Process process, string gameDirectory, DateTime startedAt, string subVersion,
        bool vanillaReady)
        => WatchFirstWindow(process, gameDirectory, startedAt, subVersion, vanillaReady, Interlocked.Read(ref _processSession));

    private static void WatchFirstWindow(Process process, string gameDirectory, DateTime startedAt, string subVersion,
        bool vanillaReady, long launchSession)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!GameSessions.IsCurrent(launchSession) || !ReferenceEquals(_process, process) || process.HasExited) return;
                if (!vanillaReady)
                    LogSession(launchSession, $"原版 {subVersion} 的文件还没补齐，Lunar 会自己把剩下的下到 {VanillaRoot}。" +
                        "这期间不会有窗口，属正常现象，别关进程。");

                var lastBytes = MeasureVanillaFootprint().Bytes;
                var lastCpuTime = ReadCpuTime(process);
                var stalled = 0;

                // 每 20 秒看一次，最多看 10 分钟：大文件下载慢的时候也不至于提前下结论
                for (var round = 1; round <= 30; round++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    if (!GameSessions.IsCurrent(launchSession) || !ReferenceEquals(_process, process) || process.HasExited) return;

                    // 已经进游戏了，看门狗可以收工
                    if (HasGameWindow(process.Id)) return;
                    if (HasGameLog(gameDirectory, startedAt)) return;

                    // ① 还在吐字。Genesis / MC 正常启动时会一直刷日志，彻底静默才有嫌疑
                    if (Environment.TickCount64 - Interlocked.Read(ref _lastOutputTicks) < 90_000)
                    {
                        stalled = 0;
                        continue;
                    }

                    // ② 还在补原版依赖
                    var footprint = MeasureVanillaFootprint();
                    if (footprint.Bytes > lastBytes + 512 * 1024)
                    {
                        LogSession(launchSession, $"Lunar 自己在补原版依赖（PCL 那边没补完）：已 {footprint.Bytes / 1024d / 1024d:F0} MB、" +
                            $"{footprint.Files} 个文件，继续等待…");
                        lastBytes = footprint.Bytes;
                        stalled = 0;
                        continue;
                    }

                    // ③ 还在烧 CPU：解压、校验、生成 baked classes 这些都不打印日志
                    var cpu = ReadCpuTime(process);
                    if (cpu - lastCpuTime > TimeSpan.FromSeconds(1))
                    {
                        lastCpuTime = cpu;
                        lastBytes = footprint.Bytes;
                        stalled = 0;
                        continue;
                    }

                    lastBytes = footprint.Bytes;
                    stalled += 1;
                    if (stalled < 3) continue;

                    LogSession(launchSession, $"启动 {round * 20} 秒后既没有窗口、没有日志、原版依赖也不再变大 —— " +
                        "大概率是 Genesis 在下载依赖时被网络卡住了");
                    QueueSessionNotification(launchSession, () => HintService.Hint(Lang.Text("Lunar.Hint.Stalled"), HintType.Warning));
                    return;
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Lunar] 首窗检查失败", ModBase.LogLevel.Debug);
            }
        });
    }


    /// <summary>读子进程累计 CPU 时间；进程刚退出或没有权限时返回零。</summary>
    private static TimeSpan ReadCpuTime(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return TimeSpan.Zero;
        }
    }


    /// <summary>
    ///     Genesis 放原版客户端的地方。<b>它写死用标准 <c>.minecraft</c></b>，
    ///     和我们在 <c>--gameDir</c> 里传的目录无关 —— 实测 1.17.1 的
    ///     <c>versions/1.17.1/1.17.1.jar</c> 就是落在 <c>%APPDATA%\.minecraft</c> 里的。
    /// </summary>
    private static string VanillaRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");


    /// <summary>这个版本的原版客户端文件在不在（只看 jar 与 json，依赖缺失时 Genesis 会补）。</summary>
    private static bool IsVanillaReady(string version)
    {
        try
        {
            var directory = Path.Combine(VanillaRoot, "versions", version);
            if (!File.Exists(Path.Combine(directory, version + ".jar")) ||
                !File.Exists(Path.Combine(directory, version + ".json")))
                return false;

            // jar 和 json 齐了不代表能用：Genesis 还要 libraries 里那几百个文件。
            // 只看 jar/json 会漏判，结果是它自己在后台把剩下的下完 —— 实测能到
            // 1.3 GB / 970 个文件，全程没有进度，看着就像卡死。
            return CountMissingVanillaFiles(version) == 0;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 检查原版客户端失败", ModBase.LogLevel.Debug);
            return true; // 查不出来就别乱下结论
        }
    }


    /// <summary>
    ///     用 PCL 自己的库文件清单数一下还缺几个文件（主 jar 也在清单里）。
    ///     只比存在性，不算哈希 —— 够判断「要不要让 PCL 补一次」，真正的校验交给下载任务。
    /// </summary>
    private static int CountMissingVanillaFiles(string version)
    {
        var instanceFolder = Path.Combine(VanillaRoot, "versions", version);
        return ModLibrary.McLibNetFilesFromInstance(new McInstance(instanceFolder))
            .Count(file => !File.Exists(file.LocalPath));
    }


    /// <summary>
    ///     量一下 <c>libraries</c> 与 <c>versions</c> 的总体积和文件数，用来判断「还在下载」。
    ///     遍历失败（权限、文件被删）一律当 0，不能让看门狗把自己搞崩。
    /// </summary>
    private static (long Bytes, int Files) MeasureVanillaFootprint()
    {
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var root in new[] { "libraries", "versions" })
            {
                var directory = Path.Combine(VanillaRoot, root);
                if (!Directory.Exists(directory)) continue;

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                {
                    files += 1;
                    try
                    {
                        bytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 文件刚被替换掉，跳过
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 统计原版目录体积失败", ModBase.LogLevel.Debug);
        }

        return (bytes, files);
    }


    /// <summary>游戏目录里的 <c>latest.log</c> 在这次启动之后有没有被写过。</summary>
    private static bool HasGameLog(string gameDirectory, DateTime startedAt)
    {
        try
        {
            var latestLog = Path.Combine(gameDirectory, "logs", "latest.log");
            return File.Exists(latestLog) && File.GetLastWriteTime(latestLog) >= startedAt;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 读取游戏日志时间失败", ModBase.LogLevel.Debug);
            return false;
        }
    }


    /// <summary>
    ///     决定这次启动用哪个账号。
    ///     <para>
    ///         Lunar 有账号就直接用 Lunar 的（并且把它的激活账号切成这一个），
    ///         没有才回落到 PCL 主页面登录的凭据。两条路都不通才报错。
    ///     </para>
    ///     <para>
    ///         注意「账号」和「令牌」是两件事：账号由 <c>accounts.json</c> 的
    ///         <c>activeAccountLocalId</c> 决定，而命令行里的 <c>--accessToken</c>
    ///         要一个没被吊销、没到期的 Minecraft 令牌。账户库里那份也是真令牌，
    ///         只是可能已经过期，所以优先用 PCL 登录里同名账号的。
    ///     </para>
    /// </summary>
    private static async Task<(string Name, string Uuid, string Token)> ResolveCredentialsAsync()
    {
        RefreshAccounts();
        _launchedLocalId = string.Empty;
        _canRefreshSession = false;

        // ① 用户在账号下拉框里明确点了「Lunar 身份账号」—— 那是本页登录出来的，
        //    和 PCL 主页面的账号完全是两套，互不影响
        if (SelectedIdentityAccount is { } identity)
            return await ResolveFromIdentityAsync(identity).ConfigureAwait(false);

        var account = SelectedAccount;

        if (account is not null)
        {
            // 游戏进程读的是 Lunar 自己的激活账号，这里同步一下，
            // 否则「界面上选的是 A、进游戏是 B」
            if (LunarAccountStore.SetActive(LunarClientService.SettingsRoot, account.LocalId))
            {
                _launchedLocalId = account.LocalId;
                Log($"账号：{account.DisplayName}（Lunar 账户库，{account.Type}）");
            }
            else
            {
                Log($"账号：{account.DisplayName}（切换 Lunar 激活账号失败，进游戏可能是另一个账号）");
            }

            var uuid = NormalizeUuid(account.MinecraftUuid);

            // 启动前先把令牌换成新鲜的。PCL 登录里的令牌 24 小时就过期，账户库里那份也一样。
            // 以前这里只看「PCL 里有没有这个号的登录」——令牌一过期就直接判定成「没有登录」，
            // 然后拿账户库里那份同样过期的令牌去启动，进游戏就是 Invalid session。
            if (await RefreshAccountTokenAsync(account, CancellationToken.None).ConfigureAwait(false)
                is { } fresh)
            {
                _canRefreshSession = true;
                Log($"Minecraft 令牌：{fresh.Message}");
                return (account.DisplayName, uuid, fresh.Token);
            }

            if (account.IsExpired)
            {
                Log($"Lunar 会话已于 {account.ExpiresAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm} 过期，" +
                    "PCL 里也没有能替它续期的登录。");
                Log("请在主页面用这个账号登录一次（或者打开 Lunar 启动器重新登录），否则游戏里会显示 Invalid session。");
                HintService.Hint(Lang.Text("Lunar.Hint.SessionExpired", account.DisplayName),
                    HintType.Warning);
            }
            else
            {
                Log("Minecraft 令牌：退回账户库那份（还能用，但不会再续期）");
            }

            return (account.DisplayName, uuid, account.AccessToken);
        }

        // ③ 既没选 Lunar 身份账号，账户库里也没有账号。
        //    这里**不再**偷偷回落到 PCL 主页面的登录：那套令牌是 PCL 自己的 client id 签的，
        //    JWT 里的 aid 对不上，Lunar 认不出来，启动只会卡在 Connecting...。
        throw new InvalidOperationException(
            "还没有可用的 Lunar 账号。请在账号下拉框右侧点「登录」，用 Lunar 身份登录一次。\n" +
            "这个登录和 PCL 主页面的账号是两套，互不影响。");
    }

}
