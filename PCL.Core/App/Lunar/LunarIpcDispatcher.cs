using System;
using System.IO;
using System.Text;

namespace PCL.Core.App.Lunar;

/// <summary>
///     PCL 侧对 IPC 请求的响应能力。
///     <para>
///         协议层只管编解码，真正「打开浏览器」「切账号页」「重开游戏」这些必须由 UI 层做，
///         所以抽成接口注入。<see cref="LunarIpcDispatcher" /> 不认识的调用一律回空响应。
///     </para>
/// </summary>
public interface ILunarIpcHost
{
    /// <summary>
    ///     游戏请求打开一个链接（商店、公告、帮助、社交链接都走这条）。
    ///     返回是否真的打开了。
    /// </summary>
    bool OpenUrl(string url, LunarUrlInitiator initiator, LunarOpenType openType);

    /// <summary>游戏内点了「添加账号」。PCL 不能替游戏登录，只能把用户带到账号页。</summary>
    void RequestAddAccount();

    /// <summary>
    ///     游戏内请求「换版本重开」。
    ///     <paramref name="version" /> 形如 <c>1.21.4</c>，<paramref name="module" /> 是
    ///     <c>lunar</c> / <c>lunar-noOF</c> / <c>fabric</c> / <c>forge</c>，都可能为空。
    /// </summary>
    void RequestLaunchGame(string version, string module, string serverIp, int launchSource);
}

/// <summary>链接是从哪儿点出来的，取值与 <c>OpenUrlRequest_Initiator</c> 一一对应。</summary>
public enum LunarUrlInitiator
{
    Unspecified = 0,
    Cosmetics = 1,
    Alert = 2,
    Auth = 3,
    Blog = 4,
    SocialLink = 5,
    HomeButton = 6,
    LinkConfirm = 7,
    ScreenshotUpload = 8,
    HomeCta = 9,
    StorePreview = 10
}

/// <summary>打开方式，取值与 <c>OpenUrlRequest_OpenType</c> 一一对应。</summary>
public enum LunarOpenType
{
    Unspecified = 0,
    ForceEmbedded = 1,
    ForceExternal = 2
}

/// <summary>
///     把一次 RPC 分派到对应的处理逻辑，返回响应体字节。
///     <para>
///         这里的所有消息定义都来自官方启动器 <c>app.asar</c> 里 protobufjs 的生成代码
///         （<c>dist-electron/electron/main.js</c>），字段号是反编译出来的，不是猜的。
///     </para>
///     <para>
///         <b>回空字节是安全且有意义的</b>：protobuf 解空内容得到的是全默认实例。
///         所以「能力探测」类请求只要默认值就代表「不支持」，没必要显式编码一个 0。
///         反过来，非默认值必须写出来 —— 比如 <c>OpenTebexJsCheckoutResponse.status</c>
///         要的是 <c>STATUS_INTERNAL_ERROR = 2</c>，不写就是 0，游戏会以为成功了。
///     </para>
/// </summary>
internal static class LunarIpcDispatcher
{
    // 官方启动器在「checkout 模块不存在」时返回的 status：
    // STATUS_UNSPECIFIED = 0、STATUS_OK = 1、STATUS_INTERNAL_ERROR = 2
    private const int CheckoutStatusInternalError = 2;

    public static byte[] Dispatch(LunarIpcRequest request, ILunarIpcHost? host, Action<string> log)
    {
        var output = request.Service switch
        {
            LunarIpcProtocol.AuthService => DispatchAuth(request, host, log),
            LunarIpcProtocol.BrowserService => DispatchBrowser(request, host, log),
            LunarIpcProtocol.ChatService => DispatchChat(request, log),
            LunarIpcProtocol.LaunchService => DispatchLaunch(request, host, log),
            LunarIpcProtocol.LocationService => DispatchLocation(request, host, log),
            LunarIpcProtocol.PromotionService => DispatchPromotion(request, log),
            LunarIpcProtocol.TebexService => DispatchTebex(request, log),
            LunarIpcProtocol.PayNowService => DispatchPayNow(request, log),
            LunarIpcProtocol.StyngrRadioService => DispatchStyngr(request, log),
            _ => Unknown(request, log)
        };

        return output;
    }

    private static byte[] Unknown(LunarIpcRequest request, Action<string> log)
    {
        log($"未实现的 IPC 调用：{request.Service}/{request.Method}，回空响应");
        return [];
    }

    #region auth.v1.AuthService

    private static byte[] DispatchAuth(LunarIpcRequest request, ILunarIpcHost? host, Action<string> log)
    {
        // RefreshAccount 在 LunarIpcServer 里单独处理（要拿令牌、要落盘），到不了这里
        if (request.Method != "AddAccount") return Unknown(request, log);

        // 官方启动器是调自己的 auth 模块加号；PCL 的账号只能在 PCL 里加，
        // 所以这里只能把用户带到账号页，然后老实回 success = false。
        log("游戏请求添加账号：PCL 不能代替游戏登录，已把窗口切到账号页");
        host?.RequestAddAccount();

        // AddAccountResponse { success: false, error: UNSPECIFIED } —— 全是默认值
        return [];
    }

    #endregion

    #region browser.v1.BrowserService

    private static byte[] DispatchBrowser(LunarIpcRequest request, ILunarIpcHost? host, Action<string> log)
    {
        if (request.Method != "OpenUrl") return Unknown(request, log);

        // OpenUrlRequest { string url = 1; Initiator initiator = 2; OpenType openType = 3; }
        LunarIpcProtocol.TryGetString(request.Input, 1, out var url);
        LunarIpcProtocol.TryGetInt32(request.Input, 2, out var initiator);
        LunarIpcProtocol.TryGetInt32(request.Input, 3, out var openType);

        var kind = (LunarUrlInitiator)initiator;
        var mode = (LunarOpenType)openType;

        if (string.IsNullOrWhiteSpace(url))
        {
            log("OpenUrl 的请求里没有 url，忽略");
            return [];
        }

        log($"游戏请求打开链接（{kind} / {mode}）：{url}");

        var opened = host?.OpenUrl(url, kind, mode) ?? false;
        if (!opened) log("没能打开这个链接（宿主未接管或 URL 不被接受）");

        // OpenUrlResponse 是空消息
        return [];
    }

    #endregion

    #region chat.v1.ChatService

    private static byte[] DispatchChat(LunarIpcRequest request, Action<string> log)
    {
        if (request.Method != "ReceiveMessage") return Unknown(request, log);

        // ReceiveMessageRequest { UuidAndUsername sender = 1; string content = 2; }
        LunarIpcProtocol.TryGetString(request.Input, 2, out var content);

        // UuidAndUsername { Uuid uuid = 1; string username = 2; }
        var sender = string.Empty;
        if (LunarIpcProtocol.TryGetMessage(request.Input, 1, out var senderPayload))
            LunarIpcProtocol.TryGetString(senderPayload, 2, out sender);

        // PCL 没有启动器聊天面板，这里只能落到日志。游戏侧不需要回应，回空即可。
        log(string.IsNullOrEmpty(sender)
            ? $"游戏内聊天：{content}"
            : $"游戏内聊天 [{sender}]：{content}");

        return [];
    }

    #endregion

    #region launch.v1.LaunchService

    private static byte[] DispatchLaunch(LunarIpcRequest request, ILunarIpcHost? host, Action<string> log)
    {
        if (request.Method != "LaunchGame") return Unknown(request, log);

        // LaunchGameRequest { string version = 1; string module = 2; string autoJoinServerIp = 3;
        //                     LaunchSource launchSource = 4; PlayerModpack modpack = 5; }
        LunarIpcProtocol.TryGetString(request.Input, 1, out var version);
        LunarIpcProtocol.TryGetString(request.Input, 2, out var module);
        LunarIpcProtocol.TryGetString(request.Input, 3, out var serverIp);
        LunarIpcProtocol.TryGetInt32(request.Input, 4, out var launchSource);

        log($"游戏请求重开：版本={Describe(version)} 模块={Describe(module)} 服务器={Describe(serverIp)} 来源={launchSource}");

        host?.RequestLaunchGame(version, module, serverIp, launchSource);

        // LaunchGameResponse 是空消息。官方启动器是先回响应、再延迟 1 秒真正重启，
        // 这样游戏能先把 RPC 收尾；PCL 这边同样不能在响应发出前就把进程杀掉。
        return [];
    }

    #endregion

    #region location.v1.LocationService

    private static byte[] DispatchLocation(LunarIpcRequest request, ILunarIpcHost? host, Action<string> log)
    {
        switch (request.Method)
        {
            case "UpdateFocus":
            {
                // UpdateFocusRequest { bool focused = 1; }
                LunarIpcProtocol.TryGetBool(request.Input, 1, out var focused);
                log($"游戏窗口焦点：{(focused ? "获得" : "失去")}");
                return [];
            }

            case "UpdateLocation":
            {
                // UpdateLocationRequest { Location location = 1; InboundLocation inboundLocation = 2; }
                // 这两个嵌套消息的字段定义在别的包里，这里不解析，只记一条「位置变了」。
                log("游戏报告了位置变化");
                return [];
            }

            default:
                return Unknown(request, log);
        }
    }

    #endregion

    #region promotion.v1.PromotionService

    private static byte[] DispatchPromotion(LunarIpcRequest request, Action<string> log)
    {
        switch (request.Method)
        {
            case "CheckPendingPromotion":
                // CheckPendingPromotionResponse { bool hasPendingPromotion = 1; repeated PromotionType = 2; }
                // 默认 false 就是不编码，PCL 没有推广系统，回空即「没有待播活动」
                return [];

            case "PlayPromotion":
                log("游戏请求播放推广活动，PCL 没有推广模块，忽略");
                return [];

            case "Login":
            case "StartPromotionView":
            case "EndPromotionView":
            case "ClaimOutplayedReward":
            case "ClaimRewindPuzzleReward":
            case "ClaimPendingReward":
                // 响应都是空消息；PCL 没有奖励体系，什么都不用做
                return [];

            default:
                return Unknown(request, log);
        }
    }

    #endregion

    #region tebex.v1.TebexService

    private static byte[] DispatchTebex(LunarIpcRequest request, Action<string> log)
    {
        switch (request.Method)
        {
            case "CheckTebexCapabilities":
                // CheckTebexCapabilitiesResponse { EmbeddedCheckout embeddedCheckout = 1; }
                // EMBEDDED_CHECKOUT_UNSPECIFIED = 0 —— 官方启动器在没有 checkout 模块时
                // 也是返回 0，游戏据此走自己的降级路径（提示去网页）
                return [];

            case "OpenTebexJsCheckout":
            {
                // OpenTebexJsCheckoutRequest { string basketIdent = 1; string locale = 2; }
                LunarIpcProtocol.TryGetString(request.Input, 1, out var basket);
                log($"游戏请求 Tebex 内嵌结账（basket={Describe(basket)}），PCL 没有内嵌浏览器，回不支持");

                // OpenTebexJsCheckoutResponse { Status status = 1; bool openMinecraftScreen = 2; }
                // 必须显式写 2，否则游戏解出 0 = UNSPECIFIED，会当成还在处理中一直等
                using var stream = new MemoryStream();
                LunarIpcProtocol.WriteInt32Field(stream, 1, CheckoutStatusInternalError);
                return stream.ToArray();
            }

            case "TebexJsCheckoutOpened":
            case "TebexJsCheckoutClosed":
                return [];

            default:
                return Unknown(request, log);
        }
    }

    #endregion

    #region paynow.v1.PayNowService

    private static byte[] DispatchPayNow(LunarIpcRequest request, Action<string> log)
    {
        switch (request.Method)
        {
            case "CheckPayNowCapabilities":
                // CheckPayNowCapabilitiesResponse { EmbeddedCheckout embeddedCheckout = 1; }
                // 0 = 不支持，理由同 Tebex
                return [];

            case "OpenPayNowJsCheckout":
            {
                // OpenPayNowJsCheckoutRequest { string checkoutToken = 1; }
                LunarIpcProtocol.TryGetString(request.Input, 1, out var token);
                log($"游戏请求 PayNow 内嵌结账（token={Describe(token)}），PCL 没有内嵌浏览器，回不支持");

                using var stream = new MemoryStream();
                LunarIpcProtocol.WriteInt32Field(stream, 1, CheckoutStatusInternalError);
                return stream.ToArray();
            }

            case "PayNowCheckoutOpened":
            case "PayNowCheckoutClosed":
                return [];

            default:
                return Unknown(request, log);
        }
    }

    #endregion

    #region styngr.v1.StyngrRadioService

    private static byte[] DispatchStyngr(LunarIpcRequest request, Action<string> log)
    {
        switch (request.Method)
        {
            case "FetchState":
            {
                // FetchStateResponse { StyngrTrackState track = 1; StyngrControlsState controls = 2;
                //                     StyngrProgressState progress = 3; }
                // StyngrControlsState { bool playing = 1; bool muted = 2; float volume = 3; }
                // PCL 没有电台模块：明确告诉游戏「已静音、没在放」，界面就会显示不可用，
                // 而不是一直转圈等启动器推状态
                using var controls = new MemoryStream();
                LunarIpcProtocol.WriteBoolField(controls, 2, true);

                using var stream = new MemoryStream();
                LunarIpcProtocol.WriteMessageField(stream, 2, controls.ToArray());
                return stream.ToArray();
            }

            case "FetchPlaylists":
                // FetchPlaylistsResponse { repeated Playlist playlists = 1; bool premium = 2; }
                // 空列表 + premium = false，同样是默认值，不用编码
                return [];

            case "SetMuted":
            case "SetPlaying":
            case "SetVolume":
            case "NextTrack":
            case "ToggleLiked":
            case "StartPlaylist":
            case "OpenSelector":
                // 游戏在推播放状态。PCL 没在放东西，接受但什么也不做。
                // 这些请求的响应都是空消息。
                return [];

            default:
                return Unknown(request, log);
        }
    }

    #endregion

    #region 小工具

    private static string Describe(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(空)" : value;

    #endregion
}
