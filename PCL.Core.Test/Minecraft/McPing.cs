using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.McPing;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public class McPingTest
{
    [TestMethod]
    public async Task PingTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var serverTask = _RespondToStatusRequestAsync(listener, TestContext.CancellationToken);
            using var ping = new McPingService("pcl-test.local", endpoint, timeout: 3000);

            var result = await ping.PingAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await serverTask.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsNotNull(result);
            Assert.AreEqual("PCL loopback test", result.Description);
            Assert.AreEqual("PCL Test Server", result.Version.Name);
            Assert.AreEqual(772, result.Version.Protocol);
            Assert.IsTrue(result.Latency >= 0, $"Latency must be non-negative, actual: {result.Latency}");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task _RespondToStatusRequestAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var handshake = await _ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(handshake.Length > 0 && handshake[0] == 0, "Expected a handshake packet.");

        var statusRequest = await _ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false);
        CollectionAssert.AreEqual(new byte[] { 0 }, statusRequest, "Expected a status request packet.");

        var pingRequest = await _ReadPacketAsync(stream, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(9, pingRequest.Length, "Expected a ping packet with an eight-byte payload.");
        Assert.AreEqual((byte)1, pingRequest[0], "Expected ping packet ID 1.");

        var statusJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = new { name = "PCL Test Server", protocol = 772 },
            players = new { max = 20, online = 1, sample = Array.Empty<object>() },
            description = "PCL loopback test"
        });
        var statusPacket = _EncodeVarInt(0)
            .Concat(_EncodeVarInt(statusJson.Length))
            .Concat(statusJson)
            .ToArray();

        await stream.WriteAsync(_Frame(statusPacket), cancellationToken).ConfigureAwait(false);
        // Echo the timestamp from the client's ping request as required by the status protocol.
        await stream.WriteAsync(_Frame(pingRequest), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> _ReadPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = await _ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);
        var packet = new byte[length];
        await stream.ReadExactlyAsync(packet, cancellationToken).ConfigureAwait(false);
        return packet;
    }

    private static async Task<int> _ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = 0;
        var shift = 0;
        var singleByte = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(singleByte, cancellationToken).ConfigureAwait(false);
            result |= (singleByte[0] & 0x7f) << shift;
            if ((singleByte[0] & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 35) throw new InvalidDataException("Minecraft VarInt is too long.");
        }
    }

    private static byte[] _EncodeVarInt(int value)
    {
        var bytes = new byte[5];
        var length = 0;
        do
        {
            var current = (byte)(value & 0x7f);
            value >>>= 7;
            if (value != 0) current |= 0x80;
            bytes[length++] = current;
        } while (value != 0);

        return bytes[..length];
    }

    private static byte[] _Frame(byte[] packet)
    {
        return _EncodeVarInt(packet.Length).Concat(packet).ToArray();
    }

    public TestContext TestContext { get; set; } = null!;
}
