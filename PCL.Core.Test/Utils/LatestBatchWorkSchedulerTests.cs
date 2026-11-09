using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils;

namespace PCL.Core.Test.Utils;

[TestClass]
public sealed class LatestBatchWorkSchedulerTests
{
    [TestMethod]
    public async Task CurrentInstanceReplacesObsoleteQueue_WithoutExceedingTheSharedWorkerLimit()
    {
        var scheduler = new LatestBatchWorkScheduler(2);
        using var started = new CountdownEvent(2);
        var oldExecuted = 0;
        var active = 0;
        var peak = 0;
        var old = scheduler.RunAsync(Enumerable.Range(0, 500).ToArray(), (_, token) =>
        {
            Interlocked.Increment(ref oldExecuted);
            var count = Interlocked.Increment(ref active);
            Assert.IsTrue(count <= 2);
            Interlocked.Exchange(ref peak, Math.Max(peak, count));
            started.Signal();
            try { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            finally { Interlocked.Decrement(ref active); }
        });
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
        var currentExecuted = 0;
        var current = scheduler.RunAsync(Enumerable.Range(0, 20).ToArray(), (_, _) =>
        {
            var count = Interlocked.Increment(ref active);
            Assert.IsTrue(count <= 2);
            Interlocked.Exchange(ref peak, Math.Max(peak, count));
            Interlocked.Increment(ref currentExecuted);
            Interlocked.Decrement(ref active);
        });
        try { await old; Assert.Fail("Old batch should be cancelled."); }
        catch (OperationCanceledException) { }
        await current;
        Assert.AreEqual(2, oldExecuted);
        Assert.AreEqual(20, currentExecuted);
        Assert.IsTrue(peak <= 2);
    }

    [TestMethod]
    public async Task CancelledBatchDoesNotStartItemsAndLaterForegroundBatchCanRun()
    {
        var scheduler = new LatestBatchWorkScheduler(2);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var count = 0;
        try { await scheduler.RunAsync(new[] { 1, 2 }, (_, _) => count++, cancelled.Token); Assert.Fail(); }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0, count);
        await scheduler.RunAsync(new[] { 1, 2, 3 }, (_, _) => Interlocked.Increment(ref count));
        Assert.AreEqual(3, count);
    }

    [TestMethod]
    public async Task StaleQueuedCoordinatorCannotCancelTheAlreadyPromotedInstance()
    {
        var scheduler = new LatestBatchWorkScheduler(1);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var current = scheduler.RunAsync(new[] { 1 }, (_, token) =>
        { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); token.ThrowIfCancellationRequested(); });
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
        await scheduler.RunAsync(new[] { 2 }, (_, _) => Assert.Fail("Stale batch must not execute."), canStart: () => false);
        release.Set();
        await current;
    }

    [TestMethod]
    public async Task AlreadyCancelledRequestDoesNotCancelActiveForegroundWork()
    {
        var scheduler = new LatestBatchWorkScheduler(1);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var current = scheduler.RunAsync(new[] { 1 }, (_, token) =>
        { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); token.ThrowIfCancellationRequested(); });
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await scheduler.RunAsync(new[] { 2 }, (_, _) => Assert.Fail(), cancelled.Token);
                Assert.Fail("Cancelled requests must not begin.");
            }
            catch (OperationCanceledException) { }
        }
        finally { release.Set(); }
        await current;
    }
}
