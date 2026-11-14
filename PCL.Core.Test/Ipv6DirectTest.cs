using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net;
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
        var hostPayload = new InviteCodePayload(InviteKind.HostInvite, ip, 49152, 0x1234ABCD);
        var hostCode = InviteCodeCodec.Encode(hostPayload);

        Assert.IsTrue(hostCode.StartsWith(InviteCodeCodec.HostPrefix, StringComparison.Ordinal));
        Assert.IsTrue(InviteCodeCodec.TryDecode(hostCode, out var decodedHost, out var err), err);
        Assert.AreEqual(InviteKind.HostInvite, decodedHost.Kind);
        Assert.AreEqual(ip, decodedHost.Address);
        Assert.AreEqual((ushort)49152, decodedHost.Port);
        Assert.AreEqual(0x1234ABCDu, decodedHost.Token);

        var ackPayload = new InviteCodePayload(InviteKind.GuestAck, IPAddress.Parse("2408:8207::1"), 55555, 0x1234ABCD);
        var ackCode = InviteCodeCodec.Encode(ackPayload);
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
        var code = InviteCodeCodec.Encode(new InviteCodePayload(InviteKind.HostInvite, ip, 25565, 99));
        var tampered = code[..^1] + (code[^1] == '1' ? '2' : '1');
        Assert.IsFalse(InviteCodeCodec.TryDecode(tampered, out _, out _));
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
        UdpReliablePeer? peerA = null;
        UdpReliablePeer? peerB = null;
        var dropCounter = 0;

        peerA = new UdpReliablePeer(token, pkt =>
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

        peerB = new UdpReliablePeer(token, pkt =>
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
}
