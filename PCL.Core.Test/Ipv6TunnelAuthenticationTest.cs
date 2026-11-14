using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Ipv6Direct;

namespace PCL.Core.Test;

[TestClass]
public class Ipv6TunnelAuthenticationTest
{
    private const uint Token = 0x11223344;

    private static byte[] Packet(int length = 32)
    {
        var packet = new byte[27 + length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, UdpReliablePeer.PacketMagic);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(2), Token);
        packet[6] = 4; // Data
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(7), 1);
        for (var i = 27; i < packet.Length; i++) packet[i] = (byte)i;
        return packet;
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(UdpReliablePeer.MaxChunkSize)]
    public void PayloadRemainsUnencryptedAndRoundTripsExactly(int length)
    {
        var secret = TunnelSecret.Create();
        var id = RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize);
        using var sender = new TunnelAuthentication(Token, secret, id, false);
        using var receiver = new TunnelAuthentication(Token, secret, id, true);
        var original = Packet(length);
        var wire = sender.Protect(original);
        Assert.IsLessThanOrEqualTo(1280, wire.Length + 40 + 8, "Authenticated datagrams must fit the minimum IPv6 packet size.");
        CollectionAssert.AreEqual(original[27..], wire[(TunnelAuthentication.WireHeaderSize + 21)..^TunnelAuthentication.TagSize], "Minecraft bytes must not be encrypted or rewritten.");
        Assert.AreNotEqual(Token, BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(2)));
        Assert.IsTrue(receiver.TryUnprotect(wire, out var decoded));
        CollectionAssert.AreEqual(original, decoded);
    }

    [TestMethod]
    public void ModifyingAnyHeaderPayloadOrTagByteIsRejectedBeforeStateChanges()
    {
        var secret = TunnelSecret.Create();
        var id = RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize);
        using var sender = new TunnelAuthentication(Token, secret, id, false);
        using var receiver = new TunnelAuthentication(Token, secret, id, true);
        var wire = sender.Protect(Packet());
        for (var i = 0; i < wire.Length; i++)
        {
            var changed = (byte[])wire.Clone();
            changed[i] ^= 0x80;
            Assert.IsFalse(receiver.TryUnprotect(changed, out _), $"Tampered byte {i} was accepted.");
        }
        Assert.IsTrue(receiver.TryUnprotect(wire, out _), "A forged high counter must not poison the replay window.");
    }

    [TestMethod]
    public async Task ConcurrentReplaysAreAcceptedExactlyOnce()
    {
        var secret = TunnelSecret.Create();
        var id = RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize);
        using var sender = new TunnelAuthentication(Token, secret, id, false);
        using var receiver = new TunnelAuthentication(Token, secret, id, true);
        var wire = sender.Protect(Packet());
        var accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
        {
            if (receiver.TryUnprotect(wire, out _)) Interlocked.Increment(ref accepted);
        })));
        Assert.AreEqual(1, accepted);
    }

    [TestMethod]
    public void OutOfOrderAndFreshRetransmissionsWorkButOldDatagramsDoNot()
    {
        var secret = TunnelSecret.Create();
        var id = RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize);
        using var sender = new TunnelAuthentication(Token, secret, id, false);
        using var receiver = new TunnelAuthentication(Token, secret, id, true);
        var first = sender.Protect(Packet());
        var second = sender.Protect(Packet());
        var third = sender.Protect(Packet());
        Assert.IsTrue(receiver.TryUnprotect(third, out _));
        Assert.IsTrue(receiver.TryUnprotect(first, out _));
        Assert.IsTrue(receiver.TryUnprotect(second, out _));
        Assert.IsFalse(receiver.TryUnprotect(second, out _));
        byte[] newest = [];
        for (var i = 0; i < 4097; i++) newest = sender.Protect(Packet());
        Assert.IsTrue(receiver.TryUnprotect(newest, out _));
        Assert.IsFalse(receiver.TryUnprotect(first, out _));
        Assert.IsTrue(receiver.TryUnprotect(sender.Protect(Packet()), out _), "Reliable retransmits get fresh authentication counters.");
    }

    [TestMethod]
    public void RoomPeerDirectionAndProtocolAreBoundToAuthentication()
    {
        var secret = TunnelSecret.Create();
        var id = RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize);
        using var sender = new TunnelAuthentication(Token, secret, id, false);
        using var reflected = new TunnelAuthentication(Token, secret, id, false);
        using var wrongRoom = new TunnelAuthentication(Token, TunnelSecret.Create(), id, true);
        using var wrongToken = new TunnelAuthentication(Token + 1, secret, id, true);
        using var wrongPeer = new TunnelAuthentication(Token, secret, RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize), true);
        var wire = sender.Protect(Packet());
        Assert.IsFalse(reflected.TryUnprotect(wire, out _));
        Assert.IsFalse(wrongRoom.TryUnprotect(wire, out _));
        Assert.IsFalse(wrongToken.TryUnprotect(wire, out _));
        Assert.IsFalse(wrongPeer.TryUnprotect(wire, out _));
        Assert.IsFalse(TunnelAuthentication.TryAuthenticateGuest(wire, Token, TunnelSecret.Create(), out _));
        Assert.IsTrue(TunnelAuthentication.TryAuthenticateGuest(wire, Token, secret, out var authenticatedId));
        CollectionAssert.AreEqual(id, authenticatedId);
        Assert.IsFalse(TunnelAuthentication.TryAuthenticateGuest(Packet(), Token, secret, out _), "Never accept legacy clear-token packets.");
    }

    [TestMethod]
    public void AckIsAuthenticatedEvenWhenAttackerRecomputesCopyChecksums()
    {
        var secret = TunnelSecret.Create();
        var invite = new InviteCodePayload(InviteKind.HostInvite, IPAddress.IPv6Loopback, 25565, Token, secret);
        var ack = InviteCodeCodec.CreateGuestAck(invite, IPAddress.IPv6Loopback, 25566, RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize));
        var code = InviteCodeCodec.Encode(ack);
        Assert.IsTrue(InviteCodeCodec.TryDecode(code, out var decoded, out var error), error);
        Assert.IsNull(decoded.Secret, "ACK must not distribute the room key again.");
        Assert.IsTrue(InviteCodeCodec.VerifyGuestAck(decoded, Token, secret));
        Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(decoded, Token + 1, secret));
        Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(decoded, Token, TunnelSecret.Create()));
        var tampered = ack with { Port = 25567 };
        Assert.IsTrue(InviteCodeCodec.TryDecode(InviteCodeCodec.Encode(tampered), out var changed, out error), error);
        Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(changed, Token, secret));
        Assert.AreEqual("[hide]", secret.ToString());
        Assert.IsFalse(invite.ToString().Contains("11223344"));
    }

    [TestMethod]
    public void MalformedDatagramsNeverThrowOrAllocateAVisiblePeer()
    {
        var secret = TunnelSecret.Create();
        using var receiver = new TunnelAuthentication(Token, secret, new byte[TunnelAuthentication.PeerIdSize], true);
        for (var length = 0; length < 1400; length++)
        {
            var packet = RandomNumberGenerator.GetBytes(length);
            Assert.IsFalse(receiver.TryUnprotect(packet, out _));
            Assert.IsFalse(TunnelAuthentication.TryAuthenticateGuest(packet, Token, secret, out _));
        }
        Assert.IsFalse(InviteCodeCodec.TryDecode("BSK-" + new string('1', 10000), out _, out _));
    }

    [TestMethod]
    public async Task ForgedUdpAndAckDoNotCreateHostPeers()
    {
        await using var host = Ipv6HostSession.Start(IPAddress.IPv6Loopback, 25565);
        Assert.IsTrue(InviteCodeCodec.TryDecode(host.InviteCode, out var invite, out var error), error);
        var wrongInvite = invite with { Secret = TunnelSecret.Create() };
        var ack = InviteCodeCodec.CreateGuestAck(wrongInvite, IPAddress.IPv6Loopback, 25566, new byte[TunnelAuthentication.PeerIdSize]);
        Assert.IsFalse(host.TryAcceptGuestAck(InviteCodeCodec.Encode(ack), out _));
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        using var forged = new TunnelAuthentication(invite.Token, wrongInvite.Secret!, new byte[TunnelAuthentication.PeerIdSize], false);
        await socket.SendToAsync(forged.Protect(Packet()), SocketFlags.None, new IPEndPoint(IPAddress.IPv6Loopback, host.UdpPort));
        await Task.Delay(100);
        Assert.AreEqual(0, host.GetPeers().Count);
    }
}
