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

    /// <summary>
    ///     解析一个 <c>Conversation</c>。
    ///     <para>
    ///         一对一会话里 <c>participants</c> 有两个（自己和好友），
    ///         <b>好友那一半才是界面上要显示的人</b> —— 所以这里直接按
    ///         <paramref name="selfUuid" /> 挑出对方。
    ///     </para>
    /// </summary>
    private static LunarConversation DecodeConversation(ReadOnlySpan<byte> payload, string selfUuid)
=>LunarMessageCodec.DecodeConversation(payload, selfUuid);


    /// <summary><c>ConversationParticipant { UuidAndUsername player = 1; ... }</c>。</summary>
    private static (string Uuid, string Name) DecodeParticipant(ReadOnlySpan<byte> payload)
=>LunarMessageCodec.DecodeParticipant(payload);


    private static LunarChatMessage? DecodeMessage(ReadOnlySpan<byte> payload, string selfUuid)
=>LunarMessageCodec.DecodeMessage(payload, selfUuid);




    /// <summary><c>Uuid { fixed64 high64 = 1; fixed64 low64 = 2; }</c>。</summary>
    public static void WriteUuid(ProtoWriter writer, int field, LunarUuid uuid)
=>LunarMessageCodec.WriteUuid(writer, field, uuid);


    public static LunarUuid DecodeUuid(ReadOnlySpan<byte> payload)
=>LunarMessageCodec.DecodeUuid(payload);


    /// <summary><c>google.protobuf.Timestamp { int64 seconds = 1; int32 nanos = 2; }</c>。</summary>
    private static DateTimeOffset DecodeTimestamp(ReadOnlySpan<byte> payload)
=>LunarMessageCodec.DecodeTimestamp(payload);


    /// <summary>
    ///     Minecraft 的 <c>serverId</c>：<c>sha1(sharedSecret ‖ publicKey)</c> 的补码十六进制。
    ///     <para>
    ///         这是 Mojang 那套老规矩（<c>ServerHash</c>），高位是 1 时要按二进制补码取负、
    ///         前面加 <c>-</c>，否则 <c>sessionserver</c> 会和认证服务算出不同的哈希。
    ///     </para>
    /// </summary>
    public static string ComputeServerHash(byte[] sharedSecret, byte[] publicKey)
=>LunarMessageCodec.ComputeServerHash(sharedSecret, publicKey);

}
