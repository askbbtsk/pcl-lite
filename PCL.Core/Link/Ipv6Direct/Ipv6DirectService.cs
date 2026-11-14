using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Ipv6Direct;

public enum DirectPeerState
{
    Punching,
    Connected
}

public sealed record DirectPeerSnapshot(
    IPEndPoint Endpoint,
    DirectPeerState State,
    int RttMs,
    bool IsDirectNoAck);

public sealed class Ipv6HostSession : IAsyncDisposable
{
    private readonly Socket _udp;
    private readonly uint _token;
    private readonly ushort _udpPort;
    private readonly IPAddress _publicAddress;
    private readonly ConcurrentDictionary<IPEndPoint, (UdpReliablePeer Peer, bool DirectNoAck)> _peers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _recvTask;
    private readonly Task _probeTask;

    private int _mcPort;

    public string InviteCode { get; }
    public IPAddress PublicAddress => _publicAddress;
    public ushort UdpPort => _udpPort;
    public int McPort
    {
        get => Volatile.Read(ref _mcPort);
        set => Volatile.Write(ref _mcPort, Math.Clamp(value, 1, 65535));
    }

    public event Action? StateChanged;

    private Ipv6HostSession(Socket udp, IPAddress publicAddress, ushort udpPort, uint token, int mcPort)
    {
        _udp = udp;
        _publicAddress = publicAddress;
        _udpPort = udpPort;
        _token = token;
        _mcPort = mcPort;

        InviteCode = InviteCodeCodec.Encode(new InviteCodePayload(
            InviteKind.HostInvite,
            publicAddress,
            udpPort,
            token));

        _recvTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _probeTask = Task.Run(() => ProbeLoopAsync(_cts.Token));
    }

    public static Ipv6HostSession Start(IPAddress publicIpv6, int initialMcPort)
    {
        var udp = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        udp.DualMode = false;
        udp.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

        var localEp = (IPEndPoint)udp.LocalEndPoint!;
        var tokenBytes = RandomNumberGenerator.GetBytes(4);
        var token = BitConverter.ToUInt32(tokenBytes);
        if (token == 0) token = 0x50434C36;

        return new Ipv6HostSession(udp, publicIpv6, (ushort)localEp.Port, token, initialMcPort);
    }

    public bool TryAcceptGuestAck(string? ackCode, out string message)
    {
        if (!InviteCodeCodec.TryDecode(ackCode, out var payload, out var error))
        {
            message = error;
            return false;
        }
        if (payload.Kind != InviteKind.GuestAck)
        {
            message = "这不是好友发来的回执码（应以 ACK6- 开头）。";
            return false;
        }
        if (payload.Token != _token)
        {
            message = "回执码与当前房间不匹配（可能是旧房间或别人房间的回执码）。";
            return false;
        }

        var ep = new IPEndPoint(payload.Address, payload.Port);
        var peer = GetOrCreatePeer(ep, directNoAck: false);
        _ = peer.SendPingAsync();
        StateChanged?.Invoke();
        message = $"已添加好友 [{payload.Address}]:{payload.Port}，正在双向打洞…";
        return true;
    }

    public IReadOnlyList<DirectPeerSnapshot> GetPeers()
    {
        return _peers.Select(kv => new DirectPeerSnapshot(
            kv.Key,
            kv.Value.Peer.IsReachable ? DirectPeerState.Connected : DirectPeerState.Punching,
            kv.Value.Peer.RttMs,
            kv.Value.DirectNoAck)).ToList();
    }

    private UdpReliablePeer GetOrCreatePeer(IPEndPoint ep, bool directNoAck)
    {
        return _peers.GetOrAdd(ep, key =>
        {
            var peer = new UdpReliablePeer(_token, data => SendToAsync(key, data));
            peer.OnStreamAccepted += stream => _ = BridgeStreamToLocalMcAsync(stream, _cts.Token);
            peer.OnConnectedOrHeartbeat += _ => StateChanged?.Invoke();
            return (peer, directNoAck);
        }).Peer;
    }

    private async ValueTask SendToAsync(IPEndPoint ep, ReadOnlyMemory<byte> data)
    {
        try
        {
            await _udp.SendToAsync(data, SocketFlags.None, ep, _cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // 忽略单次发送失败
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        EndPoint remoteAny = new IPEndPoint(IPAddress.IPv6Any, 0);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, remoteAny, ct).ConfigureAwait(false);
                if (res.RemoteEndPoint is not IPEndPoint remoteEp)
                    continue;

                var packet = buffer.AsSpan(0, res.ReceivedBytes);
                if (packet.Length < 7)
                    continue;
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet[0..2]) != UdpReliablePeer.PacketMagic)
                    continue;
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet[2..6]) != _token)
                    continue;

                // 如果房主本地无防火墙拦截，收到携带正确房间 Token 的房客包时直接接纳，免去复制回执码！
                var isNew = !_peers.ContainsKey(remoteEp);
                var peer = GetOrCreatePeer(remoteEp, directNoAck: true);
                peer.HandleIncomingPacket(packet);
                if (isNew)
                    StateChanged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 保持接收
            }
        }
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                foreach (var kv in _peers)
                {
                    await kv.Value.Peer.SendPingAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private async Task BridgeStreamToLocalMcAsync(UdpReliablePeer.ReliableStream tunnelStream, CancellationToken ct)
    {
        var port = Volatile.Read(ref _mcPort);
        if (port <= 0 || port > 65535)
        {
            await tunnelStream.CloseAsync().ConfigureAwait(false);
            return;
        }

        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
        }
        catch
        {
            await tunnelStream.CloseAsync().ConfigureAwait(false);
            return;
        }

        await using var netStream = tcp.GetStream();
        await TunnelBridgeHelper.PumpBidirectionalAsync(netStream, tunnelStream, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp.Dispose();
        foreach (var kv in _peers)
            await kv.Value.Peer.DisposeAsync().ConfigureAwait(false);
        _peers.Clear();
        try { await _recvTask.ConfigureAwait(false); } catch { }
        try { await _probeTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

public sealed class Ipv6GuestSession : IAsyncDisposable
{
    private readonly Socket _udp;
    private readonly IPEndPoint _hostEndpoint;
    private readonly UdpReliablePeer _peer;
    private readonly TcpListener _localProxy;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _recvTask;
    private readonly Task _probeTask;
    private readonly Task _acceptTask;
    private readonly Task _lanAnnounceTask;

    public string AckCode { get; }
    public IPEndPoint HostEndpoint => _hostEndpoint;
    public ushort LocalProxyPort { get; }
    public bool IsConnected => _peer.IsReachable;
    public int RttMs => _peer.RttMs;

    public event Action? StateChanged;

    private Ipv6GuestSession(
        Socket udp,
        IPAddress localPublicIpv6,
        ushort localUdpPort,
        InviteCodePayload hostInvite,
        TcpListener localProxy)
    {
        _udp = udp;
        _hostEndpoint = new IPEndPoint(hostInvite.Address, hostInvite.Port);
        _localProxy = localProxy;
        LocalProxyPort = (ushort)((IPEndPoint)localProxy.LocalEndpoint).Port;

        AckCode = InviteCodeCodec.Encode(new InviteCodePayload(
            InviteKind.GuestAck,
            localPublicIpv6,
            localUdpPort,
            hostInvite.Token));

        _peer = new UdpReliablePeer(hostInvite.Token, SendToHostAsync);
        _peer.OnConnectedOrHeartbeat += _ => StateChanged?.Invoke();

        _recvTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _probeTask = Task.Run(() => ProbeLoopAsync(_cts.Token));
        _acceptTask = Task.Run(() => AcceptLocalMcLoopAsync(_cts.Token));
        _lanAnnounceTask = Task.Run(() => LanBroadcastHelper.RunAnnouncerAsync(
            () => (IsConnected, "好友的 MC 房间", LocalProxyPort),
            _cts.Token));
    }

    public static Ipv6GuestSession Start(IPAddress localPublicIpv6, InviteCodePayload hostInvite)
    {
        if (hostInvite.Kind != InviteKind.HostInvite)
            throw new ArgumentException("必须使用房主生成的 PCL6- 邀请码。", nameof(hostInvite));

        var udp = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        udp.DualMode = false;
        udp.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        var localUdpPort = (ushort)((IPEndPoint)udp.LocalEndPoint!).Port;

        var localProxy = new TcpListener(IPAddress.Loopback, 0);
        localProxy.Start();

        return new Ipv6GuestSession(udp, localPublicIpv6, localUdpPort, hostInvite, localProxy);
    }

    private async ValueTask SendToHostAsync(ReadOnlyMemory<byte> data)
    {
        try
        {
            await _udp.SendToAsync(data, SocketFlags.None, _hostEndpoint, _cts.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        EndPoint remoteAny = new IPEndPoint(IPAddress.IPv6Any, 0);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, remoteAny, ct).ConfigureAwait(false);
                _peer.HandleIncomingPacket(buffer.AsSpan(0, res.ReceivedBytes));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _peer.SendPingAsync().ConfigureAwait(false);
                await Task.Delay(IsConnected ? 1500 : 450, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private async Task AcceptLocalMcLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcp = await _localProxy.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    using (tcp)
                    await using (var netStream = tcp.GetStream())
                    {
                        var tunnel = _peer.OpenStream();
                        await TunnelBridgeHelper.PumpBidirectionalAsync(netStream, tunnel, ct).ConfigureAwait(false);
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _localProxy.Stop();
        _udp.Dispose();
        await _peer.DisposeAsync().ConfigureAwait(false);
        try { await _recvTask.ConfigureAwait(false); } catch { }
        try { await _probeTask.ConfigureAwait(false); } catch { }
        try { await _acceptTask.ConfigureAwait(false); } catch { }
        try { await _lanAnnounceTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

internal static class TunnelBridgeHelper
{
    public static async Task PumpBidirectionalAsync(
        NetworkStream netStream,
        UdpReliablePeer.ReliableStream tunnelStream,
        CancellationToken ct)
    {
        var up = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await netStream.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    await tunnelStream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            catch
            {
            }
            finally
            {
                await tunnelStream.CloseAsync().ConfigureAwait(false);
            }
        }, ct);

        var down = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await tunnelStream.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    await netStream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }, ct);

        await Task.WhenAll(up, down).ConfigureAwait(false);
    }
}
