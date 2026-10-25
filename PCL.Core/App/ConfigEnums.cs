namespace PCL.Core.App;

/// <summary>
/// 联机协议偏好
/// </summary>
public enum LinkProtocolPreference
{
    Tcp,
    Udp
}

/// <summary>
/// 文件下载使用的 HTTP 协议模式。
/// </summary>
public enum DownloadHttpMode
{
    Auto = 0,
    Http11 = 1,
    Http2 = 2
}

/// <summary>
/// 主题模式（亮/暗/系统）
/// </summary>
public enum ColorMode
{
    Light = 0,
    Dark = 1,
    System = 2
}

/// <summary>
/// 配色主题
/// </summary>
public enum ColorTheme
{
    SkyBlue = 0,
    CatBlue = 1,
    DeathBlue = 2,
    HmclBlue = 3
}

/// <summary>
/// 更新通道。
/// <para>
/// 四个选项都是**包含式**的：越往下越激进，但依然收上面所有更稳定的版本。
/// 这样「始终最新」的用户在正式版发布后也能升到正式版，不会因为手里的
/// 预发布版本号比新正式版大而卡死。
/// </para>
/// <para>
/// 数值与旧的 Release / Beta / Dev 对得上（Release 落在 Stable、Beta 落在 Preview），
/// 老配置不需要迁移。
/// </para>
/// </summary>
public enum UpdateChannel
{
    /// <summary>稳定版：只收正式版，一条预发布都不收。</summary>
    Stable = 0,

    /// <summary>预览体验：正式版 + 预览版（pre / beta）。</summary>
    Preview = 1,

    /// <summary>快照尝鲜：在预览体验基础上再加快照版（snapshot）。</summary>
    Snapshot = 2,

    /// <summary>始终最新：全部，含不稳定版（nightly）。</summary>
    Latest = 3
}

/// <summary>
/// 游戏窗口大小模式
/// </summary>
public enum GameWindowSizeMode
{
    Fullscreen = 0,
    Default = 1,
    Launcher = 2,
    Custom = 3,
    Maximized = 4
}

/// <summary>
/// 游戏进程优先级
/// </summary>
public enum GameProcessPriority
{
    AboveNormal = 0,
    Normal = 1,
    BelowNormal = 2,
    High = 3,
    RealTime = 4
}

/// <summary>
/// 游戏启动后启动器可见性
/// </summary>
public enum LauncherVisibility
{
    ExitImmediately = 0,
    ObsoleteCaseDoNotUse = 1,
    HideAndExit = 2,
    HideAndReopen = 3,
    MinimizeAndReopen = 4,
    DoNothing = 5
}

/// <summary>
/// JVM 优先 IP 栈类型
/// </summary>
public enum JvmPreferredIpStack
{
    PreferV4 = 0,
    Default = 1,
    PreferV6 = 2
}

/// <summary>
/// 联机中继行为
/// </summary>
public enum LinkRelayBehavior
{
    Default = 0,
    ForceRelay = 1
}

/// <summary>
/// 启动器更新行为
/// </summary>
public enum LauncherAutoUpdateBehavior
{
    DownloadAndInstall = 0,
    DownloadAndAnnounce = 1,
    AnnounceOnly = 2,
    Disable = 3
}

public enum LauncherTitleType
{
    None = 0,
    Default = 1,
    Text = 2,
    Image = 3
}
