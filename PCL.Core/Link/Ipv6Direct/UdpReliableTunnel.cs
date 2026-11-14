using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PCL.Core.Link.Ipv6Direct;

internal enum TunnelPacketType : byte
{
    Ping = 0x01,
    Pong = 0x02,
    Open = 0x03,
    Data = 0x04,
    Ack = 0x05,
    Close = 0x06
}

/// <summary>
/// 轻量级、零外部依赖的 UDP 多路复用可靠有序流隧道（带 SACK 与快速重传），
/// 与 UDP 打洞复用同一个底层 Socket，免虚拟网卡承载 MC TCP 连接。
/// </summary>
public sealed class UdpReliablePeer : IAsyncDisposable
{
    public const ushort PacketMagic = 0x5043; // "PC"
    public const int MaxChunkSize = 1160;
    private const int HeaderSize = 2 + 4 + 1 + 4 + 4 + 4 + 8; // Magic(2)+Token(4)+Type(1)+ConnId(4)+Seq(4)+Ack(4)+SackMask(8) = 27

    private readonly uint _token;
    private readonly Func<ReadOnlyMemory<byte>, ValueTask> _sendRawAsync;
    private readonly ConcurrentDictionary<uint, ReliableStream> _streams = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _retransmitLoop;

    private int _nextConnId;
    private long _lastReceivedTicks;
    private long _lastPongTicks;
    private int _rttMs;

    public event Action<ReliableStream>? OnStreamAccepted;
    public event Action<int>? OnConnectedOrHeartbeat;

    public bool IsReachable => Volatile.Read(ref _lastPongTicks) != 0;
    public int RttMs => Volatile.Read(ref _rttMs);
    public DateTimeOffset LastActivityUtc => new(Volatile.Read(ref _lastReceivedTicks), TimeSpan.Zero);

    public UdpReliablePeer(uint token, Func<ReadOnlyMemory<byte>, ValueTask> sendRawAsync)
    {
        _token = token;
        _sendRawAsync = sendRawAsync;
        _lastReceivedTicks = DateTimeOffset.UtcNow.Ticks;
        _retransmitLoop = Task.Run(() => RetransmitLoopAsync(_cts.Token));
    }

    public ValueTask SendPingAsync()
    {
        var pkt = new byte[HeaderSize + 8];
        WriteHeader(pkt, TunnelPacketType.Ping, 0, 0, 0, 0);
        BinaryPrimitives.WriteInt64BigEndian(pkt.AsSpan(HeaderSize, 8), Environment.TickCount64);
        return _sendRawAsync(pkt);
    }

    public ReliableStream OpenStream()
    {
        var connId = (uint)Interlocked.Increment(ref _nextConnId);
        var stream = new ReliableStream(connId, this, isInitiator: true);
        _streams[connId] = stream;
        _ = SendControlAsync(TunnelPacketType.Open, connId, 0);
        return stream;
    }

    public void HandleIncomingPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize)
            return;
        if (BinaryPrimitives.ReadUInt16BigEndian(packet[0..2]) != PacketMagic)
            return;
        var token = BinaryPrimitives.ReadUInt32BigEndian(packet[2..6]);
        if (token != _token)
            return;

        var type = (TunnelPacketType)packet[6];
        var connId = BinaryPrimitives.ReadUInt32BigEndian(packet[7..11]);
        var seq = BinaryPrimitives.ReadUInt32BigEndian(packet[11..15]);
        var ack = BinaryPrimitives.ReadUInt32BigEndian(packet[15..19]);
        var sackMask = BinaryPrimitives.ReadUInt64BigEndian(packet[19..27]);

        Volatile.Write(ref _lastReceivedTicks, DateTimeOffset.UtcNow.Ticks);

        switch (type)
        {
            case TunnelPacketType.Ping:
            {
                var pong = new byte[HeaderSize + 8];
                WriteHeader(pong, TunnelPacketType.Pong, 0, 0, 0, 0);
                if (packet.Length >= HeaderSize + 8)
                    packet.Slice(HeaderSize, 8).CopyTo(pong.AsSpan(HeaderSize, 8));
                _ = _sendRawAsync(pong);
                break;
            }
            case TunnelPacketType.Pong:
            {
                Volatile.Write(ref _lastPongTicks, DateTimeOffset.UtcNow.Ticks);
                if (packet.Length >= HeaderSize + 8)
                {
                    var sentTick = BinaryPrimitives.ReadInt64BigEndian(packet.Slice(HeaderSize, 8));
                    var rtt = (int)Math.Clamp(Environment.TickCount64 - sentTick, 1, 9999);
                    Volatile.Write(ref _rttMs, rtt);
                }
                OnConnectedOrHeartbeat?.Invoke(Volatile.Read(ref _rttMs));
                break;
            }
            case TunnelPacketType.Open:
            {
                var stream = GetOrAcceptStream(connId);
                var (replyAck, replySackMask) = stream.GetAckState();
                // 回复 ACK 确认打开
                _ = SendAckAsync(connId, replyAck, replySackMask);
                break;
            }
            case TunnelPacketType.Data:
            {
                var stream = GetOrAcceptStream(connId);
                stream.HandleData(seq, packet[HeaderSize..]);
                stream.HandleAck(ack, sackMask);
                break;
            }
            case TunnelPacketType.Ack:
            {
                if (_streams.TryGetValue(connId, out var stream))
                    stream.HandleAck(ack, sackMask);
                break;
            }
            case TunnelPacketType.Close:
            {
                var stream = GetOrAcceptStream(connId);
                stream.HandleRemoteClose(seq);
                break;
            }
        }
    }

    private ReliableStream GetOrAcceptStream(uint connId)
    {
        if (_streams.TryGetValue(connId, out var existing))
            return existing;

        var created = new ReliableStream(connId, this, isInitiator: false);
        var actual = _streams.GetOrAdd(connId, created);
        if (ReferenceEquals(actual, created))
            OnStreamAccepted?.Invoke(created);
        return actual;
    }

    internal ValueTask SendControlAsync(TunnelPacketType type, uint connId, uint seq)
    {
        var pkt = new byte[HeaderSize];
        WriteHeader(pkt, type, connId, seq, 0, 0);
        return _sendRawAsync(pkt);
    }

    internal ValueTask SendAckAsync(uint connId, uint ack, ulong sackMask)
    {
        var pkt = new byte[HeaderSize];
        WriteHeader(pkt, TunnelPacketType.Ack, connId, 0, ack, sackMask);
        return _sendRawAsync(pkt);
    }

    internal ValueTask SendDataPacketAsync(uint connId, uint seq, uint ack, ulong sackMask, ReadOnlySpan<byte> payload)
    {
        var pkt = new byte[HeaderSize + payload.Length];
        WriteHeader(pkt, TunnelPacketType.Data, connId, seq, ack, sackMask);
        payload.CopyTo(pkt.AsSpan(HeaderSize));
        return _sendRawAsync(pkt);
    }

    private void WriteHeader(Span<byte> buf, TunnelPacketType type, uint connId, uint seq, uint ack, ulong sackMask)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buf[0..2], PacketMagic);
        BinaryPrimitives.WriteUInt32BigEndian(buf[2..6], _token);
        buf[6] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(buf[7..11], connId);
        BinaryPrimitives.WriteUInt32BigEndian(buf[11..15], seq);
        BinaryPrimitives.WriteUInt32BigEndian(buf[15..19], ack);
        BinaryPrimitives.WriteUInt64BigEndian(buf[19..27], sackMask);
    }

    private async Task RetransmitLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(15, ct).ConfigureAwait(false);
                var now = Environment.TickCount64;
                foreach (var kv in _streams)
                {
                    await kv.Value.PumpRetransmitsAsync(now).ConfigureAwait(false);
                    if (kv.Value.IsFullyClosed)
                        _streams.TryRemove(kv.Key, out _);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 保持调度循环运行
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var kv in _streams)
            kv.Value.ForceClose();
        _streams.Clear();
        try { await _retransmitLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    public sealed class ReliableStream
    {
        private sealed class PendingPacket(uint seq, byte[] data, long firstSentMs)
        {
            public uint Seq { get; } = seq;
            public byte[] Data { get; } = data;
            public long LastSentMs { get; set; } = firstSentMs;
            public int RetransmitCount { get; set; }
            public bool IsClose { get; init; }
        }

        private const int MaxWindow = 64;

        private readonly uint _connId;
        private readonly UdpReliablePeer _peer;
        private readonly object _sendLock = new();
        private readonly SortedDictionary<uint, PendingPacket> _unacked = new();
        private readonly SemaphoreSlim _windowSignal = new(MaxWindow, MaxWindow);

        private readonly object _recvLock = new();
        private readonly SortedDictionary<uint, byte[]> _outOfOrder = new();
        private readonly Channel<byte[]> _incomingChunks = Channel.CreateUnbounded<byte[]>();

        private uint _nextSendSeq;
        private uint _recvExpectedSeq;
        private uint? _remoteCloseSeq;
        private bool _localClosed;
        private bool _remoteClosedDelivered;

        public uint ConnId => _connId;
        public uint ExpectedReceiveSeq { get { lock (_recvLock) return _recvExpectedSeq; } }
        public bool IsFullyClosed => _localClosed && _remoteClosedDelivered && _unacked.IsEmpty();

        internal ReliableStream(uint connId, UdpReliablePeer peer, bool isInitiator)
        {
            _connId = connId;
            _peer = peer;
            _ = isInitiator;
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var len = Math.Min(MaxChunkSize, data.Length - offset);
                var slice = data.Slice(offset, len);
                offset += len;

                await _windowSignal.WaitAsync(ct).ConfigureAwait(false);
                uint seq;
                byte[] copy;
                lock (_sendLock)
                {
                    if (_localClosed)
                        throw new ObjectDisposedException(nameof(ReliableStream));
                    seq = _nextSendSeq++;
                    copy = slice.ToArray();
                    _unacked[seq] = new PendingPacket(seq, copy, Environment.TickCount64);
                }

                var (ack, sackMask) = GetAckState();
                await _peer.SendDataPacketAsync(
                    _connId, seq, ack, sackMask, copy).ConfigureAwait(false);
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            while (true)
            {
                if (_pendingReadBuffer is { Length: > 0 } buf && _pendingReadOffset < buf.Length)
                {
                    var take = Math.Min(destination.Length, buf.Length - _pendingReadOffset);
                    buf.AsMemory(_pendingReadOffset, take).CopyTo(destination);
                    _pendingReadOffset += take;
                    if (_pendingReadOffset >= buf.Length)
                    {
                        _pendingReadBuffer = null;
                        _pendingReadOffset = 0;
                    }
                    return take;
                }

                if (!await _incomingChunks.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    return 0;

                if (_incomingChunks.Reader.TryRead(out var chunk))
                {
                    if (chunk.Length == 0)
                        return 0;
                    _pendingReadBuffer = chunk;
                    _pendingReadOffset = 0;
                }
            }
        }

        private byte[]? _pendingReadBuffer;
        private int _pendingReadOffset;

        public async Task CloseAsync()
        {
            uint closeSeq;
            lock (_sendLock)
            {
                if (_localClosed) return;
                _localClosed = true;
                closeSeq = _nextSendSeq++;
                _unacked[closeSeq] = new PendingPacket(closeSeq, [], Environment.TickCount64) { IsClose = true };
            }
            await _peer.SendControlAsync(TunnelPacketType.Close, _connId, closeSeq).ConfigureAwait(false);
        }

        internal void ForceClose()
        {
            lock (_sendLock)
            {
                _localClosed = true;
                _unacked.Clear();
            }
            _incomingChunks.Writer.TryComplete();
        }

        internal void HandleData(uint seq, ReadOnlySpan<byte> payload)
        {
            var sendAck = false;
            (uint Ack, ulong SackMask) ackState = default;
            lock (_recvLock)
            {
                if (seq < _recvExpectedSeq)
                {
                    sendAck = true;
                }
                else if (seq == _recvExpectedSeq)
                {
                    _incomingChunks.Writer.TryWrite(payload.ToArray());
                    _recvExpectedSeq++;
                    while (_outOfOrder.Remove(_recvExpectedSeq, out var buffered))
                    {
                        _incomingChunks.Writer.TryWrite(buffered);
                        _recvExpectedSeq++;
                    }
                    CheckRemoteCloseLocked();
                    sendAck = true;
                }
                else if (seq - _recvExpectedSeq <= 64)
                {
                    _outOfOrder.TryAdd(seq, payload.ToArray());
                    sendAck = true;
                }

                if (sendAck)
                    ackState = GetAckStateLocked();
            }

            if (sendAck)
                _ = _peer.SendAckAsync(_connId, ackState.Ack, ackState.SackMask);
        }

        internal void HandleRemoteClose(uint seq)
        {
            (uint Ack, ulong SackMask) ackState;
            lock (_recvLock)
            {
                _remoteCloseSeq = seq;
                CheckRemoteCloseLocked();
                ackState = GetAckStateLocked();
            }
            _ = _peer.SendAckAsync(_connId, ackState.Ack, ackState.SackMask);
        }

        private void CheckRemoteCloseLocked()
        {
            if (_remoteCloseSeq is { } closeSeq && _recvExpectedSeq >= closeSeq && !_remoteClosedDelivered)
            {
                _remoteClosedDelivered = true;
                _recvExpectedSeq = closeSeq + 1;
                _incomingChunks.Writer.TryComplete();
            }
        }

        internal (uint Ack, ulong SackMask) GetAckState()
        {
            lock (_recvLock)
                return GetAckStateLocked();
        }

        private (uint Ack, ulong SackMask) GetAckStateLocked()
        {
            ulong mask = 0;
            foreach (var seq in _outOfOrder.Keys)
            {
                var diff = (int)(seq - _recvExpectedSeq);
                if (diff >= 1 && diff <= 64)
                    mask |= 1UL << (diff - 1);
            }
            return (_recvExpectedSeq, mask);
        }

        internal void HandleAck(uint ack, ulong sackMask)
        {
            var released = 0;
            lock (_sendLock)
            {
                var toRemove = new List<uint>();
                foreach (var seq in _unacked.Keys)
                {
                    if (seq < ack)
                    {
                        toRemove.Add(seq);
                    }
                    else
                    {
                        var diff = (int)(seq - ack);
                        if (diff >= 1 && diff <= 64 && (sackMask & (1UL << (diff - 1))) != 0)
                            toRemove.Add(seq);
                    }
                }

                foreach (var seq in toRemove)
                {
                    if (_unacked.Remove(seq))
                        released++;
                }
            }

            if (released > 0)
                _windowSignal.Release(released);
        }

        internal async Task PumpRetransmitsAsync(long nowMs)
        {
            List<PendingPacket>? due = null;
            lock (_sendLock)
            {
                var rto = Math.Clamp(_peer.RttMs > 0 ? _peer.RttMs * 2 : 60, 35, 400);
                foreach (var pkt in _unacked.Values)
                {
                    if (nowMs - pkt.LastSentMs >= rto)
                    {
                        pkt.LastSentMs = nowMs;
                        pkt.RetransmitCount++;
                        due ??= [];
                        due.Add(pkt);
                    }
                }
            }

            if (due is null) return;
            foreach (var pkt in due)
            {
                if (pkt.IsClose)
                {
                    await _peer.SendControlAsync(TunnelPacketType.Close, _connId, pkt.Seq).ConfigureAwait(false);
                }
                else
                {
                    var (ack, sackMask) = GetAckState();
                    await _peer.SendDataPacketAsync(
                        _connId, pkt.Seq, ack, sackMask, pkt.Data).ConfigureAwait(false);
                }
            }
        }
    }
}

file static class DictionaryExtensions
{
    public static bool IsEmpty<TKey, TValue>(this SortedDictionary<TKey, TValue> dict) where TKey : notnull =>
        dict.Count == 0;
}
