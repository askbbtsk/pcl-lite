using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

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
    bool IsDirectNoAck,
    DateTimeOffset AddedUtc = default,
    int VirtualId = 0);

public sealed class Ipv6HostSession : IAsyncDisposable
{
    private sealed class PeerEntry
    {
        public required int VirtualId { get; init; }
        public required UdpReliablePeer Peer { get; init; }
        public required bool DirectNoAck { get; init; }
        public required DateTimeOffset AddedUtc { get; init; }
        public int ConnectedLogged;
    }

    private readonly Socket _udp;
    private readonly uint _token;
    private readonly ushort _udpPort;
    private readonly IPAddress _publicAddress;
    private readonly bool _requireOnlineAuth;
    private readonly ConcurrentDictionary<IPEndPoint, PeerEntry> _peers = new();
    private readonly object _peerCreateLock = new();
    private readonly ConcurrentDictionary<int, byte> _loggedErrors = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _recvTask;
    private readonly Task _probeTask;

    private int _mcPort;
    private int _mcConnectWarned;
    private int _nextPeerId;

    public string InviteCode { get; }
    public IPAddress PublicAddress => _publicAddress;
    public ushort UdpPort => _udpPort;
    public bool RequireOnlineAuth => _requireOnlineAuth;
    public int McPort
    {
        get => Volatile.Read(ref _mcPort);
        set => Volatile.Write(ref _mcPort, Math.Clamp(value, 1, 65535));
    }

    public event Action? StateChanged;

    private Ipv6HostSession(
        Socket udp,
        IPAddress publicAddress,
        ushort udpPort,
        uint token,
        int mcPort,
        bool requireOnlineAuth)
    {
        _udp = udp;
        _publicAddress = publicAddress;
        _udpPort = udpPort;
        _token = token;
        _mcPort = mcPort;
        _requireOnlineAuth = requireOnlineAuth;

        InviteCode = InviteCodeCodec.Encode(new InviteCodePayload(
            InviteKind.HostInvite,
            publicAddress,
            udpPort,
            token));

        var actualLocal = _udp.LocalEndPoint as IPEndPoint;
        LogWrapper.Info("Link",
            $"[Host] 房间已启动：邀请地址 [{publicAddress}]:{udpPort}，实际绑定 {actualLocal}，目标 MC 端口 {mcPort}，正版验证={(requireOnlineAuth ? "开启" : "关闭")}");

        _recvTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _probeTask = Task.Run(() => ProbeLoopAsync(_cts.Token));
    }

    public static Ipv6HostSession Start(IPAddress publicIpv6, int initialMcPort, bool requireOnlineAuth = false)
    {
        var udp = TunnelBridgeHelper.BindUdpSocket(publicIpv6, "[Host]");

        var localEp = (IPEndPoint)udp.LocalEndPoint!;
        var tokenBytes = RandomNumberGenerator.GetBytes(4);
        var token = BitConverter.ToUInt32(tokenBytes);
        if (token == 0) token = 0x50434C36;

        return new Ipv6HostSession(udp, publicIpv6, (ushort)localEp.Port, token, initialMcPort, requireOnlineAuth);
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

        var ep = TunnelBridgeHelper.NormalizeEndpoint(new IPEndPoint(payload.Address, payload.Port));
        var entry = GetOrCreatePeerEntry(ep, directNoAck: false);
        _ = SendPunchBurstAsync(entry.Peer, _cts.Token);
        LogWrapper.Info("Link", $"[Host] 已添加 好友 #{entry.VirtualId}，正在连发打洞探测包");
        StateChanged?.Invoke();
        message = $"已添加 好友 #{entry.VirtualId}，正在双向打洞…";
        return true;
    }

    public IReadOnlyList<DirectPeerSnapshot> GetPeers()
    {
        return _peers.Select(kv => new DirectPeerSnapshot(
                kv.Key,
                kv.Value.Peer.IsReachable ? DirectPeerState.Connected : DirectPeerState.Punching,
                kv.Value.Peer.RttMs,
                kv.Value.DirectNoAck,
                kv.Value.AddedUtc,
                kv.Value.VirtualId))
            .OrderBy(p => p.VirtualId)
            .ToList();
    }

    private PeerEntry GetOrCreatePeerEntry(IPEndPoint ep, bool directNoAck)
    {
        ep = TunnelBridgeHelper.NormalizeEndpoint(ep);
        if (_peers.TryGetValue(ep, out var existing))
            return existing;

        lock (_peerCreateLock)
        {
            if (_peers.TryGetValue(ep, out existing))
                return existing;

            var virtualId = Interlocked.Increment(ref _nextPeerId);
            var created = new PeerEntry
            {
                VirtualId = virtualId,
                Peer = new UdpReliablePeer(
                    _token,
                    data => SendToAsync(ep, virtualId, data),
                    localRequiresOnlineAuth: _requireOnlineAuth),
                DirectNoAck = directNoAck,
                AddedUtc = DateTimeOffset.UtcNow
            };
            created.Peer.OnStreamAccepted += stream => _ = BridgeStreamToLocalMcAsync(stream, _cts.Token);
            created.Peer.OnConnectedOrHeartbeat += rtt =>
            {
                if (Interlocked.Exchange(ref created.ConnectedLogged, 1) == 0)
                    LogWrapper.Info("Link", $"[Host] 与 好友 #{virtualId} 打洞连通，延迟 {rtt} ms{(directNoAck ? "（免回执直连）" : "")}");
                StateChanged?.Invoke();
            };
            _peers[ep] = created;
            return created;
        }
    }

    private async Task SendPunchBurstAsync(UdpReliablePeer peer, CancellationToken ct)
    {
        for (var i = 0; i < 3 && !ct.IsCancellationRequested && !peer.IsReachable; i++)
        {
            try
            {
                await peer.SendPingAsync().ConfigureAwait(false);
                if (i < 2)
                    await Task.Delay(40, ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
        }
    }

    private async ValueTask SendToAsync(IPEndPoint ep, int virtualId, ReadOnlyMemory<byte> data)
    {
        try
        {
            await _udp.SendToAsync(data, SocketFlags.None, ep, _cts.Token).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            if (_loggedErrors.TryAdd(ex.ErrorCode, 1))
                LogWrapper.Warn("Link", $"[Host] UDP 发送到 好友 #{virtualId} 出现 Socket 错误 ({ex.SocketErrorCode}/{ex.ErrorCode}): {ex.Message}");
        }
        catch
        {
            // 忽略取消等单次发送失败
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
                if (res.RemoteEndPoint is not IPEndPoint rawRemoteEp)
                    continue;

                var remoteEp = TunnelBridgeHelper.NormalizeEndpoint(rawRemoteEp);
                var packet = buffer.AsSpan(0, res.ReceivedBytes);
                if (packet.Length < 7)
                    continue;
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet[0..2]) != UdpReliablePeer.PacketMagic)
                    continue;
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet[2..6]) != _token)
                    continue;

                // 如果房主本地无防火墙拦截，收到携带正确房间 Token 的房客包时直接接纳，免去复制回执码！
                var isNew = !_peers.ContainsKey(remoteEp);
                var entry = GetOrCreatePeerEntry(remoteEp, directNoAck: true);
                if (isNew)
                    LogWrapper.Info("Link", $"[Host] 收到 好友 #{entry.VirtualId} 的直连数据包");
                entry.Peer.HandleIncomingPacket(packet);
                if (isNew)
                    StateChanged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (_loggedErrors.TryAdd(ex.ErrorCode, 1))
                    LogWrapper.Warn("Link", $"[Host] UDP 接收出现 Socket 错误 ({ex.SocketErrorCode}/{ex.ErrorCode}): {ex.Message}");
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
                var hasPunching = false;
                foreach (var kv in _peers)
                {
                    if (!kv.Value.Peer.IsReachable)
                        hasPunching = true;
                    await kv.Value.Peer.SendPingAsync().ConfigureAwait(false);
                }

                await Task.Delay(hasPunching ? 300 : 1200, ct).ConfigureAwait(false);
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

        using var tcp = await TunnelBridgeHelper.ConnectLoopbackDualStackAsync(port, ct).ConfigureAwait(false);
        if (tcp is null)
        {
            if (Interlocked.Exchange(ref _mcConnectWarned, 1) == 0)
                LogWrapper.Warn("Link", $"[Host] 无法连接本地 MC 端口 {port}，请确认游戏内已点『对局域网开放』且端口一致");
            await tunnelStream.CloseAsync().ConfigureAwait(false);
            return;
        }

        await using var netStream = tcp.GetStream();
        await TunnelBridgeHelper.PumpBidirectionalAsync(tcp, netStream, tunnelStream, ct).ConfigureAwait(false);
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
    private readonly ConcurrentDictionary<int, byte> _loggedErrors = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _recvTask;
    private readonly Task _probeTask;
    private readonly Task _acceptTask;
    private readonly Task _lanAnnounceTask;

    private int _connectedLogged;

    public string AckCode { get; }
    public IPEndPoint HostEndpoint => _hostEndpoint;
    public ushort LocalProxyPort { get; }
    public bool IsConnected => _peer.IsReachable;
    public int RttMs => _peer.RttMs;
    public bool HostRequiresOnlineAuth => _peer.RemoteRequiresOnlineAuth;

    public event Action? StateChanged;

    private Ipv6GuestSession(
        Socket udp,
        IPAddress localPublicIpv6,
        ushort localUdpPort,
        InviteCodePayload hostInvite,
        TcpListener localProxy)
    {
        _udp = udp;
        _hostEndpoint = TunnelBridgeHelper.NormalizeEndpoint(new IPEndPoint(hostInvite.Address, hostInvite.Port));
        _localProxy = localProxy;
        LocalProxyPort = (ushort)((IPEndPoint)localProxy.LocalEndpoint).Port;

        AckCode = InviteCodeCodec.Encode(new InviteCodePayload(
            InviteKind.GuestAck,
            localPublicIpv6,
            localUdpPort,
            hostInvite.Token));

        _peer = new UdpReliablePeer(hostInvite.Token, SendToHostAsync);
        _peer.OnConnectedOrHeartbeat += rtt =>
        {
            if (Interlocked.Exchange(ref _connectedLogged, 1) == 0)
            {
                LogWrapper.Info("Link",
                    $"[Guest] 已与 房主 #1 连通，延迟 {rtt} ms，本地代理 127.0.0.1:{LocalProxyPort}，正版验证={(_peer.RemoteRequiresOnlineAuth ? "开启" : "关闭")}");
            }
            StateChanged?.Invoke();
        };

        var actualLocal = _udp.LocalEndPoint as IPEndPoint;
        LogWrapper.Info("Link",
            $"[Guest] 客户端已启动：本地端口 {actualLocal?.Port ?? localUdpPort}，目标 房主 #1，本地 MC 代理 127.0.0.1:{LocalProxyPort}");

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

        var udp = TunnelBridgeHelper.BindUdpSocket(localPublicIpv6, "[Guest]");
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
        catch (SocketException ex)
        {
            if (_loggedErrors.TryAdd(ex.ErrorCode, 1))
                LogWrapper.Warn("Link", $"[Guest] UDP 发送到 房主 #1 出现 Socket 错误 ({ex.SocketErrorCode}/{ex.ErrorCode}): {ex.Message}");
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
            catch (SocketException ex)
            {
                if (_loggedErrors.TryAdd(ex.ErrorCode, 1))
                    LogWrapper.Warn("Link", $"[Guest] UDP 接收出现 Socket 错误 ({ex.SocketErrorCode}/{ex.ErrorCode}): {ex.Message}");
            }
            catch
            {
            }
        }
    }

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        for (var i = 0; i < 3 && !ct.IsCancellationRequested && !IsConnected; i++)
        {
            try
            {
                await _peer.SendPingAsync().ConfigureAwait(false);
                if (i < 2)
                    await Task.Delay(40, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                break;
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IsConnected ? 1200 : 300, ct).ConfigureAwait(false);
                await _peer.SendPingAsync().ConfigureAwait(false);
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
                tcp.NoDelay = true;
                _ = Task.Run(async () =>
                {
                    using (tcp)
                    await using (var netStream = tcp.GetStream())
                    {
                        var tunnel = _peer.OpenStream();
                        await TunnelBridgeHelper.PumpBidirectionalAsync(tcp, netStream, tunnel, ct).ConfigureAwait(false);
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
    private const int SioUdpConnReset = -1744830452;
    private const int SioUdpNetReset = -1744830445;

    public static IPEndPoint NormalizeEndpoint(IPEndPoint ep)
    {
        if (ep.AddressFamily == AddressFamily.InterNetworkV6 && ep.Address.ScopeId != 0)
        {
            Span<byte> bytes = stackalloc byte[16];
            ep.Address.TryWriteBytes(bytes, out _);
            return new IPEndPoint(new IPAddress(bytes), ep.Port);
        }
        return ep;
    }

    public static Socket BindUdpSocket(IPAddress preferredIpv6, string roleTag = "")
    {
        var udp = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
        {
            DualMode = false
        };
        DisableUdpResetErrors(udp);
        try
        {
            udp.Bind(new IPEndPoint(preferredIpv6, 0));
            return udp;
        }
        catch (SocketException ex)
        {
            udp.Dispose();
            LogWrapper.Warn("Link", $"{roleTag} 绑定首选 IPv6 [{preferredIpv6}] 失败 ({ex.SocketErrorCode})，回退到 [::]: {ex.Message}");
            var fallback = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            {
                DualMode = false
            };
            DisableUdpResetErrors(fallback);
            fallback.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            return fallback;
        }
    }

    public static async Task<TcpClient?> ConnectLoopbackDualStackAsync(int port, CancellationToken ct)
    {
        var v4 = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await v4.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
            return v4;
        }
        catch
        {
            v4.Dispose();
        }

        if (Socket.OSSupportsIPv6)
        {
            var v6 = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
            try
            {
                await v6.ConnectAsync(IPAddress.IPv6Loopback, port, ct).ConfigureAwait(false);
                return v6;
            }
            catch
            {
                v6.Dispose();
            }
        }

        return null;
    }

    private static void DisableUdpResetErrors(Socket udp)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var off = new byte[] { 0, 0, 0, 0 };
            udp.IOControl(SioUdpConnReset, off, null);
            udp.IOControl(SioUdpNetReset, off, null);
        }
        catch
        {
            // 部分精简系统或沙箱不支持时安全忽略
        }
    }

    public static async Task PumpBidirectionalAsync(
        TcpClient tcp,
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
            finally
            {
                try { tcp.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
        }, ct);

        await Task.WhenAll(up, down).ConfigureAwait(false);
    }
}
