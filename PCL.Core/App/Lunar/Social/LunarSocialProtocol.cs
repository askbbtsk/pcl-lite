using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.App.Lunar.Social;

/// <summary>
///     Lunar 后端（<c>wss://websocket.lunarclientprod.com/launcher-v3</c>）的协议编解码。
///     <para>
///         结构是三层：<b>WebSocket 帧</b> → <b>信封</b> → <b>业务消息</b>。
///         握手是一条裸的 <c>Handshake</c>（没有信封），握手之后每条帧都是
///         <c>ClientboundWebSocketMessage</c> / <c>ServerboundWebSocketMessage</c>。
///     </para>
///     <code>
///     ServerboundWebSocketMessage { bytes request_id = 1; string service = 2; string method = 3; bytes input = 4; }
///     ClientboundWebSocketMessage { WebSocketRpcResponse rpc_response = 1; google.protobuf.Any push_notification = 2; }
///     WebSocketRpcResponse       { bytes request_id = 1; bytes output = 2; }
///     </code>
///     <para>
///         字段号全部来自官方启动器 <c>app.asar</c> 里 protobufjs 生成的 <c>encode</c> 代码，
///         不是猜的。取法：每个消息的 <c>encode</c> 里 <c>uint32(tag)</c> 就是
///         <c>(字段号 &lt;&lt; 3) | wire_type</c>。
///     </para>
/// </summary>
internal static partial class LunarSocialProtocol
{
    public const string AuthenticatorUrl = "wss://authenticator.lunarclientprod.com/launcher-v3";
    public const string AssetServerUrl = "wss://websocket.lunarclientprod.com/launcher-v3";

    /// <summary>认证握手时告诉服务端「这次是谁在用」。官方启动器传的就是这个常量。</summary>
    public const string LauncherInitiator = "LAUNCHER_WEBSOCKET";

    public const string FriendService = "lunarclient.websocket.friend.v1.FriendService";
    public const string ConversationService = "lunarclient.websocket.conversation.v1.ConversationService";
    public const string HeartbeatService = "lunarclient.websocket.heartbeat.v1.HeartbeatService";

    /// <summary><c>MinecraftIdentity_Type.TYPE_MICROSOFT</c>。</summary>
    private const int IdentityMicrosoft = 2;

    /// <summary>
    ///     没装官方启动器时用的版本号。
    ///     <para>
    ///         <b>PCL 用户大多数没装官方启动器</b> —— 物料是 PCL 自己从服务端下的，
    ///         根本不需要那个 Electron 壳。所以这条不是边缘分支，是主路径。
    ///     </para>
    ///     <para>
    ///         服务端只校验<b>下限</b>，没有上限：实测 3.7.0 被关码 1008 拒绝、
    ///         3.7.20 起放行，4.0.0 与 99.0.0 同样放行。所以兜底值要挑一个离当前
    ///         下限有余量的，不能填刚好卡在边上的值 —— 填 3.7.27 那种，Lunar 下一次
    ///         抬下限就整条链路失效。
    ///     </para>
    ///     <para>
    ///         <b>优先走在线查版本</b>（见 <see cref="FetchLatestLauncherVersionAsync" />），
    ///         查不到才用这个兜底值。在线查到的版本是 Lunar 官方启动器的最新正式版本号，
    ///         服务端永远认。断网且本机也没装官方启动器时，才落回这里。
    ///     </para>
    /// </summary>
    private const string FallbackLauncherVersion = "4.0.0";

    /// <summary>
    ///     首选版本被服务端拒掉之后，退而求其次报的版本号。
    ///     <para>
    ///         既然只有下限没有上限，报一个远高于任何真实版本的号必然满足下限。
    ///         只在 <c>1008 Unsupported launcher version</c> 之后才用，常态下不会走到。
    ///     </para>
    /// </summary>
    private const string EscalatedLauncherVersion = "99.0.0";

    /// <summary>
    ///     Lunar 官方启动器的版本号来源。
    ///     <para>
    ///         官方启动器（Electron 壳）自己就在用这套地址：<c>/metadata/misc</c> 给出
    ///         <c>updater.feedUrl</c> 与 <c>updater.channel</c>，再由
    ///         <c>{feedUrl}/{channel}.yml</c>（electron-builder 的频道清单）读出
    ///         <c>version: x.y.z-ow</c>。剪掉 <c>-ow</c> 就是握手要的 semver。
    ///     </para>
    ///     <para>
    ///         <b>为什么要联网查</b>：服务端只校验下限，而这个下限跟着官方启动器往上涨。
    ///         写死任何一个具体版本，迟早会被 <c>1008 Unsupported launcher version</c> 踢掉。
    ///         查到的结果缓存 6 小时，一次启动最多一次请求。
    ///     </para>
    /// </summary>
    private const string LunarApiBase = "https://api.lunarclientprod.com/launcher";

    private const string DefaultUpdateFeedUrl =
        "https://launcher-releases.lunarclientprod.com/master/6e18b5a4f4ebbd4c7ecff572f1700cedce1f248bcb157adce5ec49be9e081a23";

    private const string DefaultUpdateChannel = "latest-ow";

    /// <summary>
    ///     查版本时用的 UA。不带版本号，避免「先有鸡还是先有蛋」——
    ///     这两个接口都不校验 UA（实测不带版本号也能正常拿到数据）。
    /// </summary>
    private const string BootstrapUserAgent = "Lunar Client Launcher";

    private static readonly TimeSpan OnlineVersionTtl = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim OnlineVersionGate = new(1, 1);

    private static readonly HttpClient VersionHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static string? _onlineVersion;
    private static long _onlineVersionExpiresAt;

    /// <summary><c>LauncherHandshake_ElectronBinariesType.ELECTRON_BINARIES_TYPE_ELECTRON</c>。</summary>
    private const int ElectronBinariesElectron = 2;

    /// <summary>会话类型：<c>ConversationType.CONVERSATION_TYPE_FRIEND</c>，也就是一对一私聊。</summary>
    public const int ConversationTypeFriend = 1;

}
