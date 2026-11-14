using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Core.Link.Ipv6Direct;

public enum InviteKind : byte
{
    HostInvite = 0x11,
    GuestAck = 0x12
}

public readonly record struct InviteCodePayload(
    InviteKind Kind,
    IPAddress Address,
    ushort Port,
    uint Token,
    TunnelSecret? Secret = null,
    byte[]? PeerId = null,
    byte[]? Proof = null)
{
    public override string ToString() => $"{Kind}: [hide]";
}

/// <summary>
/// BSK / ACK 均为前缀加 26 个 GB2312 一级汉字，共 30 字符。
/// 汉字编码和 4 位校验仅用于复制；BSK 是房间能力凭据，必须经可信渠道分享。
/// ACK 另有 96 位 HMAC，UDP 保留完整 HMAC；游戏载荷不加密。
/// </summary>
public static class InviteCodeCodec
{
    public const string HostPrefix = "BSK-";
    public const string GuestAckPrefix = "ACK-";
    public const int CodeLength = 30;
    internal const int AckProofSize = 12;
    private const int EndpointLength = 18;
    private const int SignedAckLength = 2 + EndpointLength + 4 + TunnelAuthentication.PeerIdSize;

    public static string Encode(InviteCodePayload payload)
    {
        Span<byte> buffer = stackalloc byte[CommonHanziCode.PayloadLength];
        WriteEndpoint(buffer, payload);
        if (payload.Kind == InviteKind.HostInvite)
        {
            ArgumentNullException.ThrowIfNull(payload.Secret);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[18..22], payload.Token);
            payload.Secret.CopyTo(buffer[22..]);
        }
        else if (payload.Kind == InviteKind.GuestAck)
        {
            if (payload.PeerId?.Length != TunnelAuthentication.PeerIdSize || payload.Proof?.Length != AckProofSize)
                throw new ArgumentException("Guest ACK requires authenticated session credentials.", nameof(payload));
            payload.PeerId.CopyTo(buffer[18..26]);
            payload.Proof.CopyTo(buffer[26..]);
        }
        else
        {
            throw new ArgumentException("Invalid invite kind.", nameof(payload));
        }
        var prefix = payload.Kind == InviteKind.HostInvite ? HostPrefix : GuestAckPrefix;
        return prefix + CommonHanziCode.Encode(buffer, payload.Kind);
    }

    public static InviteCodePayload CreateGuestAck(InviteCodePayload hostInvite, IPAddress address, ushort port, ReadOnlySpan<byte> peerId)
    {
        if (hostInvite.Kind != InviteKind.HostInvite || hostInvite.Secret is null || peerId.Length != TunnelAuthentication.PeerIdSize)
            throw new ArgumentException("A current BSK invite and session ID are required.", nameof(hostInvite));
        var ack = new InviteCodePayload(InviteKind.GuestAck, address, port, hostInvite.Token, PeerId: peerId.ToArray());
        Span<byte> signed = stackalloc byte[SignedAckLength];
        WriteSignedAck(signed, ack, hostInvite.Token);
        var signature = hostInvite.Secret.Sign(signed);
        try { return ack with { Proof = signature.AsSpan(0, AckProofSize).ToArray() }; }
        finally { CryptographicOperations.ZeroMemory(signature); }
    }

    public static bool VerifyGuestAck(InviteCodePayload ack, uint token, TunnelSecret secret)
    {
        // ACK 不重复传输房间标识；必须绑定房主当前房间的 token，不能信任输入中的值。
        if (ack.Kind != InviteKind.GuestAck || ack.Address is null ||
            ack.Address.AddressFamily != AddressFamily.InterNetworkV6 || ack.Port == 0 ||
            ack.PeerId?.Length != TunnelAuthentication.PeerIdSize || ack.Proof?.Length != AckProofSize)
            return false;
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> signed = stackalloc byte[SignedAckLength];
        WriteSignedAck(signed, ack, token);
        var expected = secret.Sign(signed);
        try { return CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, AckProofSize), ack.Proof); }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    private static void WriteSignedAck(Span<byte> buffer, InviteCodePayload ack, uint token)
    {
        buffer[0] = TunnelAuthentication.Version;
        buffer[1] = (byte)InviteKind.GuestAck;
        WriteEndpoint(buffer[2..], ack);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[20..24], token);
        ack.PeerId!.CopyTo(buffer[24..]);
    }

    private static void WriteEndpoint(Span<byte> buffer, InviteCodePayload payload)
    {
        if (payload.Port == 0 || payload.Address is null || payload.Address.AddressFamily != AddressFamily.InterNetworkV6 ||
            !payload.Address.TryWriteBytes(buffer[..16], out var written) || written != 16)
            throw new ArgumentException("Invalid invite endpoint.", nameof(payload));
        BinaryPrimitives.WriteUInt16BigEndian(buffer[16..18], payload.Port);
    }

    public static bool TryDecode(string? code, out InviteCodePayload payload, out string error)
    {
        payload = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(code) || code.Length > 1024)
        {
            error = "请输入完整的 30 字符 BSK 邀请码或 ACK 回执码。";
            return false;
        }
        var cleaned = new StringBuilder(CodeLength);
        foreach (var ch in code)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (cleaned.Length == CodeLength)
            {
                error = "邀请码格式已更新；双方更新启动器后重新生成 30 字符代码。";
                return false;
            }
            cleaned.Append(ch);
        }
        var text = cleaned.ToString();
        if (text.Length != CodeLength)
        {
            error = "代码应为前缀加 26 个汉字，共 30 字符；请双方更新后重新生成。";
            return false;
        }
        InviteKind kind;
        if (text.StartsWith(HostPrefix, StringComparison.OrdinalIgnoreCase)) kind = InviteKind.HostInvite;
        else if (text.StartsWith(GuestAckPrefix, StringComparison.OrdinalIgnoreCase)) kind = InviteKind.GuestAck;
        else
        {
            error = "邀请码前缀无效（应为 BSK- 或 ACK-）。";
            return false;
        }
        if (!CommonHanziCode.TryDecode(text.AsSpan(4), kind, out var raw))
        {
            error = "代码无效或复制不完整，请重新复制好友发送的原文。";
            return false;
        }
        try
        {
            var port = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(16, 2));
            if (port == 0)
            {
                error = "代码中的端口无效，请重新生成。";
                return false;
            }
            var address = new IPAddress(raw.AsSpan(0, 16));
            payload = kind == InviteKind.HostInvite
                ? new InviteCodePayload(kind, address, port, BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(18, 4)),
                    TunnelSecret.Read(raw.AsSpan(22, TunnelSecret.Size)))
                : new InviteCodePayload(kind, address, port, 0,
                    PeerId: raw.AsSpan(18, TunnelAuthentication.PeerIdSize).ToArray(), Proof: raw.AsSpan(26, AckProofSize).ToArray());
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
}
