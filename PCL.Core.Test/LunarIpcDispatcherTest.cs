using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     IPC 各服务分派的回归测试。
///     <para>
///         消息定义和字段号是从官方启动器 <c>app.asar</c> 的 protobufjs 生成代码里
///         反编译出来的，这里逐条钉住，免得以后改字段号时悄悄破坏协议。
///     </para>
///     <para>
///         注意 protobuf 的「等于默认值就不编码」规则：空响应解出来就是全默认实例，
///         所以「回空」在能力探测类请求里等于「不支持」，不是 bug。
///     </para>
/// </summary>
[TestClass]
public class LunarIpcDispatcherTest
{
    private static readonly byte[] RequestId = [1, 2, 3, 4];

    #region 辅助

    private sealed class FakeHost : ILunarIpcHost
    {
        public List<(string Url, LunarUrlInitiator Initiator, LunarOpenType OpenType)> Opened { get; } = [];
        public int AddAccountCalls { get; private set; }
        public List<(string Version, string Module, string Server, int Source)> Launches { get; } = [];
        public bool AcceptUrls { get; set; } = true;

        public bool OpenUrl(string url, LunarUrlInitiator initiator, LunarOpenType openType)
        {
            Opened.Add((url, initiator, openType));
            return AcceptUrls;
        }

        public bool AcceptAddAccount { get; set; }

        public bool RequestAddAccount()
        {
            AddAccountCalls++;
            return AcceptAddAccount;
        }

        public void RequestLaunchGame(string version, string module, string serverIp, int launchSource) =>
            Launches.Add((version, module, serverIp, launchSource));
    }

    private static byte[] Dispatch(string service, string method, byte[] input,
        ILunarIpcHost? host = null, List<string>? logs = null)
    {
        var request = new LunarIpcRequest(RequestId, service, method, string.Empty, input);
        return LunarIpcDispatcher.Dispatch(request, host, message => logs?.Add(message));
    }

    /// <summary>按「字段号 + 值」拼一个只有标量字段的消息。</summary>
    private static byte[] Build(Action<MemoryStream> write)
    {
        using var stream = new MemoryStream();
        write(stream);
        return stream.ToArray();
    }

    #endregion

    #region browser.v1.BrowserService

    [TestMethod]
    public void OpenUrl_ShouldParseAllThreeFields()
    {
        var host = new FakeHost();
        var input = Build(stream =>
        {
            LunarIpcProtocol.WriteStringField(stream, 1, "https://store.lunarclient.com/item");
            LunarIpcProtocol.WriteInt32Field(stream, 2, (int)LunarUrlInitiator.Cosmetics);
            LunarIpcProtocol.WriteInt32Field(stream, 3, (int)LunarOpenType.ForceExternal);
        });

        var output = Dispatch(LunarIpcProtocol.BrowserService, "OpenUrl", input, host);

        // OpenUrlResponse 是空消息
        Assert.AreEqual(0, output.Length);
        Assert.AreEqual(1, host.Opened.Count);
        Assert.AreEqual("https://store.lunarclient.com/item", host.Opened[0].Url);
        Assert.AreEqual(LunarUrlInitiator.Cosmetics, host.Opened[0].Initiator);
        Assert.AreEqual(LunarOpenType.ForceExternal, host.Opened[0].OpenType);
    }

    [TestMethod]
    public void OpenUrl_WithEmptyUrl_ShouldNotCallHost()
    {
        var host = new FakeHost();
        var output = Dispatch(LunarIpcProtocol.BrowserService, "OpenUrl", [], host);

        Assert.AreEqual(0, output.Length);
        Assert.AreEqual(0, host.Opened.Count);
    }

    #endregion

    #region launch.v1.LaunchService

    [TestMethod]
    public void LaunchGame_ShouldParseVersionModuleAndServer()
    {
        var host = new FakeHost();
        var input = Build(stream =>
        {
            LunarIpcProtocol.WriteStringField(stream, 1, "1.21.4");
            LunarIpcProtocol.WriteStringField(stream, 2, "fabric");
            LunarIpcProtocol.WriteStringField(stream, 3, "mc.hypixel.net");
            LunarIpcProtocol.WriteInt32Field(stream, 4, 1);
        });

        var output = Dispatch(LunarIpcProtocol.LaunchService, "LaunchGame", input, host);

        Assert.AreEqual(0, output.Length);
        Assert.AreEqual(1, host.Launches.Count);
        Assert.AreEqual("1.21.4", host.Launches[0].Version);
        Assert.AreEqual("fabric", host.Launches[0].Module);
        Assert.AreEqual("mc.hypixel.net", host.Launches[0].Server);
        Assert.AreEqual(1, host.Launches[0].Source);
    }

    #endregion

    #region auth.v1.AuthService

    [TestMethod]
    public void AddAccount_ShouldReachHostAndReturnDefaultFailure()
    {
        var host = new FakeHost();
        var output = Dispatch(LunarIpcProtocol.AuthService, "AddAccount", [], host);

        // 拿不出账号时：AddAccountResponse { success: false, error: UNSPECIFIED } —— 全是默认值
        Assert.AreEqual(0, output.Length);
        Assert.AreEqual(1, host.AddAccountCalls);
    }

    [TestMethod]
    public void AddAccount_ShouldReportSuccessWhenHostProvidesAccount()
    {
        var host = new FakeHost { AcceptAddAccount = true };
        var output = Dispatch(LunarIpcProtocol.AuthService, "AddAccount", [], host);

        // success = 1（字段 1，varint）→ 08 01
        CollectionAssert.AreEqual(new byte[] { 0x08, 0x01 }, output);
        Assert.AreEqual(1, host.AddAccountCalls);
    }

    #endregion

    #region tebex / paynow

    [TestMethod]
    public void CheckTebexCapabilities_ShouldReportUnsupported()
    {
        var output = Dispatch(LunarIpcProtocol.TebexService, "CheckTebexCapabilities", []);

        // embeddedCheckout = EMBEDDED_CHECKOUT_UNSPECIFIED = 0，默认值不编码
        Assert.AreEqual(0, output.Length);
    }

    [TestMethod]
    public void OpenTebexJsCheckout_ShouldReturnInternalError()
    {
        var input = Build(stream =>
        {
            LunarIpcProtocol.WriteStringField(stream, 1, "basket-abc");
            LunarIpcProtocol.WriteStringField(stream, 2, "zh_CN");
        });

        var output = Dispatch(LunarIpcProtocol.TebexService, "OpenTebexJsCheckout", input);

        // OpenTebexJsCheckoutResponse { status = 1 }
        // STATUS_INTERNAL_ERROR = 2，必须显式写；写成 0 游戏会当成还在处理
        CollectionAssert.AreEqual(new byte[] { 0x08, 0x02 }, output);
    }

    [TestMethod]
    public void CheckPayNowCapabilities_ShouldReportUnsupported()
    {
        var output = Dispatch(LunarIpcProtocol.PayNowService, "CheckPayNowCapabilities", []);
        Assert.AreEqual(0, output.Length);
    }

    [TestMethod]
    public void OpenPayNowJsCheckout_ShouldReturnInternalError()
    {
        var input = Build(stream => LunarIpcProtocol.WriteStringField(stream, 1, "token-xyz"));
        var output = Dispatch(LunarIpcProtocol.PayNowService, "OpenPayNowJsCheckout", input);

        CollectionAssert.AreEqual(new byte[] { 0x08, 0x02 }, output);
    }

    #endregion

    #region styngr.v1.StyngrRadioService

    [TestMethod]
    public void FetchState_ShouldReportMutedAndNotPlaying()
    {
        var output = Dispatch(LunarIpcProtocol.StyngrRadioService, "FetchState", []);

        // FetchStateResponse { controls = 2 { muted = 2 = true } }
        // 外层 tag = (2<<3)|2 = 0x12，长度 2；内层 tag = (2<<3)|0 = 0x10，值 1
        CollectionAssert.AreEqual(new byte[] { 0x12, 0x02, 0x10, 0x01 }, output);
    }

    [TestMethod]
    public void FetchPlaylists_ShouldReturnEmptyList()
    {
        // playlists 为空、premium = false，都是默认值
        Assert.AreEqual(0, Dispatch(LunarIpcProtocol.StyngrRadioService, "FetchPlaylists", []).Length);
    }

    #endregion

    #region promotion / location / chat

    [TestMethod]
    public void CheckPendingPromotion_ShouldReportNothingPending()
    {
        Assert.AreEqual(0,
            Dispatch(LunarIpcProtocol.PromotionService, "CheckPendingPromotion", []).Length);
    }

    [TestMethod]
    public void UpdateFocus_ShouldParseAndReturnEmpty()
    {
        var logs = new List<string>();
        var input = Build(stream => LunarIpcProtocol.WriteBoolField(stream, 1, true));

        var output = Dispatch(LunarIpcProtocol.LocationService, "UpdateFocus", input, null, logs);

        Assert.AreEqual(0, output.Length);
        Assert.IsTrue(logs.Any(x => x.Contains("焦点")), "应当记一条焦点变化的日志");
    }

    [TestMethod]
    public void ReceiveMessage_ShouldLogSenderAndContent()
    {
        var logs = new List<string>();
        // UuidAndUsername { Uuid uuid = 1; string username = 2; }
        var sender = Build(stream => LunarIpcProtocol.WriteStringField(stream, 2, "Steve"));
        var input = Build(stream =>
        {
            LunarIpcProtocol.WriteMessageField(stream, 1, sender);
            LunarIpcProtocol.WriteStringField(stream, 2, "hello world");
        });

        var output = Dispatch(LunarIpcProtocol.ChatService, "ReceiveMessage", input, null, logs);

        Assert.AreEqual(0, output.Length);
        Assert.IsTrue(logs.Any(x => x.Contains("Steve") && x.Contains("hello world")));
    }

    #endregion

    #region 兜底

    [TestMethod]
    public void UnknownService_ShouldReturnEmptyAndLog()
    {
        var logs = new List<string>();
        var output = Dispatch("lunarclient.gameipc.unknown.v1.Nope", "Whatever", [], null, logs);

        Assert.AreEqual(0, output.Length);
        Assert.IsTrue(logs.Any(x => x.Contains("未实现")));
    }

    [TestMethod]
    public void KnownServiceWithUnknownMethod_ShouldReturnEmptyAndLog()
    {
        var logs = new List<string>();
        var output = Dispatch(LunarIpcProtocol.BrowserService, "SomethingElse", [], null, logs);

        Assert.AreEqual(0, output.Length);
        Assert.IsTrue(logs.Any(x => x.Contains("未实现")));
    }

    #endregion

    #region 协议读取

    [TestMethod]
    public void TryGetString_ShouldSkipUnknownFields()
    {
        var input = Build(stream =>
        {
            LunarIpcProtocol.WriteInt32Field(stream, 1, 42);
            LunarIpcProtocol.WriteStringField(stream, 5, "target");
        });

        Assert.IsTrue(LunarIpcProtocol.TryGetString(input, 5, out var value));
        Assert.AreEqual("target", value);
        Assert.IsFalse(LunarIpcProtocol.TryGetString(input, 9, out _));
    }

    [TestMethod]
    public void TryGetFloat_ShouldRoundTrip()
    {
        var input = Build(stream => LunarIpcProtocol.WriteFloatField(stream, 1, 0.75f));

        Assert.IsTrue(LunarIpcProtocol.TryGetFloat(input, 1, out var value));
        Assert.AreEqual(0.75f, value, 1e-6);
    }

    [TestMethod]
    public void TryGetBool_ShouldIgnoreWrongWireType()
    {
        // 同一个字段号用 length-delimited 编码（string），读 bool 时应当跳过而不是误解
        var input = Build(stream => LunarIpcProtocol.WriteStringField(stream, 1, "x"));

        Assert.IsFalse(LunarIpcProtocol.TryGetBool(input, 1, out _));
        Assert.IsTrue(LunarIpcProtocol.TryGetString(input, 1, out var text));
        Assert.AreEqual("x", text);
    }

    #endregion

    #region 推送（启动器 → 游戏）

    [TestMethod]
    public void EncodePush_ShouldWrapTypeUrlInAny()
    {
        var message = LunarIpcProtocol.EncodePush(LunarIpcPush.ExitGame.TypeUrl, LunarIpcPush.ExitGame.Payload);

        // GameboundIPCMessage { push_notification = 2 { type_url = 1, value = 2 } }
        Assert.IsTrue(LunarIpcProtocol.TryGetMessage(message, 2, out var any), "外层必须是 push_notification");
        Assert.IsTrue(LunarIpcProtocol.TryGetString(any, 1, out var typeUrl));
        Assert.AreEqual("type.googleapis.com/lunarclient.gameipc.launch.v1.ExitGamePush", typeUrl);

        // ExitGamePush 没有字段，value 应当整个省略（protobuf 的「默认值不编码」）
        Assert.IsFalse(LunarIpcProtocol.TryGetMessage(any, 2, out _), "空消息不该写 value");
    }

    [TestMethod]
    public void PlayServerPush_ShouldCarryServerIp()
    {
        var push = LunarIpcPush.PlayServer("mc.example.com:25565");
        var message = LunarIpcProtocol.EncodePush(push.TypeUrl, push.Payload);

        Assert.IsTrue(LunarIpcProtocol.TryGetMessage(message, 2, out var any));
        Assert.IsTrue(LunarIpcProtocol.TryGetString(any, 1, out var typeUrl));
        Assert.AreEqual("type.googleapis.com/lunarclient.gameipc.launch.v1.PlayServerPush", typeUrl);

        Assert.IsTrue(LunarIpcProtocol.TryGetMessage(any, 2, out var value), "有字段的推送必须带 value");
        Assert.IsTrue(LunarIpcProtocol.TryGetString(value, 1, out var ip));
        Assert.AreEqual("mc.example.com:25565", ip);
    }

    [TestMethod]
    public void PlayServerPush_ShouldRejectEmptyIp()
    {
        Assert.ThrowsExactly<ArgumentException>(() => LunarIpcPush.PlayServer(" "));
    }

    #endregion
}
