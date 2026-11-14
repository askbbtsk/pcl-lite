using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Link.Ipv6Direct;

namespace PCL.Core.Test;

[TestClass]
public class Ipv6DirectTest
{
    [TestMethod]
    public void InviteCode_RoundTrip_HostAndAck()
    {
        var ip = IPAddress.Parse("240e:3b4:1234:5678::abcd");
        var hostPayload = new InviteCodePayload(InviteKind.HostInvite, ip, 49152, 0x1234ABCD, TunnelSecret.Create());
        var hostCode = InviteCodeCodec.Encode(hostPayload);

        Assert.IsTrue(hostCode.StartsWith("BSK-", StringComparison.Ordinal));
        Assert.IsTrue(hostCode.StartsWith(InviteCodeCodec.HostPrefix, StringComparison.Ordinal));
        Assert.IsTrue(InviteCodeCodec.TryDecode(hostCode, out var decodedHost, out var err), err);
        Assert.AreEqual(InviteKind.HostInvite, decodedHost.Kind);
        Assert.AreEqual(ip, decodedHost.Address);
        Assert.AreEqual((ushort)49152, decodedHost.Port);
        Assert.AreEqual(0x1234ABCDu, decodedHost.Token);

        var ackPayload = InviteCodeCodec.CreateGuestAck(decodedHost, IPAddress.Parse("2408:8207::1"), 55555, RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize));
        var ackCode = InviteCodeCodec.Encode(ackPayload);
        Assert.IsTrue(ackCode.StartsWith("ACK-", StringComparison.Ordinal));
        Assert.IsTrue(ackCode.StartsWith(InviteCodeCodec.GuestAckPrefix, StringComparison.Ordinal));
        Assert.IsTrue(InviteCodeCodec.TryDecode("  " + ackCode + "\n", out var decodedAck, out err), err);
        Assert.AreEqual(InviteKind.GuestAck, decodedAck.Kind);
        Assert.AreEqual(ackPayload.Address, decodedAck.Address);
        Assert.AreEqual(ackPayload.Port, decodedAck.Port);
    }

    [TestMethod]
    public void InviteCode_DetectsTampering()
    {
        var ip = IPAddress.Parse("2409:8a00::9");
        var code = InviteCodeCodec.Encode(new InviteCodePayload(InviteKind.HostInvite, ip, 25565, 99, TunnelSecret.Create()));
        var tamperedLast = code[..^1] + (code[^1] == '1' ? '2' : '1');
        var tamperedMid = code[..8] + (code[8] == 'A' ? 'B' : 'A') + code[9..];
        var truncated = code[..^1];
        Assert.IsFalse(InviteCodeCodec.TryDecode(tamperedLast, out _, out _));
        Assert.IsFalse(InviteCodeCodec.TryDecode(tamperedMid, out _, out _));
        Assert.IsFalse(InviteCodeCodec.TryDecode(truncated, out _, out _));
        Assert.IsFalse(InviteCodeCodec.TryDecode("BAD-123", out _, out _));
    }

    [TestMethod]
    public void Ipv6AddressDetector_FiltersNonPublicAddresses()
    {
        Assert.IsTrue(Ipv6AddressDetector.IsRoutableGlobalIpv6(IPAddress.Parse("240e:3b4::1")));
        Assert.IsFalse(Ipv6AddressDetector.IsRoutableGlobalIpv6(IPAddress.Parse("fe80::1")));
        Assert.IsFalse(Ipv6AddressDetector.IsRoutableGlobalIpv6(IPAddress.Parse("fd12:3456::1")));
        Assert.IsFalse(Ipv6AddressDetector.IsRoutableGlobalIpv6(IPAddress.Parse("::1")));
        Assert.IsFalse(Ipv6AddressDetector.IsRoutableGlobalIpv6(IPAddress.Parse("2002:c000:0204::1")));
        Assert.IsFalse(Ipv6AddressDetector.IsRoutableGlobalIpv6(IPAddress.Parse("127.0.0.1")));
    }

    [TestMethod]
    public void LanBroadcastHelper_ParsesAnnouncement()
    {
        Assert.IsTrue(LanBroadcastHelper.TryParseAnnouncement("[MOTD]Steve 的世界[/MOTD][AD]51234[/AD]", out var motd, out var port));
        Assert.AreEqual("Steve 的世界", motd);
        Assert.AreEqual((ushort)51234, port);
        Assert.IsFalse(LanBroadcastHelper.TryParseAnnouncement("[MOTD]bad[/MOTD][AD]0[/AD]", out _, out _));
    }

    [TestMethod]
    public async Task UdpReliablePeer_TransfersLargePayloadWithPacketLoss()
    {
        const uint token = 0xCAFEBABE;
        var secret = TunnelSecret.Create();
        var peerId = RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize);
        UdpReliablePeer? peerA = null;
        UdpReliablePeer? peerB = null;
        var dropCounter = 0;

        peerA = new UdpReliablePeer(token, secret, peerId, isHost: false, pkt =>
        {
            var copy = pkt.ToArray();
            if (Interlocked.Increment(ref dropCounter) % 7 == 0)
                return ValueTask.CompletedTask; // 模拟 ~14% 丢包
            _ = Task.Run(async () =>
            {
                await Task.Delay(5);
                peerB!.HandleIncomingPacket(copy);
            });
            return ValueTask.CompletedTask;
        });

        peerB = new UdpReliablePeer(token, secret, peerId, isHost: true, pkt =>
        {
            var copy = pkt.ToArray();
            _ = Task.Run(async () =>
            {
                await Task.Delay(5);
                peerA.HandleIncomingPacket(copy);
            });
            return ValueTask.CompletedTask;
        });

        await using (peerA)
        await using (peerB)
        {
            var acceptedTcs = new TaskCompletionSource<UdpReliablePeer.ReliableStream>();
            peerB.OnStreamAccepted += s => acceptedTcs.TrySetResult(s);

            await peerA.SendPingAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!peerA.IsReachable && !cts.IsCancellationRequested)
                await Task.Delay(20, cts.Token);
            Assert.IsTrue(peerA.IsReachable);

            var payload = RandomNumberGenerator.GetBytes(32 * 1024);
            var streamA = peerA.OpenStream();
            var streamB = await acceptedTcs.Task.WaitAsync(cts.Token);

            var readTask = Task.Run(async () =>
            {
                using var ms = new MemoryStream();
                var buf = new byte[2048];
                while (true)
                {
                    var n = await streamB.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }, cts.Token);

            await streamA.WriteAsync(payload, cts.Token);
            await streamA.CloseAsync();

            var received = await readTask.WaitAsync(cts.Token);
            CollectionAssert.AreEqual(payload, received);
        }
    }

    [TestMethod]
    public async Task UdpReliablePeer_PongPropagatesOnlineAuthAndNoSemaphoreOverflow()
    {
        const uint token = 0x13572468;
        var secret = TunnelSecret.Create();
        var peerId = RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize);
        var semaphoreOverflowSeen = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onFirstChance = (_, args) =>
        {
            if (args.Exception is SemaphoreFullException)
                Interlocked.Increment(ref semaphoreOverflowSeen);
        };
        AppDomain.CurrentDomain.FirstChanceException += onFirstChance;

        try
        {
            UdpReliablePeer? hostPeer = null;
            UdpReliablePeer? guestPeer = null;

            hostPeer = new UdpReliablePeer(token, secret, peerId, isHost: true, pkt =>
            {
                var copy = pkt.ToArray();
                _ = Task.Run(() => guestPeer!.HandleIncomingPacket(copy));
                return ValueTask.CompletedTask;
            }, localRequiresOnlineAuth: true);

            guestPeer = new UdpReliablePeer(token, secret, peerId, isHost: false, pkt =>
            {
                var copy = pkt.ToArray();
                _ = Task.Run(() => hostPeer.HandleIncomingPacket(copy));
                return ValueTask.CompletedTask;
            });

            await using (hostPeer)
            await using (guestPeer)
            {
                var acceptedTcs = new TaskCompletionSource<UdpReliablePeer.ReliableStream>();
                hostPeer.OnStreamAccepted += s => acceptedTcs.TrySetResult(s);

                await guestPeer.SendPingAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!guestPeer.IsReachable && !cts.IsCancellationRequested)
                    await Task.Delay(15, cts.Token);

                Assert.IsTrue(guestPeer.IsReachable);
                Assert.IsTrue(guestPeer.RemoteRequiresOnlineAuth);

                var streamGuest = guestPeer.OpenStream();
                var streamHost = await acceptedTcs.Task.WaitAsync(cts.Token);

                var data = RandomNumberGenerator.GetBytes(8 * 1024);
                var readTask = Task.Run(async () =>
                {
                    using var ms = new MemoryStream();
                    var buf = new byte[1024];
                    while (true)
                    {
                        var n = await streamHost.ReadAsync(buf, cts.Token);
                        if (n == 0) break;
                        ms.Write(buf, 0, n);
                    }
                    return ms.ToArray();
                }, cts.Token);

                await streamGuest.WriteAsync(data, cts.Token);
                await streamGuest.CloseAsync();
                CollectionAssert.AreEqual(data, await readTask.WaitAsync(cts.Token));

                // 等待 Close 包的 ACK 完成处理，确认不会再出现 SemaphoreFullException
                await Task.Delay(150, cts.Token);
                Assert.AreEqual(0, Volatile.Read(ref semaphoreOverflowSeen));
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= onFirstChance;
        }
    }

    [TestMethod]
    public async Task HostAndGuestSession_BridgesMultipleTcpConnectionsOverUdp()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var mockMcServer = new TcpListener(IPAddress.Loopback, 0);
        mockMcServer.Start();
        var mcPort = ((IPEndPoint)mockMcServer.LocalEndpoint).Port;

        var serverAccepted = 0;
        var serverBytesEchoed = 0L;
        var serverTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await mockMcServer.AcceptTcpClientAsync(cts.Token);
                    Interlocked.Increment(ref serverAccepted);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    await using (var s = client.GetStream())
                    {
                        var buf = new byte[4096];
                        while (true)
                        {
                            var n = await s.ReadAsync(buf, cts.Token);
                            if (n == 0) break;
                            await s.WriteAsync(buf.AsMemory(0, n), cts.Token);
                            Interlocked.Add(ref serverBytesEchoed, n);
                        }
                    }
                }, cts.Token);
            }
        }, cts.Token);

        await using var host = Ipv6HostSession.Start(IPAddress.IPv6Loopback, mcPort, requireOnlineAuth: true);
        Assert.IsTrue(InviteCodeCodec.TryDecode(host.InviteCode, out var invite, out var err), err);
        await using var guest = Ipv6GuestSession.Start(IPAddress.IPv6Loopback, invite);

        Assert.IsTrue(host.TryAcceptGuestAck(guest.AckCode, out var ackMsg), ackMsg);
        StringAssert.Contains(ackMsg, "好友 #1");
        Assert.IsFalse(ackMsg.Contains("::1", StringComparison.Ordinal));
        var initialPeers = host.GetPeers();
        Assert.AreEqual(1, initialPeers.Count);
        Assert.AreEqual(1, initialPeers[0].VirtualId);
        while (!guest.IsConnected && !cts.IsCancellationRequested)
            await Task.Delay(25, cts.Token);
        Assert.IsTrue(guest.IsConnected);
        Assert.IsTrue(guest.HostRequiresOnlineAuth);

        var sizes = new[] { 128, 16 * 1024, 64 * 1024 };
        foreach (var size in sizes)
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, guest.LocalProxyPort, cts.Token);
            await using var stream = client.GetStream();

            var payload = RandomNumberGenerator.GetBytes(size);
            await stream.WriteAsync(payload, cts.Token);

            using var received = new MemoryStream(size);
            var buf = new byte[4096];
            while (received.Length < payload.Length)
            {
                var n = await stream.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                received.Write(buf, 0, n);
            }
            var receivedBytes = received.ToArray();
            Assert.AreEqual(payload.Length, receivedBytes.Length,
                $"Size {size}: expected {payload.Length} bytes, got {receivedBytes.Length} bytes. hostEp={host.PublicAddress}:{host.UdpPort}, peers={host.GetPeers().Count}, serverAccepted={serverAccepted}, serverBytesEchoed={serverBytesEchoed}");
            CollectionAssert.AreEqual(payload, receivedBytes);
        }

        Assert.IsTrue(serverAccepted >= sizes.Length);
        Assert.IsTrue(Interlocked.Read(ref serverBytesEchoed) >= 128 + 16 * 1024 + 64 * 1024);

        cts.Cancel();
        mockMcServer.Stop();
        try { await serverTask; } catch { }
    }

    [TestMethod]
    public async Task GuestSession_AssignsRandomUnoccupiedPorts()
    {
        using var occupiedTcp = new TcpListener(IPAddress.Loopback, 0);
        occupiedTcp.Start();
        var occupiedPort = ((IPEndPoint)occupiedTcp.LocalEndpoint).Port;

        await using var host = Ipv6HostSession.Start(IPAddress.IPv6Loopback, 25565, requireOnlineAuth: false);
        Assert.IsTrue(InviteCodeCodec.TryDecode(host.InviteCode, out var invite, out var err), err);

        await using var guest1 = Ipv6GuestSession.Start(IPAddress.IPv6Loopback, invite);
        await using var guest2 = Ipv6GuestSession.Start(IPAddress.IPv6Loopback, invite);

        Assert.IsTrue(guest1.LocalProxyPort is >= 20000 and < 60000, $"Unexpected guest1 LocalProxyPort: {guest1.LocalProxyPort}");
        Assert.IsTrue(guest2.LocalProxyPort is >= 20000 and < 60000, $"Unexpected guest2 LocalProxyPort: {guest2.LocalProxyPort}");
        Assert.AreNotEqual(occupiedPort, guest1.LocalProxyPort);
        Assert.AreNotEqual(occupiedPort, guest2.LocalProxyPort);
        Assert.AreNotEqual(guest1.LocalProxyPort, guest2.LocalProxyPort);

        Assert.IsTrue(InviteCodeCodec.TryDecode(guest1.AckCode, out var ack1, out err), err);
        Assert.IsTrue(InviteCodeCodec.TryDecode(guest2.AckCode, out var ack2, out err), err);
        Assert.IsTrue(ack1.Port is >= 20000 and < 60000);
        Assert.IsTrue(ack2.Port is >= 20000 and < 60000);
        Assert.AreNotEqual(ack1.Port, ack2.Port);
    }
}
