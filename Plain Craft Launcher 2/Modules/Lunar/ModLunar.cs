using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Lunar;

namespace PCL;

/// <summary>
///     Lunar Client 的运行状态与启动流程。
///     <para>
///         这里只做四件事：确定用哪个版本、确定用哪个账号、按服务端清单补齐物料、
///         拼命令行拉起来。协议细节见 <c>docs/LUNAR.md</c>。
///     </para>
///     <para>
///         两个必须记住的事实：
///         ① 物料接口不需要鉴权，正版验证发生在游戏进程内；
///         ② 但游戏进程**不读**命令行里的 accessToken，它读的是
///         <c>~/.lunarclient/settings/game/accounts.json</c> 里的激活账号，
///         所以换账号必须改那个文件（<see cref="LunarAccountStore" />）。
///     </para>
/// </summary>
public static class ModLunar
{
    /// <summary>没选过版本时的默认值，也是唯一一个「本机一定有物料」的保底版本。</summary>
    public const string DefaultSubVersion = "1.8.9";

    public const string DefaultModule = "lunar";

    private static Process? _process;
    private static bool _isBusy;
    private static IReadOnlyList<LunarAccount> _accounts = [];
    private static string _activeAccountLocalId = string.Empty;

    public static event Action? StateChanged;

    public static event Action<string>? LogReceived;

    /// <summary>Lunar 数据根目录。</summary>
    public static string LunarRoot => LunarClientService.DefaultLunarRoot;

    public static bool IsInstalled => LunarClientService.IsInstalled();

    public static bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            RaiseStateChanged();
        }
    }

    /// <summary>游戏进程是否还活着。已退出或从未启动都返回 <c>false</c>。</summary>
    public static bool IsRunning
    {
        get
        {
            var process = _process;
            if (process is null) return false;
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // 进程对象还没 Start 过，当成没在跑
                return false;
            }
        }
    }

    public static string StatusText { get; private set; } = "正在检测 Lunar Client…";

    public static string DetailText { get; private set; } = "";

    /// <summary>最近一次启动的完整命令行，出错时贴出来方便定位。</summary>
    public static string LastCommandLine { get; private set; } = "";

    /// <summary>最近一次启动用到的 Java，未启动过时为空。</summary>
    public static string JavaPath { get; private set; } = "";

    /// <summary>最近一次启动实际使用的游戏目录（即 <c>--gameDir</c>）。</summary>
    public static string GameDirectory { get; private set; } = "";

    /// <summary>可选的 Lunar 子版本，未拉到版本列表时为空。</summary>
    public static IReadOnlyList<LunarVersion> Versions => LunarVersionCatalog.Versions;

    /// <summary>Lunar 账户库里的账号。</summary>
    public static IReadOnlyList<LunarAccount> Accounts => _accounts;

    /// <summary>Lunar 当前激活的账号。</summary>
    public static string ActiveAccountLocalId => _activeAccountLocalId;

    /// <summary>
    ///     当前选中的子版本。存在配置里，没选过时用 <see cref="DefaultSubVersion" />；
    ///     如果配置里那个版本已经不在服务端列表里（Lunar 会下线老版本），也退回默认值。
    /// </summary>
    public static string SelectedSubVersion
    {
        get
        {
            var configured = Config.Preference.Lunar.SubVersion;
            if (string.IsNullOrWhiteSpace(configured)) return DefaultSubVersion;
            if (LunarVersionCatalog.IsEmpty) return configured;
            return LunarVersionCatalog.Find(configured) is null ? DefaultSubVersion : configured;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == SelectedSubVersion) return;
            Config.Preference.Lunar.SubVersion = value;
            JavaPath = "";
            Refresh();
        }
    }

    /// <summary>
    ///     当前选中的账号。空值表示跟随 Lunar 自己的激活账号。
    /// </summary>
    public static LunarAccount? SelectedAccount =>
        new LunarAccountSnapshot(_activeAccountLocalId, _accounts)
            .Resolve(Config.Preference.Lunar.AccountLocalId);

    /// <summary>
    ///     刷新界面用的状态：是否装了 Lunar、版本列表、账号列表。
    ///     只读本地，不发网络请求，可以在 UI 线程直接调。
    /// </summary>
    public static void Refresh()
    {
        if (!IsInstalled)
        {
            StatusText = "未检测到 Lunar Client";
            DetailText = $"没有找到 {LunarRoot}。请先安装并至少启动过一次 Lunar Client，让物料落盘。";
            RaiseStateChanged();
            return;
        }

        LunarVersionCatalog.TryLoadCache(LunarRoot);
        RefreshAccounts();

        StatusText = $"Lunar Client {SelectedSubVersion}";
        DetailText = $"物料目录：{LunarClientService.MultiverDirectory(LunarRoot)}";
        RaiseStateChanged();
    }

    /// <summary>重新读一遍 Lunar 的账户库。</summary>
    public static void RefreshAccounts()
    {
        var snapshot = LunarAccountStore.Read(LunarRoot);
        _accounts = snapshot.Accounts;
        _activeAccountLocalId = snapshot.ActiveLocalId;
    }

    /// <summary>
    ///     拉一次版本列表（网络）。失败时保留缓存，不抛异常。
    /// </summary>
    public static async Task RefreshVersionsAsync()
    {
        if (!IsInstalled) return;

        var installationId = ReadInstallationId();
        await LunarVersionCatalog.FetchAsync(LunarRoot, installationId).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>
    ///     状态变化通知。统一走 UI 线程 —— 启动流程跑在后台线程，
    ///     直接在这里触发事件会让订阅者跨线程改控件。
    /// </summary>
    private static void RaiseStateChanged() =>
        ModBase.RunInUi(() => StateChanged?.Invoke());

    private static void Log(string message)
    {
        ModBase.Log("[Lunar] " + message);
        LogReceived?.Invoke(message);
    }

    /// <summary>
    ///     启动 Lunar Client。
    ///     <para>
    ///         整个流程丢到后台线程：登录加载器的 <c>WaitForExit</c> 是
    ///         <c>while (...) Thread.Sleep(10)</c> 的同步阻塞，在 UI 线程上调用
    ///         会直接把界面冻住（1.4.0-nightly 就是这么翻的车）。
    ///     </para>
    /// </summary>
    /// <param name="memoryMb">分配内存。</param>
    public static void Launch(int memoryMb = 2048)
    {
        if (IsBusy || IsRunning) return;

        // 游戏目录必须在 UI 线程取：PathIndie 首次访问会触发实例加载
        var gameDirectory = ResolveGameDirectory();

        // IsBusy 在 UI 线程上先占位，防止连点两次起两个进程
        IsBusy = true;
        _ = Task.Run(() => LaunchCoreAsync(gameDirectory, memoryMb));
    }

    /// <summary>
    ///     强制结束游戏进程（连同它派生的子进程）。
    /// </summary>
    public static void Stop()
    {
        var process = _process;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                Log("已强制结束游戏进程");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log("结束进程失败：" + ex.Message);
        }
    }

    /// <summary>
    ///     和正常启动用同一个游戏目录：优先取当前实例的版本隔离目录，
    ///     取不到才退回整个 .minecraft。
    ///     <para>
    ///         之前这里写死 <c>ModFolder.mcFolderSelected</c>，导致「主页面选的是
    ///         实例 A、Lunar 却写进 .minecraft 根目录」，存档和配置全混在一起。
    ///     </para>
    /// </summary>
    private static string ResolveGameDirectory()
    {
        try
        {
            var instance = ModInstanceList.McMcInstanceSelected;
            if (instance is not null && Directory.Exists(instance.PathIndie))
                return instance.PathIndie;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 读取当前实例目录失败，退回 .minecraft 根目录", ModBase.LogLevel.Debug);
        }

        return ModFolder.mcFolderSelected;
    }

    private static async Task LaunchCoreAsync(string gameDirectory, int memoryMb)
    {
        try
        {
            var subVersion = SelectedSubVersion;
            GameDirectory = gameDirectory;

            StatusText = $"正在准备 {subVersion}…";
            DetailText = "";
            RaiseStateChanged();

            var (playerName, playerUuid, accessToken) = await ResolveCredentialsAsync().ConfigureAwait(false);

            Log($"目标版本：{subVersion}");
            Log($"游戏目录：{gameDirectory}");

            StatusText = "正在获取 Lunar 物料清单…";
            RaiseStateChanged();

            var installationId = ReadInstallationId();
            var data = await LunarClientService.FetchLaunchDataAsync(
                subVersion, DefaultModule,
                installationId: installationId,
                profileId: playerUuid,
                profileName: playerName).ConfigureAwait(false);

            Log($"物料清单：{data.Artifacts.Count} 个附件，主类 {data.MainClass}");

            var cacheDirectory = LunarClientService.MultiverDirectory(LunarRoot);
            var nativesDirectory = LunarClientService.NativesDirectory(LunarRoot);

            // 校验可以在设置里关掉：关掉后只判断文件在不在，不算 SHA-1
            var verifyFiles = Config.Preference.Lunar.VerifyFiles;
            StatusText = verifyFiles ? "正在校验物料…" : "正在补齐物料…";
            RaiseStateChanged();

            var progress = new Progress<(int Done, int Total)>(p =>
            {
                StatusText = verifyFiles
                    ? $"正在校验物料 {p.Done}/{p.Total}"
                    : $"正在补齐物料 {p.Done}/{p.Total}";
                RaiseStateChanged();
            });
            await LunarClientService
                .EnsureArtifactsAsync(data, cacheDirectory, progress, verifyFiles)
                .ConfigureAwait(false);
            await LunarClientService.ExtractNativesAsync(data, cacheDirectory, nativesDirectory)
                .ConfigureAwait(false);

            // 不同 MC 版本要的 JRE 不一样，本机没有就按服务端地址补一份
            StatusText = "正在定位 Java…";
            RaiseStateChanged();

            var javaPath = await LunarClientService
                .EnsureJavaAsync(data, LunarRoot, new Progress<string>(message =>
                {
                    StatusText = message;
                    RaiseStateChanged();
                }))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(javaPath))
                throw new InvalidOperationException(
                    $"没有找到 {subVersion} 需要的 Java（期望目录 jre/{data.JreFolderChecksum}），" +
                    "自动下载也没成功。请先用 Lunar 启动器跑一次这个版本，让它把 JRE 下好。");

            Log("Java：" + javaPath);
            JavaPath = javaPath;
            RaiseStateChanged();

            // 资源索引是原版那套，缺了会没贴图没声音。只补索引文件，不拉几万个资源对象
            if (data.AssetIndexInfo is { } assetIndex)
            {
                Log($"资源索引：{assetIndex.Id}");
                await LunarClientService
                    .EnsureAssetIndexAsync(assetIndex, LunarClientService.AssetsDirectory(LunarRoot))
                    .ConfigureAwait(false);
            }
            else
            {
                Log("没有拿到资源索引，游戏可能缺贴图与音效");
            }

            // 界面资源：Lunar 按 SHA-1 分目录存放，--uiDir 要指到那一层，
            // 指错了游戏内菜单整块不显示
            if (!string.IsNullOrWhiteSpace(data.UiSha1))
            {
                var uiDir = await LunarClientService.EnsureUiAsync(data, LunarRoot).ConfigureAwait(false);
                Log($"界面资源：{uiDir}");
            }
            else
            {
                Log("没有拿到界面资源版本号，游戏内菜单可能不显示");
            }

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
                AllocatedMemoryMb = memoryMb,
                InstallationId = installationId
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

            StatusText = "正在启动…";
            RaiseStateChanged();

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogReceived?.Invoke(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogReceived?.Invoke(e.Data);
            };
            process.Exited += (_, _) =>
            {
                _process = null;
                IsBusy = false;
                StatusText = "游戏已退出";
                DetailText = "";
                RaiseStateChanged();
            };

            _process = process;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            StatusText = "游戏运行中";
            DetailText = "由 PCL 直接拉起，未经过 Lunar 启动器";
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            _process = null;
            IsBusy = false;
            StatusText = "启动失败";
            DetailText = ex.Message;
            RaiseStateChanged();
            ModBase.Log(ex, "[Lunar] 启动失败", ModBase.LogLevel.Msgbox, userSummary: ex.Message);
        }
    }

    /// <summary>
    ///     决定这次启动用哪个账号。
    ///     <para>
    ///         Lunar 有账号就直接用 Lunar 的（并且把它的激活账号切成这一个），
    ///         没有才回落到 PCL 主页面登录的凭据。两条路都不通才报错。
    ///     </para>
    /// </summary>
    private static async Task<(string Name, string Uuid, string Token)> ResolveCredentialsAsync()
    {
        RefreshAccounts();
        var account = SelectedAccount;

        if (account is not null)
        {
            // 游戏进程读的是 Lunar 自己的激活账号，这里同步一下，
            // 否则「界面上选的是 A、进游戏是 B」
            if (LunarAccountStore.SetActive(LunarRoot, account.LocalId))
                Log($"账号：{account.DisplayName}（Lunar 账户库，{account.Type}）");
            else
                Log($"账号：{account.DisplayName}（切换 Lunar 激活账号失败，进游戏可能是另一个账号）");

            var uuid = NormalizeUuid(account.MinecraftUuid);
            return (account.DisplayName, uuid, account.AccessToken);
        }

        Log("Lunar 账户库里没有账号，改用 PCL 主页面登录的凭据");

        // 直接复用主页面登录过的凭据：mcLoginLoader 跑完一次之后 output 就有值，
        // 再 Start() 一次会重新走 OAuth、多弹一次浏览器，用户会以为出问题了
        var login = ModLaunch.mcLoginLoader.output;

        if (string.IsNullOrWhiteSpace(login.AccessToken))
        {
            // 主页面还没登录过，兜底走一次。
            // 不能用 WaitForExit —— 它会同步 sleep，这里必须用轮询等
            ModLaunch.mcLoginLoader.Start();
            while (ModLaunch.mcLoginLoader.State == ModBase.LoadState.Loading)
                await Task.Delay(50).ConfigureAwait(false);

            if (ModLaunch.mcLoginLoader.State == ModBase.LoadState.Aborted)
                throw new InvalidOperationException("登录已取消。");
            if (ModLaunch.mcLoginLoader.Error is { } loginError)
                throw loginError;

            login = ModLaunch.mcLoginLoader.output;
        }

        if (string.IsNullOrWhiteSpace(login.AccessToken))
            throw new InvalidOperationException(
                "Lunar 账户库里没有账号，PCL 也没有登录凭据。请先在 Lunar 启动器里登录一次，或在主页面登录微软账号。");

        Log($"账号：{login.Name}（PCL 登录）");
        return (login.Name, NormalizeUuid(login.Uuid), login.AccessToken);
    }

    /// <summary>
    ///     Lunar 的 <c>installation_id</c> 存在 launcher-cache 下，有就复用，
    ///     这样服务端看到的是同一个「安装」。
    /// </summary>
    private static string ReadInstallationId()
    {
        try
        {
            var path = Path.Combine(LunarRoot, "launcher-cache", "installation-id");
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (Guid.TryParse(text, out var id)) return id.ToString();
            }
        }
        catch (IOException)
        {
            // 读不到就现生成一个，不影响启动
        }

        return Guid.NewGuid().ToString();
    }

    /// <summary>Lunar 要的是不带横线的 UUID，接口校验要的是带横线的，这里统一成带横线。</summary>
    private static string NormalizeUuid(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return Guid.Empty.ToString();
        var hex = uuid.Replace("-", string.Empty);
        if (hex.Length != 32) return Guid.Empty.ToString();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
