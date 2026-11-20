using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Ipv6Direct;

namespace PCL.Core.Test;

[TestClass]
public sealed class Ipv6ReliableWindowRegressionTests
{
    [TestMethod]
    public async Task SelectiveAcksDoNotAdvanceTheCumulativeSendWindow()
    {
        await using var peer = new UdpReliablePeer(1, TunnelSecret.Create(),
            RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize), isHost: true, _ => ValueTask.CompletedTask);
        var stream = peer.OpenStream();
        await stream.WriteAsync(new byte[64 * UdpReliablePeer.MaxChunkSize], CancellationToken.None);
        stream.HandleAck(0, ulong.MaxValue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = stream.WriteAsync(new byte[1], timeout.Token);
        Assert.IsFalse(pending.IsCompleted, "A missing first packet must continue to hold the window.");
        stream.HandleAck(64, 0);
        await pending.WaitAsync(timeout.Token);
        stream.HandleAck(64, 0); // Repeated cumulative ACKs cannot release slots twice.
    }

    [TestMethod]
    public async Task AStalledStreamTimesOutAndWakesBlockedWriters()
    {
        await using var peer = new UdpReliablePeer(1, TunnelSecret.Create(),
            RandomNumberGenerator.GetBytes(TunnelAuthentication.PeerIdSize), isHost: true, _ => ValueTask.CompletedTask);
        var stream = peer.OpenStream();
        await stream.WriteAsync(new byte[64 * UdpReliablePeer.MaxChunkSize], CancellationToken.None);
        var pending = stream.WriteAsync(new byte[1], CancellationToken.None);
        await stream.PumpRetransmitsAsync(Environment.TickCount64 + 60001);
        Assert.IsTrue(stream.IsFullyClosed);
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
