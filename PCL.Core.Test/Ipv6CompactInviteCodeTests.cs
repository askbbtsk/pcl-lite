using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Ipv6Direct;

namespace PCL.Core.Test;

[TestClass]
public class Ipv6CompactInviteCodeTests
{
    [TestMethod]
    public void AlphabetContainsOnlyUniqueFirstLevelGb2312Hanzi()
    {
        Assert.AreEqual(3755, CommonHanziCode.Alphabet.Length);
        Assert.AreEqual(3755, CommonHanziCode.Alphabet.Distinct().Count());
        Assert.IsTrue(CommonHanziCode.Alphabet.All(ch => ch is >= '\u4e00' and <= '\u9fff'));
    }

    [TestMethod]
    public void FixedWidthEncodingCoversZeroMaximumAndRandomPayloads()
    {
        foreach (var kind in new[] { InviteKind.HostInvite, InviteKind.GuestAck })
        {
            for (var i = 0; i < 258; i++)
            {
                var input = i == 0 ? new byte[38] : i == 1 ? Enumerable.Repeat((byte)255, 38).ToArray() : RandomNumberGenerator.GetBytes(38);
                var code = CommonHanziCode.Encode(input, kind);
                Assert.AreEqual(26, code.Length);
                Assert.IsTrue(CommonHanziCode.TryDecode(code, kind, out var decoded));
                CollectionAssert.AreEqual(input, decoded);
            }
        }
        Assert.IsFalse(CommonHanziCode.TryDecode(new string(CommonHanziCode.Alphabet[^1], 26), InviteKind.HostInvite, out _), "Reject unused values beyond the 308-bit envelope.");
    }

    [TestMethod]
    public void BothCodesAreAlwaysThirtyCharactersAndAuthenticateAfterWhitespaceCopy()
    {
        for (var i = 0; i < 128; i++)
        {
            var host = new InviteCodePayload(InviteKind.HostInvite, new IPAddress(RandomNumberGenerator.GetBytes(16)),
                i % 2 == 0 ? (ushort)1 : ushort.MaxValue, i % 2 == 0 ? 0 : uint.MaxValue, TunnelSecret.Create());
            var hostCode = InviteCodeCodec.Encode(host);
            AssertCode(hostCode, "BSK-");
            Assert.IsTrue(InviteCodeCodec.TryDecode(hostCode, out var decodedHost, out var error), error);
            Assert.AreEqual(host.Address, decodedHost.Address);
            Assert.AreEqual(host.Port, decodedHost.Port);
            Assert.AreEqual(host.Token, decodedHost.Token);
            var ack = InviteCodeCodec.CreateGuestAck(decodedHost, IPAddress.IPv6Loopback, 65535, RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize));
            var ackCode = InviteCodeCodec.Encode(ack);
            AssertCode(ackCode, "ACK-");
            var copied = "\t" + string.Join("\r\n　", ackCode.Select(ch => ch.ToString())).Replace("A", "a") + " \n";
            Assert.IsTrue(InviteCodeCodec.TryDecode(copied, out var decodedAck, out error), error);
            Assert.IsNull(decodedAck.Secret);
            Assert.AreEqual(0u, decodedAck.Token, "ACK uses the host's expected room token instead of transmitting it.");
            Assert.IsTrue(InviteCodeCodec.VerifyGuestAck(decodedAck, host.Token, host.Secret!));
            Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(decodedAck, host.Token ^ 1, host.Secret!));
            Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(decodedAck, host.Token, TunnelSecret.Create()));
        }
    }

    [TestMethod]
    public void InvalidInputAndLegacyCodesAreRejectedWithoutThrowing()
    {
        var host = new InviteCodePayload(InviteKind.HostInvite, IPAddress.IPv6Loopback, 25565, 42, TunnelSecret.Create());
        var code = InviteCodeCodec.Encode(host);
        foreach (var invalid in new string?[] { null, "", " ", code[..^1], code + code[^1], "BSK-" + new string('1', 26),
                     code[..^1] + "😀", code[..^1] + "\ue000", "BAD-" + code[4..], "BSK-" + new string('1', 80),
                     new string(' ', 1025), "BSK-" + new string(CommonHanziCode.Alphabet[^1], 26) })
            Assert.IsFalse(InviteCodeCodec.TryDecode(invalid, out _, out _));
        var raw = new byte[38];
        Assert.IsFalse(InviteCodeCodec.TryDecode("BSK-" + CommonHanziCode.Encode(raw, InviteKind.HostInvite), out _, out _), "Port zero must be rejected even with a matching copy checksum.");
    }

    [TestMethod]
    public void AckProofMustHaveExactLengthAndBindEveryCredentialByte()
    {
        var host = new InviteCodePayload(InviteKind.HostInvite, IPAddress.IPv6Loopback, 25565, 42, TunnelSecret.Create());
        var ack = InviteCodeCodec.CreateGuestAck(host, IPAddress.IPv6Loopback, 25566, RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize));
        foreach (var length in new[] { 0, 1, 11, 13, 32 })
            Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(ack with { Proof = new byte[length] }, host.Token, host.Secret!));
        Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(default, host.Token, host.Secret!));
        for (var i = 0; i < ack.Proof!.Length; i++)
        {
            var proof = (byte[])ack.Proof.Clone();
            proof[i] ^= 1;
            Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(ack with { Proof = proof }, host.Token, host.Secret!));
        }
        for (var i = 0; i < ack.PeerId!.Length; i++)
        {
            var peer = (byte[])ack.PeerId.Clone();
            peer[i] ^= 1;
            var changed = ack with { PeerId = peer };
            Assert.IsTrue(InviteCodeCodec.TryDecode(InviteCodeCodec.Encode(changed), out var reencoded, out var error), error);
            Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(reencoded, host.Token, host.Secret!));
        }
        Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(ack with { Address = IPAddress.Parse("::2") }, host.Token, host.Secret!));
        Assert.IsFalse(InviteCodeCodec.VerifyGuestAck(ack with { Port = 25567 }, host.Token, host.Secret!));
        Assert.ThrowsExactly<ArgumentException>(() => InviteCodeCodec.CreateGuestAck(host, IPAddress.IPv6Loopback, 25566, new byte[7]));
    }

    private static void AssertCode(string code, string prefix)
    {
        Assert.AreEqual(30, code.Length);
        Assert.IsTrue(code.StartsWith(prefix, StringComparison.Ordinal));
        Assert.IsTrue(code[4..].All(CommonHanziCode.Alphabet.Contains));
    }
}
