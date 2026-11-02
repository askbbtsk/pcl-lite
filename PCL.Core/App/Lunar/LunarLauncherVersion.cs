using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App.Lunar.Social;

namespace PCL.Core.App.Lunar;

/// <summary>
///     上报给 Lunar 服务端的「启动器版本号」。
///     <para>
///         <b>别和 <c>--launcherVersion</c> 搞混</b>：那个是传给 Genesis 的游戏侧参数，
///         恒为 <c>3.3.0</c>（见 <see cref="LunarLaunchOptions.GameLauncherVersion" />）。
///         这里这个是握手 <c>launcherVersion.semver</c>、<c>/launch</c> 请求体里的
///         <c>launcher_version</c> 以及 <c>User-Agent</c> 用的值 —— 服务端会校验它的
///         <b>下限</b>，而这个下限跟着官方启动器往上涨，所以必须是<b>官方最新正式版</b>。
///     </para>
///     <para>
///         取值优先级：在线查到的官方最新正式版 → 本机官方启动器的 <c>FileVersion</c>
///         → 内置兜底值。在线查询带 6 小时缓存。
///     </para>
/// </summary>
public static class LunarLauncherVersion
{
    /// <summary>
    ///     当前使用的版本号。<b>不会发网络请求</b>，只读缓存 / 本机文件。
    ///     <para>启动早期请先 <see cref="ResolveAsync" /> 预热一次。</para>
    /// </summary>
    public static string Current => LunarSocialProtocol.ResolveLauncherVersion();

    /// <summary>同 <see cref="Current" />，但会主动去查一次（带缓存）。</summary>
    public static Task<string> ResolveAsync(CancellationToken cancellationToken = default) =>
        LunarSocialProtocol.ResolveLauncherVersionAsync(cancellationToken);

    /// <summary>
    ///     只查「官方最新正式版」，拿不到返回 <see langword="null" />，不带任何本机回退。
    ///     <para>诊断 / 设置页展示用。</para>
    /// </summary>
    public static Task<string?> FetchLatestAsync(CancellationToken cancellationToken = default) =>
        LunarSocialProtocol.FetchLatestLauncherVersionAsync(cancellationToken);

    /// <summary>官方启动器格式的 UA：<c>Lunar Client Launcher v{版本}</c>。</summary>
    public static string UserAgent => LunarSocialProtocol.LauncherUserAgent;
}
