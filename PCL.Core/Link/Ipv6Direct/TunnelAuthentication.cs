using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;

namespace PCL.Core.Link.Ipv6Direct;

/// <summary>每个房间重新生成的 128 位能力密钥。仅通过 BSK 邀请码交付，不发送到 UDP 链路。</summary>
public sealed class TunnelSecret
{
    internal const int Size = 16;
    private readonly byte[] _bytes;

    private TunnelSecret(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
    public static TunnelSecret Create() => new(RandomNumberGenerator.GetBytes(Size));
    internal static TunnelSecret Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size) throw new ArgumentException("Invalid tunnel key length.", nameof(bytes));
        return new(bytes);
    }
    internal void CopyTo(Span<byte> destination) => _bytes.CopyTo(destination);
    internal byte[] Sign(ReadOnlySpan<byte> message) => HMACSHA256.HashData(_bytes, message);
    internal bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(_bytes, message, expected);
        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }
    public override string ToString() => "[hide]";
}

/// <summary>只认证，不加密：游戏字节原样封装。方向独立密钥、房客随机会话 ID、64 位计数器和重放窗口。</summary>
internal sealed class TunnelAuthentication : IDisposable
{
    internal const byte Version = 3;
    internal const int PeerIdSize = 8;
    internal const int WireHeaderSize = 16 + PeerIdSize;
    internal const int TagSize = 32;
    private const uint ProtocolMarker = 0x484D4143; // HMAC；原来的明文 token 不再上网
    private const int WindowSize = 4096;
    private readonly uint _token;
    private readonly bool _isHost;
    private readonly byte[] _peerId;
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private readonly object _gate = new();
    private readonly ulong[] _received = new ulong[WindowSize];
    private ulong _highest;
    private long _counter;
    private bool _disposed;

    internal TunnelAuthentication(uint token, TunnelSecret secret, ReadOnlySpan<byte> peerId, bool isHost)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (peerId.Length != PeerIdSize) throw new ArgumentException("Invalid peer ID length.", nameof(peerId));
        _token = token;
        _isHost = isHost;
        _peerId = peerId.ToArray();
        _sendKey = DeriveKey(secret, token, peerId, isHost);
        _receiveKey = DeriveKey(secret, token, peerId, !isHost);
    }

    private static byte[] DeriveKey(TunnelSecret secret, uint token, ReadOnlySpan<byte> peerId, bool fromHost)
    {
        Span<byte> context = stackalloc byte[31 + PeerIdSize];
        "PCL-Lite/UDP/HMAC/v3/key/"u8.CopyTo(context);
        BinaryPrimitives.WriteUInt32BigEndian(context[25..29], token);
        peerId.CopyTo(context[29..]);
        context[^2] = Version;
        context[^1] = fromHost ? (byte)1 : (byte)0;
        return secret.Sign(context);
    }

    internal byte[] Protect(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 27 || packet.Length > 27 + UdpReliablePeer.MaxChunkSize)
            throw new ArgumentException("Invalid tunnel packet length.", nameof(packet));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var counter = Interlocked.Increment(ref _counter);
            if (counter <= 0) throw new InvalidOperationException("Tunnel packet counter exhausted; create a new room.");
            var wire = new byte[WireHeaderSize + packet.Length - 6 + TagSize];
            packet[..2].CopyTo(wire);
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(2, 4), ProtocolMarker);
            wire[6] = Version;
            wire[7] = _isHost ? (byte)1 : (byte)0;
            _peerId.CopyTo(wire.AsSpan(8, PeerIdSize));
            BinaryPrimitives.WriteUInt64BigEndian(wire.AsSpan(8 + PeerIdSize, 8), (ulong)counter);
            packet[6..].CopyTo(wire.AsSpan(WireHeaderSize));
            HMACSHA256.HashData(_sendKey, wire.AsSpan(0, wire.Length - TagSize), wire.AsSpan(wire.Length - TagSize));
            return wire;
        }
    }

    internal bool TryUnprotect(ReadOnlySpan<byte> wire, out byte[] packet)
    {
        packet = [];
        if (!HasValidHeader(wire, _token, !_isHost) || !wire.Slice(8, PeerIdSize).SequenceEqual(_peerId)) return false;
        lock (_gate)
        {
            if (_disposed || !Verify(wire, _receiveKey)) return false;
            var counter = BinaryPrimitives.ReadUInt64BigEndian(wire.Slice(8 + PeerIdSize, 8));
            if (counter == 0 || counter > long.MaxValue ||
                (_highest >= counter && _highest - counter >= WindowSize) ||
                _received[counter % WindowSize] == counter) return false;
            _received[counter % WindowSize] = counter;
            _highest = Math.Max(_highest, counter);
            packet = new byte[6 + wire.Length - WireHeaderSize - TagSize];
            wire[..2].CopyTo(packet);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2, 4), _token);
            wire.Slice(WireHeaderSize, wire.Length - WireHeaderSize - TagSize).CopyTo(packet.AsSpan(6));
            return true;
        }
    }

    // 在为未知端点创建 Peer、计入好友列表或回复之前验签，32 位 token 只用来选房间。
    internal static bool TryAuthenticateGuest(ReadOnlySpan<byte> wire, uint token, TunnelSecret secret, out byte[] peerId)
    {
        peerId = [];
        if (!HasValidHeader(wire, token, fromHost: false)) return false;
        var key = DeriveKey(secret, token, wire.Slice(8, PeerIdSize), fromHost: false);
        try
        {
            if (!Verify(wire, key)) return false;
            peerId = wire.Slice(8, PeerIdSize).ToArray();
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static bool HasValidHeader(ReadOnlySpan<byte> wire, uint token, bool fromHost) =>
        wire.Length >= WireHeaderSize + 21 + TagSize &&
        wire.Length <= WireHeaderSize + 21 + UdpReliablePeer.MaxChunkSize + TagSize &&
        BinaryPrimitives.ReadUInt16BigEndian(wire[..2]) == UdpReliablePeer.PacketMagic &&
        BinaryPrimitives.ReadUInt32BigEndian(wire[2..6]) == ProtocolMarker &&
        wire[6] == Version && wire[7] == (fromHost ? 1 : 0) &&
        BinaryPrimitives.ReadUInt64BigEndian(wire.Slice(8 + PeerIdSize, 8)) is > 0 and <= long.MaxValue;

    private static bool Verify(ReadOnlySpan<byte> wire, ReadOnlySpan<byte> key)
    {
        Span<byte> expected = stackalloc byte[TagSize];
        HMACSHA256.HashData(key, wire[..^TagSize], expected);
        return CryptographicOperations.FixedTimeEquals(expected, wire[^TagSize..]);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CryptographicOperations.ZeroMemory(_sendKey);
            CryptographicOperations.ZeroMemory(_receiveKey);
        }
    }
}
