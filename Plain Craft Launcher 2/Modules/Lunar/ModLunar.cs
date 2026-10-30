using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

/// <summary>
///     Lunar Client 的运行状态与启动流程。
///     <para>
///         这里只做四件事：确定用哪个版本、确定用哪个账号、按服务端清单补齐物料、
///         拼命令行拉起来。协议细节见 <c>docs/LUNAR.md</c>。
///     </para>
///     <para>
///         三个必须记住的事实：
///         ① 物料接口不需要鉴权，正版验证发生在游戏进程内；
///         ② 游戏进程**不读**命令行里的 accessToken 做会话，它读的是
///         <c>~/.lunarclient/settings/game/accounts.json</c> 里的激活账号，
///         所以换账号必须改那个文件（<see cref="LunarAccountStore" />）；
///         ③ 账户库里那个 <c>accessToken</c> 过期后，游戏会通过本地 IPC 的
///         <c>AuthService/RefreshAccount</c> 找启动器续期。PCL 不经过 Lunar 启动器，
///         所以由 <see cref="LunarIpcServer" /> 自己接管那个端口。
///     </para>
/// </summary>
public static class ModLunar
{
    /// <summary>没选过版本时的默认值，也是唯一一个「本机一定有物料」的保底版本。</summary>
    public const string DefaultSubVersion = "1.8.9";

    /// <summary>
    ///     拿不到版本元数据时用的 loader。<c>lunar</c> 就是「Lunar + OptiFine」，
    ///     也是 1.8.9 的官方默认，所以当兜底是安全的。
    /// </summary>
    public const string FallbackModule = "lunar";

    private static Process? _process;
    private static bool _isBusy;
    private static LunarIpcServer? _ipcServer;

    /// <summary>
    ///     子进程最后一次吐字的时间（<see cref="Environment.TickCount64" />）。
    ///     <para>
    ///         卡死判定最可靠的信号。Genesis 从「Starting game!」到窗口出现之间什么都不打印，
    ///         但**一旦开始正常启动就一定会持续刷日志**；反过来真被网络卡住时是彻底静默。
    ///         以前只看 <c>logs/latest.log</c>，而 Lunar 自带的 log4j 配置会让文件 appender
    ///         起不来（日志里那几行 <c>Unable to locate appender</c>），于是游戏都进世界了
    ///         还在报「疑似卡死」。
    ///     </para>
    /// </summary>
    private static long _lastOutputTicks = Environment.TickCount64;

    /// <summary>上次看门狗轮询时子进程累计占用的 CPU 时间，用来判断它是不是还在干活。</summary>
    private static TimeSpan _lastCpuTime;

    /// <summary>IPC 里除续期之外那些请求的落地实现（开链接、切账号页、重开提示）。</summary>
    private static readonly LunarIpcHost IpcHost = new();

    private static IReadOnlyList<LunarAccount> _accounts = [];
    private static string _activeAccountLocalId = string.Empty;

    /// <summary>
    ///     这次退出是不是我们自己按的「结束游戏」。
    ///     <para>
    ///         主动结束时退出码可能是 <c>-1</c>（<c>Process.Kill</c>），也可能是 <c>0</c>
    ///         （请游戏自己退出）。以前只判断「退出码非 0」，于是玩家自己点停止也会被报成
    ///         「游戏异常退出（退出码 -1）」，白白吓一跳。
    ///     </para>
    /// </summary>
    private static volatile bool _stopRequested;

    /// <summary>正在执行停止流程，防止连点两次发两遍退出请求。</summary>
    private static bool _stopping;
    private static readonly object StopLock = new();

    /// <summary>
    ///     请游戏自己退出之后，先安静等多久再改口说「正在保存存档」。
    ///     <para>绝大多数情况一两秒就退了，这个阶段不打扰用户。</para>
    /// </summary>
    private static readonly TimeSpan ExitQuietPeriod = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     等游戏自己退出的上限。
    ///     <para>
    ///         以前是 10 秒 —— <b>太短了</b>。实测 1.8.9 在装了整合包的情况下，收到
    ///         <c>ExitGamePush</c> 之后要走完「停集成服务器 → 存档落盘 → 关资源包 → 关声音」，
    ///         十几秒很正常；10 秒一到就被 <c>Kill</c>，等于存档只写了一半，
    ///         这跟直接强杀没区别，白白浪费了这条推送的意义。
    ///         现在给到两分钟，但只有在进程真的一动不动时才提前放弃。
    ///     </para>
    /// </summary>
    private static readonly TimeSpan ExitMaxWait = TimeSpan.FromSeconds(120);

    /// <summary>
    ///     连续多久「既没有输出、也没有 CPU 时间推进」就判定它卡死了。
    ///     <para>
    ///         存档是重活，CPU 会一直跳；真卡住时（比如被弹窗挡住）两者会同时熄火。
    ///     </para>
    /// </summary>
    private static readonly TimeSpan ExitIdleLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     子进程最近若干行输出。游戏在 Minecraft 初始化前就静默退出时（1.17.1 踩过），
    ///     文件日志里只剩「启动了」和「退出了」，这几行是唯一的死因线索。
    /// </summary>
    private static readonly Queue<string> _recentOutput = new();

    private static readonly object _recentOutputLock = new();

    private const int RecentOutputLimit = 80;

    /// <summary>
    ///     Lunar 后端（好友 / 私聊）的连接。没有连接时为 <see langword="null" />。
    ///     <para>
    ///         刻意挂在静态类上而不是页面上：翻到别的页面再翻回来时连接不能断，
    ///         否则好友列表要重新拉一遍、私聊推送也会漏。
    ///     </para>
    /// </summary>
    private static LunarSocialClient? _social;

    private static readonly object SocialLock = new();

    /// <summary>
    ///     本次启动实际交给游戏的 Lunar 账号（<c>localId</c>）。
    ///     <para>
    ///         游戏发续期请求时带的是它自己认为的当前账号 UUID。万一对不上账户库
    ///         （账户库被 Lunar 启动器改过、或者文件正好写到一半），就按这个兜底：
    ///         启动前已经把激活账号切成它了，游戏问的必然是它。
    ///     </para>
    /// </summary>
    private static string _launchedLocalId = string.Empty;

    /// <summary>
    ///     本次启动能不能由 PCL 自己给账号续期（PCL 里有同一个账号的登录）。
    ///     <para>
    ///         能续期时就不把 IPC 端口让给 Lunar 启动器 —— 启动器开着但没登录时它续不了，
    ///         游戏照样会弹 Invalid session。抢不到 28190 就退到 28191，把端口指给游戏。
    ///     </para>
    /// </summary>
    private static bool _canRefreshSession;

    /// <summary>账号选择里 PCL 账号的前缀，和 Lunar 的 <c>localId</c> 区分开。</summary>
    public const string PclSelectionPrefix = "pcl:";

    public static event Action? StateChanged;

    public static event Action<string>? LogReceived;

    /// <summary>Lunar 自带的默认数据根目录（<c>~/.lunarclient</c>）。</summary>
    public static string DefaultLunarRoot => LunarClientService.DefaultLunarRoot;

    /// <summary>
    ///     Lunar 数据根目录。设置里指定过就用指定的，否则用默认的 <c>~/.lunarclient</c>。
    ///     <para>
    ///         物料、JRE、账户库全挂在这下面，所以换目录等于换一套环境，需要重新下物料。
    ///     </para>
    /// </summary>
    public static string LunarRoot
    {
        get
        {
            var custom = Config.Preference.Lunar.Root;
            return string.IsNullOrWhiteSpace(custom) ? LunarClientService.DefaultLunarRoot : custom;
        }
    }

    /// <summary>默认启动目录：当前选中的 MC 根目录。</summary>
    public static string DefaultGameDirectory => ModFolder.mcFolderSelected;

    public static bool IsInstalled => LunarClientService.IsInstalled(LunarRoot);

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

    /// <summary>
    ///     版本下拉框里那一项的悬停说明：大版本名，外加「官方默认」标记。
    ///     <para>
    ///         两个页面（Lunar 板块、设置页）都用它，免得一处写「官方默认」、另一处忘了写。
    ///     </para>
    /// </summary>
    public static string DescribeVersion(LunarVersion version)
    {
        var name = string.IsNullOrWhiteSpace(version.ParentName) ? version.Id : version.ParentName;
        return version.IsDefault ? name + " " + Lang.Text("Lunar.Module.Default") : name;
    }

    /// <summary>Lunar 账户库里的账号。</summary>
    public static IReadOnlyList<LunarAccount> Accounts => _accounts;

    /// <summary>Lunar 当前激活的账号。</summary>
    public static string ActiveAccountLocalId => _activeAccountLocalId;

    /// <summary>
    ///     当前选中的子版本。存在配置里，没选过时用 <see cref="DefaultSubVersion" />；
    ///     如果配置里那个版本已经不在服务端列表里（Lunar 会下线老版本），也退回默认值。
    ///     <para>
    ///         兜底顺序：配置 → <see cref="DefaultSubVersion" />（1.8.9，Lunar 最稳的版本）
    ///         → 服务端标了 <c>default</c> 的那个 → 列表里版本号最大的。
    ///     </para>
    ///     <para>
    ///         特意把 1.8.9 排在官方默认之前：官方默认是「最新 drop」（现在是 26.3），
    ///         换 drop 时 loader 清单会跟着变，用户点开 Lunar 板块就被丢进一个没下过物料的新版本。
    ///         官方那个只在 1.8.9 也被下线时才用得上。
    ///     </para>
    /// </summary>
    public static string SelectedSubVersion
    {
        get
        {
            var configured = Config.Preference.Lunar.SubVersion;
            if (LunarVersionCatalog.IsEmpty)
                return string.IsNullOrWhiteSpace(configured) ? DefaultSubVersion : configured;

            if (!string.IsNullOrWhiteSpace(configured) && LunarVersionCatalog.Find(configured) is not null)
                return configured;
            if (LunarVersionCatalog.Find(DefaultSubVersion) is not null)
                return DefaultSubVersion;
            if (LunarVersionCatalog.DefaultVersion is { } official)
                return official.Id;

            // 列表里既没有配置值也没有默认值（服务端下线了 1.8.9 这类老版本）。
            // 这里必须返回一个**列表里真的有**的 id：界面上「下拉框选中项」是按它反查的，
            // 返回列表外的值会让下拉框退化成 SelectedIndex = 0，于是显示 A、实际启动 B。
            var versions = LunarVersionCatalog.Versions;
            return versions.Count > 0 ? versions[0].Id : DefaultSubVersion;
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
    ///     当前版本可选的 loader。版本列表还没拉到时为空，界面上就不显示这一行。
    /// </summary>
    public static IReadOnlyList<LunarModule> Modules =>
        LunarVersionCatalog.Find(SelectedSubVersion)?.Modules ?? [];

    /// <summary>
    ///     这次启动实际用哪个 loader。
    ///     <para>
    ///         顺序：配置里记的（用户点过的）→ 该版本的官方默认 → 兜底 <c>lunar</c>。
    ///         配置里那个如果当前版本不提供（比如在 1.17.1 选了 fabric，又切到 1.8.9），
    ///         就退回官方默认，但**不改配置** —— 切回 1.17.1 时选择还在。
    ///         </para>
    ///     <para>
    ///         必须按服务端下发的 modules 走：1.17.1 的官方默认是 <c>fabric</c>，
    ///         一直写死 <c>lunar</c>（OptiFine）会在游戏初始化前静默退出。
    ///     </para>
    /// </summary>
    public static LunarModule? SelectedModule
    {
        get
        {
            var modules = Modules;
            if (modules.Count == 0) return null;

            var configured = Config.Preference.Lunar.ModuleId;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var match = modules.FirstOrDefault(x =>
                    string.Equals(x.Id, configured, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }

            return LunarVersionCatalog.Find(SelectedSubVersion)?.DefaultModule ?? modules[0];
        }
    }

    /// <summary>传给 <c>/launcher/launch</c> 的 <c>module</c> 值。</summary>
    public static string SelectedModuleId => SelectedModule?.Id ?? FallbackModule;

    /// <summary>
    ///     用户点了某个 loader。只记配置，不发请求 —— 物料清单在启动时才拉，
    ///     提前拉会白下一份用不上的附件。
    /// </summary>
    public static void SelectModule(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (string.Equals(Config.Preference.Lunar.ModuleId, id, StringComparison.Ordinal)) return;

        Config.Preference.Lunar.ModuleId = id;
        Log($"已选择 loader：{SelectedModule?.DisplayName ?? id}（{id}）");
        Refresh();
    }

    /// <summary>
    ///     当前选中的 Lunar 账号。空值表示「没选 Lunar 的」——要么选了 PCL 登录账号，
    ///     要么什么都没选（这时跟随 Lunar 自己的激活账号）。
    /// </summary>
    public static LunarAccount? SelectedAccount
    {
        get
        {
            var configured = Config.Preference.Lunar.AccountLocalId;
            if (IsPclSelection(configured)) return null;
            return new LunarAccountSnapshot(_activeAccountLocalId, _accounts).Resolve(StripLunarPrefix(configured));
        }
    }

    /// <summary>
    ///     当前选中的 PCL 登录账号。没选 PCL 账号时为 <c>null</c>。
    ///     <para>
    ///         只读本地档案，**不会触发任何登录/授权流程**，所以把它列进下拉框不会导致重新登录。
    ///     </para>
    /// </summary>
    public static McProfile? SelectedPclProfile
    {
        get
        {
            var configured = Config.Preference.Lunar.AccountLocalId;
            if (!IsPclSelection(configured)) return null;
            var id = configured[PclSelectionPrefix.Length..];
            return PclProfiles.FirstOrDefault(x => x.ProfileId == id);
        }
    }

    /// <summary>PCL 里登录过的微软账号。读不出来就返回空列表，不抛异常。</summary>
    public static IReadOnlyList<McProfile> PclProfiles
    {
        get
        {
            try
            {
                return ProfileService.Profiles
                    .Where(x => x.ProfileType == ProfileType.Microsoft && !string.IsNullOrWhiteSpace(x.UserName))
                    .ToList();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Lunar] 读取 PCL 账号列表失败", ModBase.LogLevel.Debug);
                return [];
            }
        }
    }

    /// <summary>下拉框里那一项的唯一标识：<c>pcl:&lt;ProfileId&gt;</c> 或 <c>lunar:&lt;localId&gt;</c>。</summary>
    public static string SelectionTag(LunarAccount account) => "lunar:" + account.LocalId;

    public static string SelectionTag(McProfile profile) => PclSelectionPrefix + profile.ProfileId;

    private static bool IsPclSelection(string? value) =>
        value is not null && value.StartsWith(PclSelectionPrefix, StringComparison.OrdinalIgnoreCase);

    private static string StripLunarPrefix(string? value) =>
        value is not null && value.StartsWith("lunar:", StringComparison.OrdinalIgnoreCase) ? value[6..] : value ?? "";

    /// <summary>
    ///     刷新界面用的状态：是否装了 Lunar、版本列表、账号列表。
    ///     只读本地，不发网络请求，可以在 UI 线程直接调。
    ///     <para>
    ///         「没装」不再是禁用状态：物料全部由 <see cref="LunarInstaller" /> 从服务端
    ///         清单补齐，<c>~/.lunarclient</c> 是空的也能直接启动。
    ///     </para>
    /// </summary>
    public static void Refresh()
    {
        LunarVersionCatalog.TryLoadCache(LunarRoot);
        RefreshAccounts();

        if (!IsInstalled)
        {
            StatusText = Lang.Text("Lunar.Status.NoAssets");
            DetailText = Lang.Text("Lunar.Status.NoAssetsDesc");
            RaiseStateChanged();
            return;
        }

        StatusText = SelectedModule is { } module
            ? $"Lunar Client {SelectedSubVersion}（{module.DisplayName}）"
            : $"Lunar Client {SelectedSubVersion}";
        DetailText = Lang.Text("Lunar.Status.MaterialDir", LunarClientService.MultiverDirectory(LunarRoot));
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
    ///     <para>
    ///         没装 Lunar 也能拉 —— 版本列表本来就是启动前要用的东西。
    ///     </para>
    /// </summary>
    public static async Task RefreshVersionsAsync()
    {
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
    ///     接一行子进程输出：进环形缓冲（供退出时落盘）的同时推到界面日志框。
    /// </summary>
    private static void CaptureGameOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);

        lock (_recentOutputLock)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > RecentOutputLimit) _recentOutput.Dequeue();
        }

        LogReceived?.Invoke(line);
    }

    private static List<string> SnapshotGameOutput()
    {
        lock (_recentOutputLock)
        {
            return _recentOutput.ToList();
        }
    }

    /// <summary>
    ///     把 PCL 的全局内存设置换算成 MB。算不出来就返回 0，让上层退回内置默认。
    ///     <para>
    ///         <c>is32BitJava</c> 明确传 <see langword="false" />：不传的话它会去读
    ///         当前选中实例的 Java 设置，而 Lunar 的 JRE 是它自己按版本下发的，
    ///         跟实例没关系。
    ///     </para>
    /// </summary>
    public static int ResolvePclGlobalMemoryMb()
    {
        try
        {
            var gigabytes = PageSetupLaunch.GetRam(null, true, false);
            return gigabytes <= 0 ? 0 : (int)Math.Round(gigabytes * 1024d);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 读取 PCL 全局内存设置失败，改用内置默认", ModBase.LogLevel.Developer);
            return 0;
        }
    }

    /// <summary>
    ///     把这次生效的设置写进日志。用户反馈「改了没反应」时，这几行就是唯一线索。
    /// </summary>
    private static void LogSettings(LunarEffectiveSettings settings)
    {
        Log($"内存：{settings.MemoryMb} MB（{LunarLaunchProfile.DescribeSource(settings.MemorySource)}）");

        if (settings.JvmArguments.Count == 0)
            Log("JVM 参数：没有额外的自定义参数");
        else
            Log($"JVM 参数：{string.Join(' ', settings.JvmArguments)}" +
                $"（{LunarLaunchProfile.DescribeSource(settings.JvmSource)}）");
    }

    /// <summary>
    ///     安装进度的节流状态。物料阶段是并发下载，一秒能回调几十次；
    ///     每次都往 UI 线程丢一条就会把界面刷成幻灯片。
    /// </summary>
    private static string _lastProgressStage = "";

    private static int _lastProgressPercent = -1;

    /// <summary>
    ///     把 <see cref="LunarInstaller" /> 的阶段名翻成界面上的说法，并做节流。
    /// </summary>
    private static void ReportInstallProgress(LunarInstallProgress progress)
    {
        var percent = progress.Total > 0
            ? progress.Done * 100 / progress.Total
            : -1;

        // 同阶段内进度百分比没变就不刷新，换阶段一定要刷新
        if (progress.Stage == _lastProgressStage && percent == _lastProgressPercent && percent >= 0) return;

        _lastProgressStage = progress.Stage;
        _lastProgressPercent = percent;

        var name = progress.Stage switch
        {
            LunarInstaller.StageLicenses => Lang.Text("Lunar.Stage.Licenses"),
            LunarInstaller.StageTextures => Lang.Text("Lunar.Stage.Textures"),
            LunarInstaller.StageAssets => Lang.Text("Lunar.Stage.Assets"),
            LunarInstaller.StageArtifacts => Lang.Text("Lunar.Stage.Artifacts"),
            LunarInstaller.StageJre => Lang.Text("Lunar.Stage.Jre"),
            LunarInstaller.StageUi => Lang.Text("Lunar.Stage.Ui"),
            _ => progress.Stage
        };

        StatusText = progress.Total > 0
            ? Lang.Text("Lunar.Status.Preparing", name, progress.Done, progress.Total)
            : Lang.Text("Lunar.Status.PreparingName", name);
        RaiseStateChanged();
    }

    /// <summary>
    ///     账户库不存在时按这次登录建一份。
    ///     <para>
    ///         游戏进程不读命令行里的 <c>--accessToken</c>，它按
    ///         <c>accounts.json</c> 的 <c>activeAccountLocalId</c> 决定「我是谁」。
    ///         没装过 Lunar 启动器的机器上这个文件根本不存在，不补的话游戏进去没有身份。
    ///     </para>
    ///     <para>
    ///         账户库里已经**有任何账号**就什么都不做 —— 那是 Lunar 启动器或上一次启动的成果。
    ///         只有「文件不存在」或「文件里一个账号都没有」这两种情况才补。
    ///     </para>
    /// </summary>
    private static void EnsureAccountLibrary(string playerName, string playerUuid, string accessToken)
    {
        // 不能只看文件在不在：Lunar 启动器跑过但没登录时，文件里是个空的 accounts 对象
        if (_accounts.Count > 0) return;

        var uuid = NormalizeUuid(playerUuid).Replace("-", string.Empty);
        if (string.IsNullOrWhiteSpace(uuid) || uuid == new string('0', 32))
        {
            Log("账户库不存在，但这次没有拿到有效的游戏 UUID，跳过自动创建");
            return;
        }

        var localId = Guid.NewGuid().ToString("N");
        if (!LunarAccountStore.CreateIfMissing(LunarRoot, localId, playerName, playerName, uuid,
                accessToken, null))
        {
            Log("账户库不存在，自动创建失败 —— 游戏内可能没有身份，联机会失败");
            return;
        }

        _launchedLocalId = localId;
        RefreshAccounts();
        Log($"Lunar 账户库不存在，已按本次登录创建：{playerName}（{localId}）");
    }

    /// <summary>
    ///     启动 Lunar Client。
    ///     <para>
    ///         整个流程丢到后台线程：登录加载器的 <c>WaitForExit</c> 是
    ///         <c>while (...) Thread.Sleep(10)</c> 的同步阻塞，在 UI 线程上调用
    ///         会直接把界面冻住（1.4.0-nightly 就是这么翻的车）。
    ///     </para>
    /// </summary>
    /// <para>
    ///     内存、JVM 参数、Java 路径都由设置页决定（见 <see cref="LunarLaunchProfile" />），
    ///     这里不再接收参数，免得两个地方各算一套。
    /// </para>
    /// </summary>
    public static void Launch()
    {
        if (IsBusy || IsRunning) return;

        // 游戏目录必须在 UI 线程取：PathIndie 首次访问会触发实例加载
        var gameDirectory = ResolveGameDirectory();

        // IsBusy 在 UI 线程上先占位，防止连点两次起两个进程
        IsBusy = true;
        _ = Task.Run(() => LaunchCoreAsync(gameDirectory));
    }

    /// <summary>
    ///     结束游戏进程。
    ///     <para>
    ///         先请游戏自己退出（<see cref="LunarIpcPush.ExitGame" />）：游戏端收到后走的是
    ///         Minecraft 的正常关闭流程 —— 存档落盘、跑 shutdown hook、退出码 0。
    ///         等它自己退完（最长 <see cref="ExitMaxWait" />，一动不动才会提前放弃），才 <c>Kill</c> 兜底。
    ///     </para>
    ///     <para>
    ///         以前是直接 Kill：正在保存的世界会留下半截 region 文件。只有 IPC 不在 PCL
    ///         手里（端口被 Lunar 启动器占着）时才退回老做法。
    ///     </para>
    /// </summary>
    public static void Stop()
    {
        var process = _process;
        if (process is null) return;

        lock (StopLock)
        {
            if (_stopping) return;
            _stopping = true;
        }

        // 先立牌子再动手：Exited 事件可能在 Kill 返回前就排上队了
        _stopRequested = true;
        _ = Task.Run(() => StopCoreAsync(process));
    }

    private static async Task StopCoreAsync(Process process)
    {
        try
        {
            var sent = await TrySendExitPushAsync().ConfigureAwait(false);

            if (sent > 0)
            {
                Log($"已请游戏自行退出，最多等 {ExitMaxWait.TotalSeconds:F0} 秒（它一动不动才会提前强杀）");

                // 等待期间界面上要有个说法，否则按钮还写着「强制停止」，
                // 用户会以为没点着又点一次
                StatusText = Lang.Text("Lunar.Status.Stopping");
                DetailText = "";
                RaiseStateChanged();

                if (await WaitForGracefulExitAsync(process).ConfigureAwait(false))
                {
                    Log("游戏已自行退出（存档已落盘）");
                    return;
                }

                Log("游戏既没退出也没有任何动静，判定为卡住，改为强制结束");
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                Log("已强制结束游戏进程");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _stopRequested = false;
            Log("结束进程失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            _stopRequested = false;
            Log("结束进程时出错：" + ex);
        }
        finally
        {
            lock (StopLock) _stopping = false;
        }
    }

    /// <summary>
    ///     给游戏推一条 <c>ExitGamePush</c>，返回送达的连接数。
    ///     <para>
    ///         IPC 不在 PCL 手里时直接返回 0：那种情况游戏连的是 Lunar 启动器，
    ///         PCL 没有通道能跟它说话。
    ///     </para>
    /// </summary>
    private static async Task<int> TrySendExitPushAsync()
    {
        var server = _ipcServer;
        if (server is null)
        {
            Log("IPC 不在 PCL 手里（端口被 Lunar 启动器占着），没法请游戏自己退出");
            return 0;
        }

        try
        {
            return await server.SendPushAsync(LunarIpcPush.ExitGame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("发送退出请求失败：" + ex.Message);
            return 0;
        }
    }

    /// <summary>
    ///     等游戏自己退完，等到「真退」或者「真卡死」为止。
    ///     <para>
    ///         不能只给一个固定超时：存档要多久取决于世界大小和硬盘，10 秒远远不够，
    ///         而给两分钟固定超时又会在真卡住时让用户干等。所以改成看动静 ——
    ///         只要还在吐日志或者还在烧 CPU 就继续等，两个都熄火
    ///         <see cref="ExitIdleLimit" /> 才判定卡住。
    ///     </para>
    ///     <para>前 <see cref="ExitQuietPeriod" /> 秒不打扰用户，之后把界面文案换成「正在保存存档」。</para>
    /// </summary>
    private static async Task<bool> WaitForGracefulExitAsync(Process process)
    {
        var startedAt = Environment.TickCount64;
        var lastActivity = startedAt;
        var lastCpu = ReadCpuTime(process);
        var lastOutput = Interlocked.Read(ref _lastOutputTicks);
        var announced = false;

        while (true)
        {
            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(1)).ConfigureAwait(false)) return true;

            var now = Environment.TickCount64;
            var cpu = ReadCpuTime(process);
            var output = Interlocked.Read(ref _lastOutputTicks);

            // 判定「还在干活」：CPU 时间涨了，或者子进程又吐了新的一行
            if (output != lastOutput || cpu - lastCpu > TimeSpan.FromMilliseconds(100))
            {
                lastOutput = output;
                lastCpu = cpu;
                lastActivity = now;
            }

            var elapsed = now - startedAt;
            if (!announced && elapsed >= (long)ExitQuietPeriod.TotalMilliseconds)
            {
                announced = true;
                Log("游戏还在退出流程里（存档 / 关资源包），继续等它自己退完");
                StatusText = Lang.Text("Lunar.Status.Saving");
                DetailText = Lang.Text("Lunar.Status.SavingDesc");
                RaiseStateChanged();
            }

            if (now - lastActivity >= (long)ExitIdleLimit.TotalMilliseconds)
            {
                Log($"游戏已经 {ExitIdleLimit.TotalSeconds:F0} 秒没有任何输出、也没占用 CPU，判定为卡死");
                return false;
            }

            if (elapsed >= (long)ExitMaxWait.TotalMilliseconds)
            {
                Log($"等了 {ExitMaxWait.TotalSeconds:F0} 秒还没退完，放弃等待");
                return false;
            }
        }
    }

    /// <summary>等进程退出，超时返回 <c>false</c>（不抛异常）。</summary>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // 进程对象已经失效，说明它确实没了
            return true;
        }
    }

    /// <summary>
    ///     把游戏窗口切到前台。
    ///     <para>
    ///         光发 <c>FocusGamePush</c> <b>不够</b>：游戏那边的处理器只做 AWT 层面的
    ///         <c>toFront()</c>，被 Windows 的前台锁定挡住时不会真把窗口抬起来。实测日志里
    ///         能看到 <c>Received focus game push, focusing window</c> 一行接一行，窗口却纹丝
    ///         不动。所以真正的抬升由 PCL 用 Win32 做（必要时把输入队列挂到当前前台线程上），
    ///         推送只当辅助信号，让游戏自己也把内部焦点切过去。
    ///     </para>
    ///     <para>返回 <see langword="false" /> 表示既没找到窗口，也没能把它抬起来。</para>
    /// </summary>
    public static async Task<bool> FocusGameAsync()
    {
        var process = _process;
        if (process is null) return false;

        try
        {
            if (process.HasExited) return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        // 先让游戏自己聚焦（顺带把键盘输入交回给画布），再抬窗口
        var server = _ipcServer;
        if (server is not null)
        {
            try
            {
                await server.SendPushAsync(LunarIpcPush.FocusGame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Lunar] 发送前台切换请求失败", ModBase.LogLevel.Debug);
            }
        }

        var handle = TryGetGameWindow(process.Id);
        if (handle == nint.Zero)
        {
            Log("没能把游戏窗口提到前台：还没找到游戏窗口");
            return false;
        }

        if (BringToForeground(handle))
        {
            Log("已把游戏窗口提到前台");
            return true;
        }

        Log("没能把游戏窗口提到前台：系统拒绝了前台切换");
        return false;
    }

    /// <summary>
    ///     找游戏的主窗口。
    ///     <para>
    ///         按窗口类名认（<c>LWJGL</c> = 1.8.9 这类老版本，<c>GLFW30</c> = 新版本，
    ///         <c>SunAwtFrame</c> = 走 AWT 的情况），再核对进程号，最后按尺寸排掉
    ///         GLFW 那个 0×0 的消息窗口。
    ///     </para>
    /// </summary>
    private static nint TryGetGameWindow(int processId)
    {
        var found = nint.Zero;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                var owner = 0;
                GetWindowThreadProcessId(hwnd, ref owner);
                if (owner != processId) return true;

                var buffer = new StringBuilder(64);
                GetClassName(hwnd, buffer, buffer.Capacity);
                var className = buffer.ToString();
                if (className is not ("LWJGL" or "GLFW30" or "SunAwtFrame")) return true;

                if (!GetWindowRect(hwnd, out var rect)) return true;
                if (rect.Right - rect.Left < 100 || rect.Bottom - rect.Top < 100) return true;

                found = hwnd;
                return false;
            }, nint.Zero);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 枚举游戏窗口失败", ModBase.LogLevel.Debug);
        }

        return found;
    }

    /// <summary>
    ///     把窗口抬到最前。最小化过就先还原。
    ///     <para>
    ///         直接 <c>SetForegroundWindow</c> 经常被系统拒绝（「不允许抢前台」），
    ///         惯用解法是把自己的输入队列临时挂到当前前台线程上，抬完再摘掉。
    ///     </para>
    /// </summary>
    private static bool BringToForeground(nint handle)
    {
        try
        {
            if (IsIconic(handle)) ShowWindow(handle, SwRestore);
            if (GetForegroundWindow() == handle) return true;

            var foreground = GetForegroundWindow();
            var foregroundThread = 0;
            if (foreground != nint.Zero) GetWindowThreadProcessId(foreground, ref foregroundThread);
            var currentThread = GetCurrentThreadId();

            var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                           AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }

            return GetForegroundWindow() == handle;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 切换游戏窗口到前台失败", ModBase.LogLevel.Debug);
            return false;
        }
    }

    private const uint SwRestore = 9;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32")]
    private static extern int GetWindowThreadProcessId(nint hwnd, ref int processId);

    [DllImport("user32")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32")]
    private static extern bool ShowWindow(nint hwnd, uint command);

    [DllImport("user32")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32")]
    private static extern bool BringWindowToTop(nint hwnd);

    [DllImport("user32")]
    private static extern bool AttachThreadInput(int attach, int attachTo, bool doAttach);

    [DllImport("kernel32")]
    private static extern int GetCurrentThreadId();

    /// <summary>
    ///     让已经在跑的游戏直接连某个服务器（<c>PlayServerPush</c>）。
    ///     <para>
    ///         游戏端要求当前**已经进了世界**，否则它只打一条日志就忽略 —— 这个判断
    ///         在游戏那边，PCL 看不到，所以返回 <c>true</c> 只代表「推过去了」。
    ///     </para>
    ///     <para>
    ///         PCL 没有服务器列表页，现在没有调用方；留着给「直接进服」这类入口。
    ///     </para>
    /// </summary>
    /// <param name="serverIp">形如 <c>mc.example.com</c> 或 <c>mc.example.com:25565</c>。</param>
    public static async Task<bool> JoinServerAsync(string serverIp)
    {
        if (string.IsNullOrWhiteSpace(serverIp)) return false;

        var server = _ipcServer;
        if (server is null) return false;

        try
        {
            var sent = await server.SendPushAsync(LunarIpcPush.PlayServer(serverIp)).ConfigureAwait(false);
            if (sent > 0) Log($"已请游戏直连服务器 {serverIp}");
            return sent > 0;
        }
        catch (Exception ex)
        {
            Log($"请游戏直连服务器失败：{ex.Message}");
            return false;
        }
    }

    #region 好友与私聊

    /// <summary>连接状态变了（连上 / 断开 / 正在连）。</summary>
    public static event Action? SocialStateChanged;

    /// <summary>好友列表刷新了。</summary>
    public static event Action? SocialFriendsChanged;

    /// <summary>收到一条私聊消息（会话 id、消息）。</summary>
    public static event Action<string, LunarChatMessage>? SocialMessageReceived;

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

            LunarSocialClient client;
            lock (SocialLock)
            {
                if (_social is null)
                {
                    _social = new LunarSocialClient(ReadOrCreateInstallationId());
                    _social.StateChanged += () => SocialStateChanged?.Invoke();
                    _social.FriendsChanged += () => SocialFriendsChanged?.Invoke();
                    _social.MessageReceived += (id, message) => SocialMessageReceived?.Invoke(id, message);
                }

                client = _social;
            }

            Log($"正在登录 Lunar 后端（账号 {name}）…");
            await client.ConnectAsync(token, uuid, name).ConfigureAwait(false);
            Log($"Lunar 后端已连上，好友 {client.Friends.Count} 个");
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

    #endregion

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

    private static async Task LaunchCoreAsync(string gameDirectory)
    {
        try
        {
            _stopRequested = false;
            var subVersion = SelectedSubVersion;
            GameDirectory = gameDirectory;

            // 内存 / JVM 参数 / Java 覆盖都在设置页里配，这里只负责解析出最终值
            var settings = LunarLaunchProfile.Resolve(subVersion, ResolvePclGlobalMemoryMb());
            LogSettings(settings);

            StatusText = Lang.Text("Lunar.Status.PreparingVersion", subVersion);
            DetailText = "";
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
            var javaPath = await LunarInstaller.EnsureInstalledAsync(
                    data, LunarRoot, settings.JavaPathOverride, verifyFiles, Log, ReportInstallProgress)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(javaPath))
                throw new InvalidOperationException(
                    $"没有找到 {subVersion} 需要的 Java（期望目录 jre/{data.JreFolderChecksum}），" +
                    "自动下载也没成功。可以在「设置 → Lunar」里手动指定一个 Java，或者检查网络。");

            JavaPath = javaPath;
            RaiseStateChanged();

            // 游戏会连 127.0.0.1:<ipcPort> 要账号续期。端口空闲就 PCL 自己接管；
            // 被占用说明 Lunar 启动器正开着，让它处理
            var ipc = await StartIpcAsync(installationId).ConfigureAwait(false);
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
            RaiseStateChanged();

            lock (_recentOutputLock) _recentOutput.Clear();
            var startedAt = DateTime.Now;
            // 上一轮的时间戳留着会让看门狗以为「刚吐过字」，必须归零
            Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);
            _lastCpuTime = TimeSpan.Zero;

            // 必须在 Start 之前问：Genesis 一起来就会开始写 versions/<ver>，事后再问就永远是「已就绪」
            var vanillaReady = IsVanillaReady(subVersion);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => CaptureGameOutput(e.Data ?? "");
            process.ErrorDataReceived += (_, e) => CaptureGameOutput(e.Data ?? "");
            process.Exited += (_, _) =>
            {
                // 退出码先取，_process 置空之后就拿不到了
                var exitCode = -1;
                try
                {
                    exitCode = process.ExitCode;
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    // 进程对象已经不可用，只能按未知处理
                }

                // 读完就复位，免得影响下一次启动
                var stopped = _stopRequested;
                _stopRequested = false;

                _process = null;
                IsBusy = false;
                // 游戏没了就没人需要续期了，端口让出来给别的程序
                StopIpc();

                var elapsed = DateTime.Now - startedAt;
                Log($"游戏进程已退出（退出码 {exitCode}{(stopped ? "，由 PCL 结束" : "")}，运行 {elapsed.TotalSeconds:F0} 秒）");

                // Windows 给被强制结束的进程的退出码是 -1。不是自己按的停止也可能是任务管理器
                // 或杀软干掉的，这种情况说「异常退出」同样是冤枉 —— 只有真正的非零退出才算崩
                var forced = stopped || exitCode == -1;
                var clean = exitCode == 0;

                // 异常退出，或者起来不到一分钟就没了 —— 这两种情况把最后几十行落进文件日志。
                // 界面日志框只在页面打开时才有人看，导出也容易漏，文件日志才是排查的依据。
                // 主动停止就别刷了，那不是问题
                if (!forced && (exitCode != 0 || elapsed < TimeSpan.FromMinutes(1)))
                {
                    var tail = SnapshotGameOutput();
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

                StatusText = forced
                    ? Lang.Text("Lunar.Status.Stopped")
                    : clean
                        ? Lang.Text("Lunar.Status.Exited")
                        : Lang.Text("Lunar.Status.Crashed", exitCode);
                DetailText = forced || clean ? "" : Lang.Text("Lunar.Status.CrashedDesc");
                RaiseStateChanged();
            };

            _process = process;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            WatchFirstWindow(process, gameDirectory, startedAt, subVersion, vanillaReady);

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
                RaiseStateChanged();
            }
        }
        catch (Exception ex)
        {
            _process = null;
            IsBusy = false;
            StopIpc();
            StatusText = Lang.Text("Lunar.Status.Failed");
            DetailText = ex.Message;
            RaiseStateChanged();
            ModBase.Log(ex, "[Lunar] 启动失败", ModBase.LogLevel.Msgbox, userSummary: ex.Message);
        }
    }

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
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!vanillaReady)
                    Log($"原版 {subVersion} 的文件还没补齐，Lunar 会自己把剩下的下到 {VanillaRoot}。" +
                        "这期间不会有窗口，属正常现象，别关进程。");

                var lastBytes = MeasureVanillaFootprint().Bytes;
                _lastCpuTime = ReadCpuTime(process);
                var stalled = 0;

                // 每 20 秒看一次，最多看 10 分钟：大文件下载慢的时候也不至于提前下结论
                for (var round = 1; round <= 30; round++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    if (process.HasExited) return;

                    // 已经进游戏了，看门狗可以收工
                    if (TryGetGameWindow(process.Id) != nint.Zero) return;
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
                        Log($"Lunar 自己在补原版依赖（PCL 那边没补完）：已 {footprint.Bytes / 1024d / 1024d:F0} MB、" +
                            $"{footprint.Files} 个文件，继续等待…");
                        lastBytes = footprint.Bytes;
                        stalled = 0;
                        continue;
                    }

                    // ③ 还在烧 CPU：解压、校验、生成 baked classes 这些都不打印日志
                    var cpu = ReadCpuTime(process);
                    if (cpu - _lastCpuTime > TimeSpan.FromSeconds(1))
                    {
                        _lastCpuTime = cpu;
                        lastBytes = footprint.Bytes;
                        stalled = 0;
                        continue;
                    }

                    lastBytes = footprint.Bytes;
                    stalled += 1;
                    if (stalled < 3) continue;

                    Log($"启动 {round * 20} 秒后既没有窗口、没有日志、原版依赖也不再变大 —— " +
                        "大概率是 Genesis 在下载依赖时被网络卡住了");
                    HintService.Hint(Lang.Text("Lunar.Hint.Stalled"), HintType.Warning);
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

        // ① 用户在账号下拉框里明确点了「PCL Lite 登录账号」
        if (SelectedPclProfile is { } pclProfile)
            return await ResolveFromPclProfileAsync(pclProfile).ConfigureAwait(false);

        var account = SelectedAccount;

        if (account is not null)
        {
            // 游戏进程读的是 Lunar 自己的激活账号，这里同步一下，
            // 否则「界面上选的是 A、进游戏是 B」
            if (LunarAccountStore.SetActive(LunarRoot, account.LocalId))
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

        // ③ Lunar 账户库里一个账号都没有。典型情况是从没装过、或装过但从没在 Lunar 启动器里登录。
        // 这种时候只要 PCL Lite 自己登录过微软账号就够用，不必先去 Lunar 登录一次
        var pclAccount = ProfileService.Current;
        if (pclAccount is null || pclAccount.ProfileType != ProfileType.Microsoft ||
            string.IsNullOrWhiteSpace(pclAccount.AccessToken))
        {
            pclAccount = PclProfiles.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.AccessToken));
        }

        if (pclAccount is not null)
        {
            Log("Lunar 账户库里没有账号，改用 PCL Lite 登录的账号");
            return await ResolveFromPclProfileAsync(pclAccount).ConfigureAwait(false);
        }

        Log("Lunar 账户库和 PCL 账号列表都是空的，尝试走一次主页面登录");

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
                "PCL 里还没有登录任何账号。请在主页面登录微软账号后重试 —— 不需要登录 Lunar 启动器。");

        Log($"账号：{login.Name}（PCL 登录）");
        return (login.Name, NormalizeUuid(login.Uuid), login.AccessToken);
    }

    /// <summary>
    ///     用 PCL 自己登录的账号启动。
    ///     <para>
    ///         这条路的坑在于：游戏进程**不读**命令行里的令牌，它按 <c>accounts.json</c>
    ///         的激活账号决定「我是谁」。所以这里会顺手在 Lunar 账户库里找同名（同 UUID）
    ///         的账号并把激活账号切过去；找不到就只能启动，进游戏仍是 Lunar 原来那个身份。
    ///     </para>
    /// </summary>
    private static async Task<(string Name, string Uuid, string Token)> ResolveFromPclProfileAsync(
        McProfile profile)
    {
        Log($"账号：{profile.UserName}（PCL Lite 登录）");
        // 用的是 PCL 自己的档案，续期自然也能自己做
        _canRefreshSession = true;

        var uuid = NormalizeUuid(profile.Uuid);
        var twin = _accounts.FirstOrDefault(x =>
            string.Equals(NormalizeUuid(x.MinecraftUuid), uuid, StringComparison.OrdinalIgnoreCase));

        var token = profile.AccessToken;
        DateTimeOffset? tokenExpiresAt = profile.ExpiresAt;
        if (profile.IsExpired)
        {
            Log("PCL 令牌已过期，先刷新一次…");
            try
            {
                var refreshed = await new MicrosoftProvider()
                    .RefreshAsync(profile, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(refreshed.AccessToken))
                {
                    token = refreshed.AccessToken;
                    tokenExpiresAt = refreshed.ExpiresAt ?? tokenExpiresAt;
                    ProfileService.ApplyAuthenticationResult(refreshed, profile, select: false);
                    Log($"令牌已刷新，到期 {refreshed.ExpiresAt?.ToLocalTime():yyyy-MM-dd HH:mm}");
                }
            }
            catch (Exception ex)
            {
                Log("刷新失败，仍然用旧令牌启动：" + ex.Message);
            }
        }

        if (twin is not null && LunarAccountStore.SetActive(LunarRoot, twin.LocalId))
        {
            _launchedLocalId = twin.LocalId;
            Log($"Lunar 账户库里有同一个 UUID 的账号，激活账号已切过去（{twin.LocalId}）");
        }
        else if (!string.IsNullOrWhiteSpace(token) &&
                 LunarAccountStore.Upsert(LunarRoot, Guid.NewGuid().ToString("N"), profile.UserName,
                     profile.UserName, uuid.Replace("-", string.Empty), token, tokenExpiresAt))
        {
            // 账户库里没有这个号：直接补一条并切过去。以前这里只写日志然后照旧启动，
            // 结果游戏读 activeAccountLocalId，进游戏还是 Lunar 原来那个账号 ——
            // 用户看到的就是「在 PCL 里选了 A，游戏里是 B」
            RefreshAccounts();
            var added = _accounts.FirstOrDefault(x =>
                string.Equals(NormalizeUuid(x.MinecraftUuid), uuid, StringComparison.OrdinalIgnoreCase));
            _launchedLocalId = added?.LocalId ?? string.Empty;
            Log($"Lunar 账户库里没有这个 UUID，已按 PCL 登录新建一条并切成激活账号（{added?.LocalId}）");
        }
        else
        {
            Log("Lunar 账户库写入失败：游戏内身份仍由 Lunar 原来的激活账号决定，" +
                "这里只换了命令行令牌（联机用这个号，好友列表是另一个号）");
        }

        return (profile.UserName, uuid, token);
    }

    /// <summary>
    ///     抢占 Lunar 的 IPC 端口。抢到了就由 PCL 应答游戏的续期请求，
    ///     抢不到（Lunar 启动器在跑）就把端口原样交给游戏。
    /// </summary>
    private static async Task<LunarIpcStartResult> StartIpcAsync(string installationId)
    {
        StopIpc();

        // 能自己续期时不要轻易把端口让给 Lunar 启动器：启动器开着但没登录的话，
        // 游戏找它续期只会失败。28190 抢不到就退到 28191，把端口指给游戏。
        var fallback = _canRefreshSession ? new[] { LunarIpcServer.DefaultPort + 1 } : null;

        var result = await LunarIpcServer
            .StartAsync(LunarIpcServer.DefaultPort, installationId, RefreshLunarAccountAsync, Log,
                fallbackPorts: fallback, host: IpcHost)
            .ConfigureAwait(false);

        _ipcServer = result.Server;
        return result;
    }

    private static void StopIpc()
    {
        _ipcServer?.Dispose();
        _ipcServer = null;
    }

    /// <summary>
    ///     应答游戏端的 <c>AuthService/RefreshAccount</c>。
    ///     <para>
    ///         Lunar 启动器收到这个请求时用的是它自己 OAuth 应用的 refreshToken，
    ///         那个令牌绑死在 Lunar 的应用上（拿它换新令牌会得到 <c>AADSTS70000</c>），
    ///         PCL 用不了。这里改走另一条路：用 **PCL 自己登录**的微软账号刷一个
    ///         新的 Minecraft 令牌，写回 <c>accounts.json</c>。
    ///     </para>
    ///     <para>
    ///         所以前提是 PCL 主页面登录了同一个账号；不是同一个号就宁可失败，
    ///         也不能把别人的令牌写进这条账号里（令牌和 UUID 是绑定的）。
    ///     </para>
    /// </summary>
    private static async Task<LunarRefreshResult> RefreshLunarAccountAsync(Guid minecraftUuid,
        CancellationToken cancellationToken)
    {
        var wanted = NormalizeUuid(minecraftUuid.ToString());

        RefreshAccounts();
        if (_accounts.Count == 0)
        {
            // 游戏随时在写这个文件，偶尔会正好读到写了一半的内容，重读一次再下结论
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            RefreshAccounts();
        }

        var account = FindRefreshTarget(wanted);
        if (account is null)
            return LunarRefreshResult.Fail(LunarAccountError.AccountsFileEmpty,
                $"Lunar 账户库里没有 UUID {wanted}（现有：{DescribeAccountUuids()}）");

        if (FindPclProfileFor(account) is null)
            return LunarRefreshResult.Fail(LunarAccountError.MissingRefreshToken,
                $"PCL 里没有登录 {account.DisplayName}（{account.MinecraftUuid}），没有可用的 refreshToken");

        // 落盘在 RefreshAccountTokenAsync 里做完了才返回 —— 游戏拿到 success 会立刻重读文件
        var fresh = await RefreshAccountTokenAsync(account, cancellationToken).ConfigureAwait(false);
        return fresh is { } value
            ? LunarRefreshResult.Ok(value.Message)
            : LunarRefreshResult.Fail(LunarAccountError.MissingRefreshToken,
                "没能换到新的 accessToken（详见日志）");
    }

    /// <summary>
    ///     在账户库里找到这次要续期的账号。
    ///     <para>
    ///         按可靠性从高到低试三种认法：UUID → <c>localId</c> → 本次启动用的那个。
    ///         最后那条不是瞎猜：启动前已经把 Lunar 的激活账号切成了它，游戏问「当前账号」
    ///         时指的必然是它。只有账户库里真有多个账号、且请求的 UUID 一个都对不上时
    ///         才会走到那里，日志里会写明是兜底命中的。
    ///     </para>
    /// </summary>
    private static LunarAccount? FindRefreshTarget(string wantedUuid)
    {
        var byUuid = _accounts.FirstOrDefault(x =>
            string.Equals(NormalizeUuid(x.MinecraftUuid), wantedUuid, StringComparison.OrdinalIgnoreCase));
        if (byUuid is not null) return byUuid;

        var byLocalId = _accounts.FirstOrDefault(x =>
            string.Equals(NormalizeUuid(x.LocalId), wantedUuid, StringComparison.OrdinalIgnoreCase));
        if (byLocalId is not null) return byLocalId;

        if (!string.IsNullOrWhiteSpace(_launchedLocalId))
        {
            var launched = _accounts.FirstOrDefault(x => x.LocalId == _launchedLocalId);
            if (launched is not null)
            {
                Log($"[续期] 请求的 UUID {wantedUuid} 不在账户库里，" +
                    $"按本次启动用的账号 {launched.DisplayName}（{launched.MinecraftUuid}）处理");
                return launched;
            }
        }

        // 只有一个账号时也不用猜
        return _accounts.Count == 1 ? _accounts[0] : null;
    }

    /// <summary>
    ///     找 PCL 里对应的登录。**先按 UUID 认，再按名字认** ——
    ///     改过名的账号 UUID 不变，只比名字会认错人。
    /// </summary>
    /// <summary>
    ///     在 PCL 的账号列表里找与 <paramref name="account" /> 对应的登录（UUID 优先，其次同名）。
    ///     <para>页面用它判断「这个 Lunar 账号能不能由 PCL 续期」，规则必须和续期时一致。</para>
    /// </summary>
    public static McProfile? MatchPclProfile(LunarAccount account) => FindPclProfileFor(account);

    private static McProfile? FindPclProfileFor(LunarAccount account)
    {
        var uuid = NormalizeUuid(account.MinecraftUuid);
        var profiles = PclProfiles;

        return profiles.FirstOrDefault(x =>
                   string.Equals(NormalizeUuid(x.Uuid), uuid, StringComparison.OrdinalIgnoreCase))
               ?? profiles.FirstOrDefault(x =>
                   string.Equals(x.UserName, account.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>失败信息里带上账户库现有的 UUID，下次一眼就能看出对不上的是哪个。</summary>
    private static string DescribeAccountUuids() =>
        _accounts.Count == 0
            ? "账户库是空的"
            : string.Join("、", _accounts.Select(x =>
                $"{x.DisplayName}={NormalizeUuid(x.MinecraftUuid)}"));

    /// <summary>刷新出来的令牌，附带一句给日志看的说明。</summary>
    private readonly record struct LunarTokenRefresh(string Token, DateTimeOffset? ExpiresAt, string Message);

    private static string FormatExpiry(DateTimeOffset? expiresAt) =>
        expiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";

    /// <summary>
    ///     把账户库里这个账号的令牌换成新鲜的，并写回 <c>accounts.json</c>。
    ///     <para>
    ///         用的一定是 PCL 里**同一个账号**的登录（UUID 优先，其次同名）——令牌和 UUID
    ///         绑在一起，拿别人的令牌联机会被服务器直接踢。PCL 那份过期了就当场刷一次
    ///         （用档案里已有的 refreshToken，不弹窗）。
    ///     </para>
    ///     <para>
    ///         找不到能刷的登录、或者微软拒绝刷新时返回 <see langword="null" />，
    ///         由调用方决定是照旧启动还是提示用户重新登录。
    ///     </para>
    /// </summary>
    private static async Task<LunarTokenRefresh?> RefreshAccountTokenAsync(LunarAccount account,
        CancellationToken cancellationToken)
    {
        McProfile? profile;
        try
        {
            profile = FindPclProfileFor(account);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 读取 PCL 登录凭据失败", ModBase.LogLevel.Debug);
            return null;
        }

        if (profile is null)
        {
            Log($"PCL 里没有 {account.DisplayName}（{NormalizeUuid(account.MinecraftUuid)}）的登录，" +
                "没法替它续期");
            return null;
        }

        var token = profile.AccessToken;
        var expiresAt = profile.ExpiresAt;

        if (profile.IsExpired || string.IsNullOrWhiteSpace(token))
        {
            Log($"PCL 里 {profile.UserName} 的令牌已过期（{FormatExpiry(expiresAt)}），正在刷新…");
            AuthenticationResult refreshed;
            try
            {
                refreshed = await new MicrosoftProvider()
                    .RefreshAsync(profile, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("刷新令牌失败：" + ex.Message);
                return null;
            }

            if (string.IsNullOrWhiteSpace(refreshed.AccessToken))
            {
                Log("微软没有返回新的 accessToken");
                return null;
            }

            token = refreshed.AccessToken;
            expiresAt = refreshed.ExpiresAt;
            ProfileService.ApplyAuthenticationResult(refreshed, profile, select: false);
            Log($"刷新成功，新令牌到期 {FormatExpiry(expiresAt)}");
        }

        // 游戏进程读的是 accounts.json，命令行里的令牌只是兜底，所以必须写回去
        var written = LunarAccountStore.UpdateToken(LunarRoot, account.LocalId, token, expiresAt);
        RefreshAccounts();

        return new LunarTokenRefresh(token, expiresAt, written
            ? $"取自 PCL 登录（{profile.UserName}），到期 {FormatExpiry(expiresAt)}"
            : $"取自 PCL 登录（{profile.UserName}），但写回 accounts.json 失败，游戏内可能仍是旧令牌");
    }

    /// <summary>
    ///     Lunar 的 <c>installation_id</c> 存在 <c>launcher-cache/installation-id</c> 下。
    ///     <para>
    ///         有就复用，没有就生成一个**并落盘** —— 每次启动都换一个 GUID 的话，
    ///         服务端会把同一台机器当成无数次全新安装。
    ///     </para>
    /// </summary>
    private static string ReadInstallationId()
    {
        var path = Path.Combine(LunarClientService.LauncherCacheDirectory(LunarRoot), "installation-id");
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (Guid.TryParse(text, out var existing)) return existing.ToString();
            }

            var created = Guid.NewGuid();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, created.ToString());
            return created.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去就现生成一个，不影响启动
            return Guid.NewGuid().ToString();
        }
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
