using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar.Social;

namespace PCL.Core.Test;

/// <summary>
///     Lunar 后端（好友 / 私聊）协议的回归测试。
///     <para>
///         字段号是从官方启动器 <c>app.asar</c> 里 protobufjs 生成的 <c>encode</c>
///         代码反推的，这里逐条钉住：以后谁改字段号、改默认值判断，测试会先炸。
///     </para>
/// </summary>
[TestClass]
public class LunarSocialProtocolTest
{
    private static readonly LunarUuid Sample = LunarUuid.Parse("ca7514be-86d6-4f29-be39-850f3cad12f7");

    [TestMethod]
    public void Uuid_ShouldRoundTripDashedAndCompact()
    {
        Assert.AreEqual("ca7514be-86d6-4f29-be39-850f3cad12f7", Sample.ToString());
        Assert.AreEqual("ca7514be86d64f29be39850f3cad12f7", Sample.ToCompactString());
    }

    [TestMethod]
    public void Uuid_ShouldRejectGarbage()
    {
        Assert.IsTrue(LunarUuid.Parse("not-a-uuid").IsEmpty);
        Assert.IsTrue(LunarUuid.Parse("").IsEmpty);
        Assert.IsTrue(LunarUuid.Parse(null).IsEmpty);
    }

    [TestMethod]
    public void Uuid_ShouldEncodeAsTwoFixed64()
    {
        var writer = new ProtoWriter();
        LunarSocialProtocol.WriteUuid(writer, 1, Sample);
        var payload = writer.ToArray();

        // 字段 1、长度分隔 → tag = (1 << 3) | 2 = 0x0A
        Assert.AreEqual(0x0A, payload[0]);

        // 里面是两个 fixed64：tag 0x09 / 0x11 + 各 8 字节
        var inner = payload[2..];
        Assert.AreEqual(18, inner.Length);
        Assert.AreEqual(0x09, inner[0]);
        Assert.AreEqual(0x11, inner[9]);
    }

    [TestMethod]
    public void Hello_ShouldCarryUuidUsernameAndInitiator()
    {
        var payload = LunarSocialProtocol.EncodeHello(Sample, "DeobfJar");

        // 外层是 ServerboundWebSocketMessage.hello = 1
        Assert.AreEqual(0x0A, payload[0]);
        Assert.IsTrue(Encoding.UTF8.GetString(payload).Contains("DeobfJar"));
        Assert.IsTrue(Encoding.UTF8.GetString(payload).Contains("LAUNCHER_WEBSOCKET"));
    }

    [TestMethod]
    public void RpcRequest_ShouldCarryIdServiceMethodAndInput()
    {
        var payload = LunarSocialProtocol.EncodeRpcRequest("7", "svc", "Login", [0x08, 0x01]);
        var text = Encoding.UTF8.GetString(payload);

        Assert.IsTrue(text.Contains("svc"));
        Assert.IsTrue(text.Contains("Login"));
        // request_id = 字段 1、长度分隔
        Assert.AreEqual(0x0A, payload[0]);
        Assert.AreEqual((byte)'7', payload[2]);
    }

    [TestMethod]
    public void Clientbound_ShouldDecodeRpcResponse()
    {
        // WebSocketRpcResponse { request_id = "3"; output = {0x08, 0x01} }
        var rpc = new ProtoWriter();
        rpc.Bytes(1, Encoding.UTF8.GetBytes("3"));
        rpc.Bytes(2, [0x08, 0x01]);

        var envelope = new ProtoWriter();
        envelope.Message(1, rpc.ToArray());

        var decoded = LunarSocialProtocol.DecodeClientbound(envelope.ToArray());
        Assert.IsTrue(decoded.IsRpc);
        Assert.AreEqual("3", decoded.RequestId);
        CollectionAssert.AreEqual(new byte[] { 0x08, 0x01 }, decoded.Output);
    }

    [TestMethod]
    public void Clientbound_ShouldDecodePushNotification()
    {
        // Any { type_url = "type.googleapis.com/x.Y"; value = {0x08} }
        var any = new ProtoWriter();
        any.String(1, "type.googleapis.com/x.Y");
        any.Bytes(2, [0x08]);

        var envelope = new ProtoWriter();
        envelope.Message(2, any.ToArray());

        var decoded = LunarSocialProtocol.DecodeClientbound(envelope.ToArray());
        Assert.IsFalse(decoded.IsRpc);
        Assert.AreEqual("x.Y", decoded.PushTypeUrl);
        CollectionAssert.AreEqual(new byte[] { 0x08 }, decoded.PushPayload);
    }

    [TestMethod]
    public void FriendLoginResponse_ShouldSplitOnlineAndOffline()
    {
        // 一个在线好友（字段 11）和一个离线好友（字段 2），字段 2 的重复编号
        // 在本消息里是「离线好友列表」，不是「status」—— 官方定义就是这么排的。
        var player = new ProtoWriter();
        LunarSocialProtocol.WriteUuid(player, 1, Sample);
        player.String(2, "DeobfJar");

        var online = new ProtoWriter();
        online.Message(1, player.ToArray());
        online.Int32(2, (int)LunarFriendStatus.Away);
        online.String(13, "MVP");

        var offline = new ProtoWriter();
        offline.Message(1, player.ToArray());
        offline.String(8, "VIP");

        var response = new ProtoWriter();
        response.Message(11, online.ToArray());
        response.Message(2, offline.ToArray());

        var friends = LunarSocialProtocol.DecodeFriendLoginResponse(response.ToArray());
        Assert.AreEqual(2, friends.Count);
        Assert.AreEqual(LunarFriendStatus.Away, friends[0].Status);
        Assert.AreEqual("DeobfJar", friends[0].Name);
        Assert.AreEqual("MVP", friends[0].RankName);
        Assert.IsTrue(friends[0].IsOnline);

        Assert.AreEqual(LunarFriendStatus.Unknown, friends[1].Status);
        Assert.IsFalse(friends[1].IsOnline);
        Assert.AreEqual("VIP", friends[1].RankName);
    }

    [TestMethod]
    public void MessagePush_ShouldDecodeReferenceAndText()
    {
        var reference = LunarSocialProtocol.EncodeConversationReference(default, Sample);

        var sender = new ProtoWriter();
        var player = new ProtoWriter();
        LunarSocialProtocol.WriteUuid(player, 1, LunarUuid.Parse("93a6aca9-33ba-40da-bd77-de8bf072ff5e"));
        player.String(2, "Bed_Spacekiller_");
        sender.Message(1, player.ToArray());

        var contents = new ProtoWriter();
        contents.String(1, "hello");

        var message = new ProtoWriter();
        LunarSocialProtocol.WriteUuid(message, 1, Sample);
        message.Message(3, sender.ToArray());
        message.Message(4, contents.ToArray());

        var push = new ProtoWriter();
        push.Message(1, reference);
        push.Message(2, message.ToArray());

        var (conversationId, _, chat) = LunarSocialProtocol.DecodeMessagePush(push.ToArray(),
            "ca7514be-86d6-4f29-be39-850f3cad12f7");

        Assert.AreEqual("ca7514be-86d6-4f29-be39-850f3cad12f7", conversationId);
        Assert.IsNotNull(chat);
        Assert.AreEqual("hello", chat.Text);
        Assert.AreEqual("Bed_Spacekiller_", chat.SenderName);
        Assert.IsFalse(chat.Mine); // 发送者是别人
    }

    [TestMethod]
    public void MessagePush_ShouldMarkOwnMessages()
    {
        var sender = new ProtoWriter();
        var player = new ProtoWriter();
        LunarSocialProtocol.WriteUuid(player, 1, Sample);
        player.String(2, "DeobfJar");
        sender.Message(1, player.ToArray());

        var contents = new ProtoWriter();
        contents.String(1, "mine");

        var message = new ProtoWriter();
        message.Message(3, sender.ToArray());
        message.Message(4, contents.ToArray());

        var push = new ProtoWriter();
        push.Message(2, message.ToArray());

        var (_, _, chat) = LunarSocialProtocol.DecodeMessagePush(push.ToArray(),
            "ca7514be-86d6-4f29-be39-850f3cad12f7");
        Assert.IsNotNull(chat);
        Assert.IsTrue(chat.Mine);
    }

    [TestMethod]
    public void SendMessageStatus_ShouldReadStatusField()
    {
        var ok = new ProtoWriter();
        ok.Int32(1, 1);
        Assert.AreEqual(1, LunarSocialProtocol.DecodeSendMessageStatus(ok.ToArray()));

        Assert.AreEqual(0, LunarSocialProtocol.DecodeSendMessageStatus([]));
    }

    [TestMethod]
    public void ServerHash_ShouldBeStableAndLowercase()
    {
        var secret = new byte[16];
        var key = new byte[] { 1, 2, 3, 4 };

        var first = LunarSocialProtocol.ComputeServerHash(secret, key);
        var second = LunarSocialProtocol.ComputeServerHash(secret, key);

        Assert.AreEqual(first, second);
        Assert.AreEqual(first.ToLowerInvariant(), first);
        Assert.IsFalse(first.StartsWith('0'), "前导零应该被去掉");
    }

    [TestMethod]
    public void ServerHash_ShouldGoNegativeWhenHighBitSet()
    {
        // 挑一组让 sha1 首字节高位为 1 的输入，验证补码取负的分支
        var found = false;
        for (var i = 0; i < 512 && !found; i++)
        {
            var secret = new byte[16];
            secret[0] = (byte)i;
            var hash = LunarSocialProtocol.ComputeServerHash(secret, [7, 7, 7, 7]);
            if (!hash.StartsWith('-')) continue;

            found = true;
            Assert.IsTrue(hash.Length > 1);
        }

        Assert.IsTrue(found, "512 次里应该至少命中一次高位为 1 的哈希");
    }

    [TestMethod]
    public void Conversation_ShouldPickPeerFromParticipants()
    {
        var self = "ca7514be-86d6-4f29-be39-850f3cad12f7";
        var peer = LunarUuid.Parse("93a6aca9-33ba-40da-bd77-de8bf072ff5e");

        var reference = LunarSocialProtocol.EncodeConversationReference(default, Sample);

        ProtoWriter Participant(LunarUuid uuid, string name)
        {
            var player = new ProtoWriter();
            LunarSocialProtocol.WriteUuid(player, 1, uuid);
            player.String(2, name);
            var writer = new ProtoWriter();
            writer.Message(1, player.ToArray());
            return writer;
        }

        var conversation = new ProtoWriter();
        conversation.Message(1, reference);
        conversation.Message(2, Participant(Sample, "DeobfJar").ToArray());
        conversation.Message(2, Participant(peer, "Bed_Spacekiller_").ToArray());

        var response = new ProtoWriter();
        response.Int32(1, 1);
        response.Message(2, conversation.ToArray());

        var (status, list) = LunarSocialProtocol.DecodeConversations(response.ToArray(), self);
        Assert.AreEqual(1, status);
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual(peer.ToString(), list[0].PeerUuid);
        Assert.AreEqual("Bed_Spacekiller_", list[0].PeerName);
        Assert.AreEqual(Sample.ToString(), list[0].Id);
    }

    [TestMethod]
    public void CreateConversation_ShouldCarryTypeAndParticipant()
    {
        var payload = LunarSocialProtocol.EncodeCreateConversation(LunarSocialProtocol.ConversationTypeFriend,
            Sample);
        Assert.AreEqual(0x08, payload[0]); // type = 1
        Assert.AreEqual(1, payload[1]);
        Assert.AreEqual(0x12, payload[2]); // participantUuids = 2
    }

    [TestMethod]
    public void Handshake_ShouldCarryIdentityJwtAndVersion()
    {
        var payload = LunarSocialProtocol.EncodeHandshake("jwt-token", Sample, "DeobfJar", "install-id", "3.7.27");
        var text = Encoding.UTF8.GetString(payload);

        Assert.IsTrue(text.Contains("jwt-token"));
        Assert.IsTrue(text.Contains("DeobfJar"));
        Assert.IsTrue(text.Contains("install-id"));
        Assert.IsTrue(text.Contains("3.7.27"));
        Assert.IsTrue(text.Contains("production"));
        Assert.IsTrue(text.Contains("win32"));
    }

    [TestMethod]
    public void Writer_ShouldSkipDefaultValues()
    {
        var writer = new ProtoWriter();
        writer.Int32(1, 0);
        writer.String(2, "");
        writer.Bool(3, false);
        writer.UInt64(4, 0);
        Assert.AreEqual(0, writer.Length);
    }
}
