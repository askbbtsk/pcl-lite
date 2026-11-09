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
public static partial class ModLunar
{
    /// <summary>没选过版本时的默认值，也是唯一一个「本机一定有物料」的保底版本。</summary>
    public const string DefaultSubVersion = "1.8.9";

    /// <summary>
    ///     拿不到版本元数据时用的 loader。<c>lunar</c> 就是「Lunar + OptiFine」，
    ///     也是 1.8.9 的官方默认，所以当兜底是安全的。
    /// </summary>
    public const string FallbackModule = "lunar";

    /// <summary>
    ///     Lunar 官方启动器自己的 Azure 应用 client id。
    ///     <para>
    ///         <b>这不是 PCL 的凭据</b>，是从 Lunar 官方启动器的 <c>main.js</c> 里提取的明文常量
    ///         （public client，没有 client secret）。只在 Lunar 这条链路上使用，PCL 自己的
    ///         微软登录仍然走 <see cref="Secrets.MSOAuthClientId" />，两者互不影响。
    ///     </para>
    ///     <para>
    ///         <b>为什么必须用它</b>：Lunar 游戏端按 JWT 里的 <c>aid</c> 选刷新实现
    ///         （<c>aid === Lunar 的 client id</c> → 走标准 OAuth；否则落到 SISU 分支，
    ///         需要 deviceToken 和密钥对，PCL 造不出来）。用 PCL 自己的 client id 签出来的
    ///         refreshToken，<c>aid</c> 对不上，游戏启动时会卡在 <c>Connecting...</c>。
    ///     </para>
    ///     <para>
    ///         风险：授权会记在 Lunar 的 Azure 应用名下；Lunar 单方面改这个应用注册
    ///         （关掉 public client、改 redirect URI、删应用）会让这条链路直接失效。
    ///     </para>
    /// </summary>
    private const string LunarOAuthClientId = PCL.Core.App.Secrets.LunarOAuthClientId;

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
    ///     页面打开时的令牌预热任务（单飞用）。见 <see cref="PrepareSessionAsync" />。
    /// </summary>
    private static Task? _prepareSessionTask;

    /// <summary>上一次预热的时间，用来做节流，避免来回切页面反复打微软的刷新接口。</summary>
    private static DateTime _lastPrepareUtc = DateTime.MinValue;

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

    /// <summary>
    ///     账号选择里「Lunar 身份账号」的前缀，和 Lunar 账户库的 <c>localId</c> 区分开。
    ///     <para>
    ///         带这个前缀的账号是用户在 Lunar 页面用 Lunar 的 client id 登录出来的，
    ///     和 PCL 主页面的登录完全是两套。
    ///     </para>
    /// </summary>
    public const string IdentitySelectionPrefix = "identity:";

    public static event Action? StateChanged;

    public static event Action<string>? LogReceived;

    public static string StatusText { get; private set; } = "正在检测 Lunar Client…";

    public static string DetailText { get; private set; } = "";

    /// <summary>
    ///     当前物料准备阶段的百分比（0–100）；负数表示没有可展示的确定进度
    ///     （不在准备阶段，或当前阶段是条数不定的步骤如 JRE），界面据此隐藏进度条。
    /// </summary>
    public static double InstallPercent { get; private set; } = -1;

    /// <summary>最近一次启动的完整命令行，出错时贴出来方便定位。</summary>
    public static string LastCommandLine { get; private set; } = "";

    /// <summary>最近一次启动用到的 Java，未启动过时为空。</summary>
    public static string JavaPath { get; private set; } = "";

    /// <summary>最近一次启动实际使用的游戏目录（即 <c>--gameDir</c>）。</summary>
    public static string GameDirectory { get; private set; } = "";

    /// <summary>
    ///     安装进度的节流状态。物料阶段是并发下载，一秒能回调几十次；
    ///     每次都往 UI 线程丢一条就会把界面刷成幻灯片。
    /// </summary>
    private static string _lastProgressStage = "";

    private static int _lastProgressPercent = -1;


    /// <summary>连接状态变了（连上 / 断开 / 正在连）。</summary>
    public static event Action? SocialStateChanged;

    /// <summary>好友列表刷新了。</summary>
    public static event Action? SocialFriendsChanged;

    /// <summary>收到一条私聊消息（会话 id、消息）。</summary>
    public static event Action<string, LunarChatMessage>? SocialMessageReceived;
}
