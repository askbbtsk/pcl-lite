using System;
using System.Collections.Concurrent;
using System.IO;
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
public class Ipv6MultiGuestTest
{
    [TestMethod]
    public async Task ThreeGuestsRequireIndividualAcksAndTransferIndependently()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        await using var server = new EchoServer();
        await using var host = Ipv6HostSession.Start(IPAddress.IPv6Loopback, server.Port);
        Assert.IsTrue(InviteCodeCodec.TryDecode(host.InviteCode, out var invite, out var error), error);
        await using var guest1 = Ipv6GuestSession.Start(IPAddress.IPv6Loopback, invite);
        await using var guest2 = Ipv6GuestSession.Start(IPAddress.IPv6Loopback, invite);
        await using var guest3 = Ipv6GuestSession.Start(IPAddress.IPv6Loopback, invite);

        // Even authenticated probes must not bypass the host's explicit ACK approval.
        await Task.Delay(180, ct);
        Assert.AreEqual(0, host.GetPeers().Count);
        Assert.IsFalse(guest1.IsConnected || guest2.IsConnected || guest3.IsConnected);
        Assert.IsTrue(host.TryAcceptGuestAck(guest1.AckCode, out var message), message);
        await WaitUntilAsync(() => guest1.IsConnected, ct);
        Assert.IsFalse(guest2.IsConnected || guest3.IsConnected);
        using var client1 = new TcpClient();
        await client1.ConnectAsync(IPAddress.Loopback, guest1.LocalProxyPort, ct);
        await EchoAsync(client1, 8192, ct);

        Assert.IsTrue(host.TryAcceptGuestAck(guest2.AckCode, out message), message);
        Assert.IsTrue(host.TryAcceptGuestAck(guest3.AckCode, out message), message);
        await WaitUntilAsync(() => guest2.IsConnected && guest3.IsConnected, ct);
        foreach (var ack in new[] { guest1.AckCode, guest2.AckCode, guest3.AckCode })
            Assert.IsTrue(host.TryAcceptGuestAck(ack, out message), message);
        var peers = host.GetPeers();
        Assert.AreEqual(3, peers.Count);
        Assert.AreEqual(3, peers.Select(p => p.VirtualId).Distinct().Count());
        Assert.IsTrue(peers.All(p => !p.IsDirectNoAck));

        using var client2 = new TcpClient();
        using var client3 = new TcpClient();
        await client2.ConnectAsync(IPAddress.Loopback, guest2.LocalProxyPort, ct);
        await client3.ConnectAsync(IPAddress.Loopback, guest3.LocalProxyPort, ct);
        await Task.WhenAll(EchoAsync(client1, 65536, ct), EchoAsync(client2, 65536, ct), EchoAsync(client3, 65536, ct));
        // Adding peers did not reset the first friend's existing TCP stream.
        await EchoAsync(client1, 4096, ct);
        Assert.AreEqual(3, server.AcceptedConnections);
    }

    [TestMethod]
    public async Task ConcurrentRepeatedAcksAreIdempotentAndConflictingBindingsAreRejected()
    {
        await using var host = Ipv6HostSession.Start(IPAddress.IPv6Loopback, 25565);
        Assert.IsTrue(InviteCodeCodec.TryDecode(host.InviteCode, out var invite, out var error), error);
        await using var guest = Ipv6GuestSession.Start(IPAddress.IPv6Loopback, invite);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            Assert.IsTrue(host.TryAcceptGuestAck(guest.AckCode, out var message), message);
        })));
        var accepted = host.GetPeers().Single();
        Assert.IsTrue(InviteCodeCodec.TryDecode(guest.AckCode, out var ack, out error), error);
        var otherPort = (ushort)(ack.Port == 60000 ? 60001 : 60000);
        var moved = InviteCodeCodec.CreateGuestAck(invite, ack.Address, otherPort, ack.PeerId!);
        Assert.IsFalse(host.TryAcceptGuestAck(InviteCodeCodec.Encode(moved), out _));
        var hijack = InviteCodeCodec.CreateGuestAck(invite, ack.Address, ack.Port, RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize));
        Assert.IsFalse(host.TryAcceptGuestAck(InviteCodeCodec.Encode(hijack), out _));
        Assert.AreEqual(1, host.GetPeers().Count);
        Assert.AreEqual(accepted.VirtualId, host.GetPeers().Single().VirtualId);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate()) await Task.Delay(15, ct);
    }

    private static async Task EchoAsync(TcpClient client, int size, CancellationToken ct)
    {
        var payload = RandomNumberGenerator.GetBytes(size);
        var stream = client.GetStream();
        await stream.WriteAsync(payload, ct);
        var received = new byte[size];
        await stream.ReadExactlyAsync(received, ct);
        CollectionAssert.AreEqual(payload, received, "Different guests' game bytes must not be mixed or changed.");
    }

    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Task _accept;
        private int _accepted;
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal int AcceptedConnections => Volatile.Read(ref _accepted);

        internal EchoServer()
        {
            _listener.Start();
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _accepted);
                    _clients.Add(HandleAsync(client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, _stop.Token)) != 0)
                        await stream.WriteAsync(buffer.AsMemory(0, count), _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (IOException) when (_stop.IsCancellationRequested) { }
                catch (IOException ex) when (ex.InnerException is SocketException
                    { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted }) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _accept;
            _listener.Stop();
            await Task.WhenAll(_clients);
            _stop.Dispose();
        }
    }
}
