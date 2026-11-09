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

using static PCL.Core.App.Lunar.Social.LunarSocialProtocol;

namespace PCL.Core.App.Lunar.Social;

internal static class LunarMessageCodec
{

    /// <summary>
    ///     解析一个 <c>Conversation</c>。
    ///     <para>
    ///         一对一会话里 <c>participants</c> 有两个（自己和好友），
    ///         <b>好友那一半才是界面上要显示的人</b> —— 所以这里直接按
    ///         <paramref name="selfUuid" /> 挑出对方。
    ///     </para>
    /// </summary>
    internal static LunarConversation DecodeConversation(ReadOnlySpan<byte> payload, string selfUuid)
    {
        var conversationId = string.Empty;
        var peerUuid = string.Empty;
        var peerName = string.Empty;
        var name = string.Empty;
        var unread = 0;
        LunarChatMessage? latest = null;

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire != 2)
            {
                if (wire == 0 && field == 3) reader.TryReadVarint(out _); // type
                else reader.Skip(wire);
                continue;
            }

            switch (field)
            {
                case 1:
                {
                    var (friend, conversation) = DecodeConversationReference(reader.ReadBytes());
                    conversationId = conversation;
                    if (friend.Length > 0 && !friend.StartsWith("00000000-", StringComparison.Ordinal))
                        (peerUuid, peerName) = (friend, string.Empty);
                    break;
                }
                case 2:
                {
                    var (uuid, username) = DecodeParticipant(reader.ReadBytes());
                    if (uuid.Length > 0 && !string.Equals(uuid, selfUuid, StringComparison.OrdinalIgnoreCase))
                    {
                        peerUuid = uuid;
                        peerName = username;
                    }

                    break;
                }
                case 5:
                    name = reader.ReadString();
                    break;
                case 8:
                {
                    var state = new ProtoReader(reader.ReadBytes());
                    while (state.TryReadTag(out var f, out var w))
                    {
                        if (f == 2 && w == 0)
                        {
                            state.TryReadVarint(out var value);
                            unread = (int)value;
                        }
                        else
                        {
                            state.Skip(w);
                        }
                    }

                    break;
                }
                case 14:
                    latest = DecodeMessage(reader.ReadBytes(), selfUuid);
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (peerName.Length == 0) peerName = name;
        var messages = latest is null ? Array.Empty<LunarChatMessage>() : [latest];
        return new LunarConversation(conversationId, peerUuid, peerName, unread, messages);
    }



    /// <summary><c>ConversationParticipant { UuidAndUsername player = 1; ... }</c>。</summary>
    internal static (string Uuid, string Name) DecodeParticipant(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2)
            {
                var (uuid, name) = DecodePlayer(reader.ReadBytes());
                return (uuid.ToString(), name);
            }

            reader.Skip(wire);
        }

        return (string.Empty, string.Empty);
    }



    internal static LunarChatMessage? DecodeMessage(ReadOnlySpan<byte> payload, string selfUuid)
    {
        var id = default(LunarUuid);
        var sentAt = DateTimeOffset.UtcNow;
        var senderUuid = string.Empty;
        var senderName = string.Empty;
        var text = string.Empty;

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
                    id = DecodeUuid(reader.ReadBytes());
                    break;
                case 2:
                    sentAt = DecodeTimestamp(reader.ReadBytes());
                    break;
                case 3:
                {
                    var sender = new ProtoReader(reader.ReadBytes());
                    while (sender.TryReadTag(out var f, out var w))
                    {
                        if (f == 1 && w == 2)
                        {
                            var (uuid, name) = DecodePlayer(sender.ReadBytes());
                            senderUuid = uuid.ToString();
                            senderName = name;
                        }
                        else
                        {
                            sender.Skip(w);
                        }
                    }

                    break;
                }
                case 4:
                {
                    var contents = new ProtoReader(reader.ReadBytes());
                    while (contents.TryReadTag(out var f, out var w))
                    {
                        if (f == 1 && w == 2) text = contents.ReadString();
                        else contents.Skip(w);
                    }

                    break;
                }
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (id.IsEmpty && text.Length == 0) return null;
        return new LunarChatMessage(id.ToString(), senderUuid, senderName, text, sentAt,
            selfUuid.Length > 0 && string.Equals(senderUuid, selfUuid, StringComparison.OrdinalIgnoreCase));
    }





    /// <summary><c>Uuid { fixed64 high64 = 1; fixed64 low64 = 2; }</c>。</summary>
    public static void WriteUuid(ProtoWriter writer, int field, LunarUuid uuid)
    {
        var nested = new ProtoWriter();
        nested.Fixed64(1, uuid.High);
        nested.Fixed64(2, uuid.Low);
        writer.Bytes(field, nested.ToArray());
    }



    public static LunarUuid DecodeUuid(ReadOnlySpan<byte> payload)
    {
        ulong high = 0, low = 0;
        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 1:
                    high = reader.ReadFixed64();
                    break;
                case 2 when wire == 1:
                    low = reader.ReadFixed64();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new LunarUuid(high, low);
    }



    /// <summary><c>google.protobuf.Timestamp { int64 seconds = 1; int32 nanos = 2; }</c>。</summary>
    internal static DateTimeOffset DecodeTimestamp(ReadOnlySpan<byte> payload)
    {
        ulong seconds = 0;
        ulong nanos = 0;

        var reader = new ProtoReader(payload);
        while (reader.TryReadTag(out var field, out var wire))
        {
            if (wire != 0)
            {
                reader.Skip(wire);
                continue;
            }

            switch (field)
            {
                case 1:
                    reader.TryReadVarint(out seconds);
                    break;
                case 2:
                    reader.TryReadVarint(out nanos);
                    break;
            }
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds).AddTicks((long)(nanos / 100));
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UtcNow;
        }
    }



    /// <summary>
    ///     Minecraft 的 <c>serverId</c>：<c>sha1(sharedSecret ‖ publicKey)</c> 的补码十六进制。
    ///     <para>
    ///         这是 Mojang 那套老规矩（<c>ServerHash</c>），高位是 1 时要按二进制补码取负、
    ///         前面加 <c>-</c>，否则 <c>sessionserver</c> 会和认证服务算出不同的哈希。
    ///     </para>
    /// </summary>
    public static string ComputeServerHash(byte[] sharedSecret, byte[] publicKey)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var buffer = new byte[sharedSecret.Length + publicKey.Length];
        Buffer.BlockCopy(sharedSecret, 0, buffer, 0, sharedSecret.Length);
        Buffer.BlockCopy(publicKey, 0, buffer, sharedSecret.Length, publicKey.Length);
        var hash = sha1.ComputeHash(buffer);

        var negative = (hash[0] & 0x80) != 0;
        if (negative)
        {
            // 二进制补码：按字节取反再整体 +1
            var carry = 1;
            for (var i = hash.Length - 1; i >= 0; i--)
            {
                var value = (hash[i] ^ 0xFF) + carry;
                carry = value > 0xFF ? 1 : 0;
                hash[i] = (byte)(value & 0xFF);
            }
        }

        var text = Convert.ToHexString(hash).TrimStart('0').ToLowerInvariant();
        if (text.Length == 0) text = "0";
        return negative ? "-" + text : text;
    }

}
