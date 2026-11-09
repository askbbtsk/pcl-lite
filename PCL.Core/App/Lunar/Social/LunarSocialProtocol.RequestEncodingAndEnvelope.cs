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

internal static partial class LunarSocialProtocol
{


    /// <summary>认证服务的第一帧：<c>hello { identity { uuid, username }, initiator }</c>。</summary>
    public static byte[] EncodeHello(LunarUuid uuid, string username)
    {
        var identity = new ProtoWriter();
        WriteUuid(identity, 1, uuid);
        identity.String(2, username);

        var hello = new ProtoWriter();
        hello.Message(1, identity.ToArray());
        hello.String(2, LauncherInitiator);

        var envelope = new ProtoWriter();
        envelope.Message(1, hello.ToArray());
        return envelope.ToArray();
    }


    /// <summary>
    ///     解析认证服务的回包。返回 <c>true</c> 表示拿到的是加密请求。
    /// </summary>
    public static bool TryDecodeEncryptionRequest(ReadOnlySpan<byte> payload,
        out byte[] publicKey, out byte[] randomBytes)
    {
        publicKey = [];
        randomBytes = [];

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2)
            {
                var nested = new ProtoReader(reader.ReadBytes());
                while (nested.TryReadTag(out var innerField, out var innerWire))
                {
                    switch (innerField)
                    {
                        case 1 when innerWire == 2:
                            publicKey = nested.ReadBytes().ToArray();
                            break;
                        case 2 when innerWire == 2:
                            randomBytes = nested.ReadBytes().ToArray();
                            break;
                        default:
                            nested.Skip(innerWire);
                            break;
                    }
                }

                continue;
            }

            reader.Skip(wire);
        }

        return publicKey.Length > 0 && randomBytes.Length > 0;
    }


    /// <summary>解析认证服务回包里的 <c>authSuccess.encryptedJwt</c>（字段 2，AES-GCM 密文）。</summary>
    public static byte[]? TryDecodeEncryptedJwt(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 2 && wire == 2)
            {
                var nested = new ProtoReader(reader.ReadBytes());
                while (nested.TryReadTag(out var innerField, out var innerWire))
                {
                    if (innerField == 2 && innerWire == 2) return nested.ReadBytes().ToArray();
                    nested.Skip(innerWire);
                }

                continue;
            }

            reader.Skip(wire);
        }

        return null;
    }


    /// <summary>加密响应：<c>secretKey</c> 与 <c>publicKey</c> 都是 RSA(PKCS#1) 密文。</summary>
    public static byte[] EncodeEncryptionResponse(byte[] encryptedSecret, byte[] encryptedRandom)
    {
        var response = new ProtoWriter();
        response.Bytes(1, encryptedSecret);
        response.Bytes(2, encryptedRandom);

        var envelope = new ProtoWriter();
        envelope.Message(2, response.ToArray());
        return envelope.ToArray();
    }




    /// <summary>
    ///     拼 <c>Handshake</c>。这条消息<b>不带信封</b>，连接一开就直接发。
    /// </summary>
    /// <param name="authenticatorJwt">认证服务换来的 JWT。</param>
    /// <param name="uuid">玩家 UUID。</param>
    /// <param name="username">玩家名。</param>
    /// <param name="installationId">安装 ID，官方启动器从本地配置里读；这里用 PCL 自己的一份。</param>
    /// <param name="launcherVersion">伪装成官方启动器时上报的版本号。</param>
    public static byte[] EncodeHandshake(string authenticatorJwt, LunarUuid uuid, string username,
        string installationId, string launcherVersion)
    {
        var player = new ProtoWriter();
        WriteUuid(player, 1, uuid);
        player.String(2, username);

        var identity = new ProtoWriter();
        identity.Message(1, player.ToArray());
        identity.Int32(2, IdentityMicrosoft);
        identity.String(3, authenticatorJwt);

        var launcherHandshake = new ProtoWriter();
        launcherHandshake.String(1, "production");
        launcherHandshake.String(2, Guid.NewGuid().ToString("N"));
        launcherHandshake.Int32(4, ElectronBinariesElectron);
        launcherHandshake.Int32(7, (int)(Environment.TickCount64 / 1000));

        var startedAt = new ProtoWriter();
        startedAt.UInt64(1, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        launcherHandshake.Message(8, startedAt.ToArray());

        var version = new ProtoWriter();
        version.String(1, launcherVersion);

        var language = new ProtoWriter();
        language.String(1, "zho");
        language.String(2, "zh_cn");

        var handshake = new ProtoWriter();
        handshake.Message(1, identity.ToArray());
        handshake.Message(2, version.ToArray());
        handshake.String(3, installationId);
        handshake.String(5, "win32");
        handshake.String(6, "x64");
        handshake.Message(7, language.ToArray());
        handshake.Message(9, launcherHandshake.ToArray());
        return handshake.ToArray();
    }




    /// <summary>一次 RPC 调用。</summary>
    public static byte[] EncodeRpcRequest(string requestId, string service, string method, byte[]? input)
    {
        var envelope = new ProtoWriter();
        envelope.Bytes(1, Encoding.UTF8.GetBytes(requestId));
        envelope.String(2, service);
        envelope.String(3, method);
        if (input is { Length: > 0 }) envelope.Bytes(4, input);
        return envelope.ToArray();
    }


    /// <summary>服务端回包：要么是 RPC 响应，要么是推送。</summary>
    public readonly record struct ClientboundMessage(string? RequestId, byte[]? Output, string? PushTypeUrl, byte[]? PushPayload)
    {
        public bool IsRpc => RequestId is not null;
    }


    public static ClientboundMessage DecodeClientbound(ReadOnlySpan<byte> payload)
    {
        string? requestId = null;
        byte[]? output = null;
        string? typeUrl = null;
        byte[]? pushPayload = null;

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire != 2)
            {
                reader.Skip(wire);
                continue;
            }

            var nested = new ProtoReader(reader.ReadBytes());
            if (field == 1)
            {
                while (nested.TryReadTag(out var innerField, out var innerWire))
                {
                    switch (innerField)
                    {
                        case 1 when innerWire == 2:
                            requestId = Encoding.UTF8.GetString(nested.ReadBytes());
                            break;
                        case 2 when innerWire == 2:
                            output = nested.ReadBytes().ToArray();
                            break;
                        default:
                            nested.Skip(innerWire);
                            break;
                    }
                }
            }
            else if (field == 2)
            {
                // google.protobuf.Any { string type_url = 1; bytes value = 2; }
                while (nested.TryReadTag(out var innerField, out var innerWire))
                {
                    switch (innerField)
                    {
                        case 1 when innerWire == 2:
                            typeUrl = Encoding.UTF8.GetString(nested.ReadBytes());
                            break;
                        case 2 when innerWire == 2:
                            pushPayload = nested.ReadBytes().ToArray();
                            break;
                        default:
                            nested.Skip(innerWire);
                            break;
                    }
                }
            }
        }

        if (typeUrl is not null) typeUrl = typeUrl.Replace("type.googleapis.com/", "");
        return new ClientboundMessage(requestId, output, typeUrl, pushPayload);
    }




    /// <summary><c>FriendService/Login</c> 的请求体是空消息。</summary>
    public static byte[] EncodeEmptyMessage() => [];


    /// <summary>
    ///     解析 <c>friend.v1.LoginResponse</c>。
    ///     <para>
    ///         <b>服务端会把同一个人同时塞进两个列表</b>：实测一个在线好友在
    ///         <c>onlineFriends</c>(11) 和 <c>offlineFriends</c>(2) 里各出现一次，
    ///         连 UUID 都一模一样。直接拼起来列表里就是两个同名好友（一个「启动器」、
    ///         一个「离线」）。所以这里以 <c>offlineFriends</c> 为底，
    ///         再用 <c>onlineFriends</c> 按 UUID 覆盖状态与位置。
    ///     </para>
    /// </summary>
    public static IReadOnlyList<LunarFriend> DecodeFriendLoginResponse(ReadOnlySpan<byte> payload)
    {
        var online = new List<LunarFriend>();
        var offline = new List<LunarFriend>();
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire != 2)
            {
                reader.Skip(wire);
                continue;
            }

            var nested = reader.ReadBytes();
            if (field == 11) online.Add(DecodeFriend(nested, true));
            else if (field == 2) offline.Add(DecodeFriend(nested, false));
        }

        return MergeFriends(online, offline);
    }


    /// <summary>
    ///     合并两个好友列表，同一个 UUID 只留一条。
    ///     <para>
    ///         <c>onlineFriends</c> 带状态和所在位置（<c>OfflineFriend</c> 没有这两个字段），
    ///         所以冲突时用它覆盖；但它偶尔不带 <c>rankName</c>，那种情况保留离线列表里的。
    ///     </para>
    /// </summary>
    private static IReadOnlyList<LunarFriend> MergeFriends(List<LunarFriend> online, List<LunarFriend> offline)
    {
        var merged = new List<LunarFriend>(offline.Count + online.Count);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var friend in offline)
        {
            if (friend.Uuid.Length == 0)
            {
                merged.Add(friend);
                continue;
            }

            index[friend.Uuid] = merged.Count;
            merged.Add(friend);
        }

        foreach (var friend in online)
        {
            if (friend.Uuid.Length > 0 && index.TryGetValue(friend.Uuid, out var at))
            {
                merged[at] = friend with
                {
                    RankName = string.IsNullOrEmpty(friend.RankName) ? merged[at].RankName : friend.RankName
                };
                continue;
            }

            if (friend.Uuid.Length > 0) index[friend.Uuid] = merged.Count;
            merged.Add(friend);
        }

        return merged;
    }


    private static LunarFriend DecodeFriend(ReadOnlySpan<byte> payload, bool online)
    {
        var uuid = default(LunarUuid);
        var name = string.Empty;
        var status = online ? LunarFriendStatus.Online : LunarFriendStatus.Unknown;
        var where = string.Empty;
        var rank = string.Empty;

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire != 2)
            {
                if (wire == 0 && field == 2 && online)
                {
                    reader.TryReadVarint(out var value);
                    status = (LunarFriendStatus)(int)value;
                }
                else
                {
                    reader.Skip(wire);
                }

                continue;
            }

            switch (field)
            {
                case 1:
                    (uuid, name) = DecodePlayer(reader.ReadBytes());
                    break;
                case 3 when online:
                    where = DecodeLocation(reader.ReadBytes());
                    break;
                // 段位名在两种好友里字段号不一样：在线是 13，离线是 8
                case 13 when online:
                    rank = reader.ReadString();
                    break;
                case 8 when !online:
                    rank = reader.ReadString();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new LunarFriend(uuid.ToString(), name, status, where, rank);
    }


    /// <summary><c>UuidAndUsername { Uuid uuid = 1; string username = 2; }</c>。</summary>
    public static (LunarUuid Uuid, string Name) DecodePlayer(ReadOnlySpan<byte> payload)
    {
        var uuid = default(LunarUuid);
        var name = string.Empty;

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    uuid = DecodeUuid(reader.ReadBytes());
                    break;
                case 2 when wire == 2:
                    name = reader.ReadString();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return (uuid, name);
    }


    /// <summary>
    ///     把 <c>Location</c> 那个 11 分支的 oneof 翻成一句人话。
    ///     <para>只认有信息量的分支，其余给个空串，界面上就不显示位置。</para>
    /// </summary>
    private static string DecodeLocation(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire != 2)
            {
                reader.Skip(wire);
                continue;
            }

            var nested = reader.ReadBytes();
            switch (field)
            {
                case 1: // publicServer：优先服务器名，没有就用地址
                {
                    var name = string.Empty;
                    var address = string.Empty;
                    var server = new ProtoReader(nested);
                    while (server.TryReadTag(out var f, out var w))
                    {
                        switch (f)
                        {
                            case 2 when w == 2:
                                name = server.ReadString();
                                break;
                            case 3 when w == 2:
                                address = server.ReadString();
                                break;
                            default:
                                server.Skip(w);
                                break;
                        }
                    }

                    return name.Length > 0 ? name : address;
                }
                case 3:
                    return "局域网";
                case 4:
                    return "单人游戏";
                case 5:
                    return "Realms";
                case 6:
                    return "菜单";
                case 7:
                    return "启动器";
                case 10:
                    return "游戏中";
                default:
                    continue;
            }
        }

        return string.Empty;
    }




    /// <summary><c>ConversationService/Login { bool usingSatellite = 1; }</c>，用卫星连接会走另一条链路，这里填 false。</summary>
    public static byte[] EncodeConversationLogin() => [];


    /// <summary><c>ConversationReference { Uuid friendUuid = 1; Uuid conversationReference = 2; }</c>。</summary>
    public static byte[] EncodeConversationReference(LunarUuid friendUuid, LunarUuid conversationId)
    {
        var reference = new ProtoWriter();
        WriteUuid(reference, 1, friendUuid);
        WriteUuid(reference, 2, conversationId);
        return reference.ToArray();
    }


    /// <summary><c>GetConversationsRequest { int32 limit = 1; }</c>。</summary>
    public static byte[] EncodeGetConversations(int limit)
    {
        var request = new ProtoWriter();
        request.Int32(1, limit);
        return request.ToArray();
    }


    /// <summary>
    ///     <c>CreateConversationRequest { int32 type = 1; repeated Uuid participantUuids = 2; }</c>。
    ///     <para>
    ///         第一次给某个好友发消息时没有会话 id，得先建一个；已经聊过的话服务端会直接
    ///         把原来那个还回来（不会建出第二个）。
    ///     </para>
    /// </summary>
    public static byte[] EncodeCreateConversation(int type, LunarUuid participant)
    {
        var request = new ProtoWriter();
        request.Int32(1, type);
        WriteUuid(request, 2, participant);
        return request.ToArray();
    }


    /// <summary><c>CreateConversationResponse { int32 status = 1; Conversation conversation = 2; }</c>。</summary>
    public static (int Status, LunarConversation? Conversation) DecodeCreateConversationResponse(
        ReadOnlySpan<byte> payload, string selfUuid)
    {
        var status = 0;
        LunarConversation? conversation = null;

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire == 0 && field == 1)
            {
                reader.TryReadVarint(out var value);
                status = (int)value;
                continue;
            }

            if (wire == 2 && field == 2)
            {
                conversation = DecodeConversation(reader.ReadBytes(), selfUuid);
                continue;
            }

            reader.Skip(wire);
        }

        return (status, conversation);
    }


    /// <summary>
    ///     解析 <c>SendConversationMessageResponse { int32 status = 1; }</c>。
    ///     <para>1 是成功；2 会话不存在、3 内容不合适、4 超长。</para>
    /// </summary>
    public static int DecodeSendMessageStatus(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire == 0 && field == 1)
            {
                reader.TryReadVarint(out var value);
                return (int)value;
            }

            reader.Skip(wire);
        }

        return 0;
    }


    /// <summary><c>LoadConversationRequest { ConversationReference conversationReference = 1; }</c>。</summary>
    public static byte[] EncodeLoadConversation(byte[] reference)
    {
        var request = new ProtoWriter();
        request.Message(1, reference);
        return request.ToArray();
    }


    /// <summary><c>SendConversationMessageRequest { reference = 1; ConversationMessageContents messageContents = 2; }</c>。</summary>
    public static byte[] EncodeSendMessage(byte[] reference, string text)
    {
        var contents = new ProtoWriter();
        contents.String(1, text);

        var request = new ProtoWriter();
        request.Message(1, reference);
        request.Message(2, contents.ToArray());
        return request.ToArray();
    }


    /// <summary>
    ///     解析 <c>GetConversationsResponse</c>：<c>status = 1</c>、
    ///     <c>repeated Conversation conversations = 2</c>、<c>totalConversations = 3</c>。
    /// </summary>
    public static (int Status, IReadOnlyList<LunarConversation> Conversations) DecodeConversations(
        ReadOnlySpan<byte> payload, string selfUuid)
    {
        var status = 0;
        var list = new List<LunarConversation>();

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire == 0 && field == 1)
            {
                reader.TryReadVarint(out var value);
                status = (int)value;
                continue;
            }

            if (wire == 2 && field == 2)
            {
                list.Add(DecodeConversation(reader.ReadBytes(), selfUuid));
                continue;
            }

            reader.Skip(wire);
        }

        return (status, list);
    }


    /// <summary>解析 <c>LoadConversationResponse { repeated ConversationMessage messages = 1; }</c>。</summary>
    public static IReadOnlyList<LunarChatMessage> DecodeConversationMessages(ReadOnlySpan<byte> payload,
        string selfUuid)
    {
        var list = new List<LunarChatMessage>();
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire == 2 && field == 1)
            {
                var message = DecodeMessage(reader.ReadBytes(), selfUuid);
                if (message is not null) list.Add(message);
                continue;
            }

            reader.Skip(wire);
        }

        list.Sort((a, b) => a.SentAt.CompareTo(b.SentAt));
        return list;
    }


    /// <summary>解析 <c>ConversationMessagePush { reference = 1; message = 2; bool updated = 3; }</c>。</summary>
    public static (string ConversationId, string FriendUuid, LunarChatMessage? Message) DecodeMessagePush(
        ReadOnlySpan<byte> payload, string selfUuid)
    {
        var conversationId = string.Empty;
        var friendUuid = string.Empty;
        LunarChatMessage? message = null;

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire != 2)
            {
                reader.Skip(wire);
                continue;
            }

            switch (field)
            {
                case 1:
                    (friendUuid, conversationId) = DecodeConversationReference(reader.ReadBytes());
                    break;
                case 2:
                    message = DecodeMessage(reader.ReadBytes(), selfUuid);
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return (conversationId, friendUuid, message);
    }


    public static (string FriendUuid, string ConversationId) DecodeConversationReference(ReadOnlySpan<byte> payload)
    {
        var friend = default(LunarUuid);
        var conversation = default(LunarUuid);

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    friend = DecodeUuid(reader.ReadBytes());
                    break;
                case 2 when wire == 2:
                    conversation = DecodeUuid(reader.ReadBytes());
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return (friend.ToString(), conversation.ToString());
    }

}
