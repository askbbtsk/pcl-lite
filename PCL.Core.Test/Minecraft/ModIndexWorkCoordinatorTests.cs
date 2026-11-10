using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Resources;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class ModIndexWorkCoordinatorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private static TaskCompletionSource<bool> Signal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Await(Task task) => task.WaitAsync(Deadline);
    private static Task<T> Await<T>(Task<T> task) => task.WaitAsync(Deadline);

    [TestMethod]
    public async Task ForegroundCancelsCooperativeBackgroundAndBackgroundAutomaticallyRetries()
    {
        var coordinator = new ModIndexWorkCoordinator();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = Signal();
        var sawCancellation = Signal();
        var attempts = 0;
        var active = 0;
        var background = coordinator.RunBackgroundAsync(async token =>
        {
            Assert.AreEqual(1, Interlocked.Increment(ref active), "Actual work must remain serialized.");
            try
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    started.TrySetResult(true);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException)
                    {
                        sawCancellation.TrySetResult(true);
                        throw;
                    }
                }
                return 42;
            }
            finally { Interlocked.Decrement(ref active); }
        }, lifetime.Token);
        try
        {
            await Await(started.Task);
            var foreground = coordinator.RunForegroundAsync(token =>
            {
                Assert.IsTrue(sawCancellation.Task.IsCompleted, "Foreground may run only after background yields.");
                Assert.AreEqual(1, Interlocked.Increment(ref active));
                Interlocked.Decrement(ref active);
                return Task.FromResult("foreground result");
            }, lifetime.Token);
            Assert.AreEqual("foreground result", await Await(foreground));
            Assert.AreEqual(42, await Await(background));
            Assert.AreEqual(2, attempts);
            Assert.AreEqual(0, active);
        }
        finally { lifetime.Cancel(); }
    }

    [TestMethod]
    public async Task ForegroundHasPriorityOverQueuedAndRetriedBackgroundWork()
    {
        var coordinator = new ModIndexWorkCoordinator();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var backgroundStarted = Signal();
        var foregroundStarted = Signal();
        var releaseForeground = Signal();
        var order = new ConcurrentQueue<string>();
        var attempts = 0;
        var firstBackground = coordinator.RunBackgroundAsync(async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                backgroundStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
            }
            order.Enqueue("retried background");
            return 1;
        }, lifetime.Token);
        try
        {
            await Await(backgroundStarted.Task);
            var queuedBackground = coordinator.RunBackgroundAsync(_ =>
            {
                order.Enqueue("queued background");
                return Task.FromResult(2);
            }, lifetime.Token);
            var foreground = coordinator.RunForegroundAsync(async token =>
            {
                order.Enqueue("foreground");
                foregroundStarted.TrySetResult(true);
                await releaseForeground.Task.WaitAsync(token);
                return 3;
            }, lifetime.Token);
            await Await(foregroundStarted.Task);
            CollectionAssert.AreEqual(new[] { "foreground" }, order.ToArray());
            Assert.IsFalse(firstBackground.IsCompleted);
            Assert.IsFalse(queuedBackground.IsCompleted);
            releaseForeground.TrySetResult(true);
            Assert.AreEqual(3, await Await(foreground));
            Assert.AreEqual(1, await Await(firstBackground));
            Assert.AreEqual(2, await Await(queuedBackground));
            Assert.AreEqual("foreground", order.First());
        }
        finally { releaseForeground.TrySetResult(true); lifetime.Cancel(); }
    }

    [TestMethod]
    public async Task MultipleForegroundRequestsAreSerializedWithoutDroppingResults()
    {
        var coordinator = new ModIndexWorkCoordinator();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var holderStarted = Signal();
        var releaseHolder = Signal();
        var active = 0;
        var holder = coordinator.RunForegroundAsync(async token =>
        {
            Assert.AreEqual(1, Interlocked.Increment(ref active));
            try
            {
                holderStarted.TrySetResult(true);
                await releaseHolder.Task.WaitAsync(token);
                return -1;
            }
            finally { Interlocked.Decrement(ref active); }
        }, lifetime.Token);
        try
        {
            await Await(holderStarted.Task);
            var requests = Enumerable.Range(0, 32).Select(index =>
                coordinator.RunForegroundAsync(async token =>
                {
                    Assert.AreEqual(1, Interlocked.Increment(ref active), "Foreground work may not overlap.");
                    try { token.ThrowIfCancellationRequested(); await Task.Yield(); return index; }
                    finally { Interlocked.Decrement(ref active); }
                }, lifetime.Token)).ToArray();
            releaseHolder.TrySetResult(true);
            Assert.AreEqual(-1, await Await(holder));
            var results = await Await(Task.WhenAll(requests));
            CollectionAssert.AreEqual(Enumerable.Range(0, 32).ToArray(), results);
            Assert.AreEqual(0, active);
        }
        finally { releaseHolder.TrySetResult(true); lifetime.Cancel(); }
    }

    [TestMethod]
    public async Task AlreadyCancelledForegroundDoesNotInterruptRunningBackground()
    {
        var coordinator = new ModIndexWorkCoordinator();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var started = Signal();
        var release = Signal();
        var observedToken = CancellationToken.None;
        var attempts = 0;
        var background = coordinator.RunBackgroundAsync(async token =>
        {
            observedToken = token;
            Interlocked.Increment(ref attempts);
            started.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return 7;
        }, lifetime.Token);
        try
        {
            await Await(started.Task);
            await Assert.ThrowsAsync<OperationCanceledException>(() => Await(coordinator.RunForegroundAsync<int>(
                _ => throw new InvalidOperationException("Cancelled work must never start."), cancelled.Token)));
            Assert.IsFalse(observedToken.IsCancellationRequested);
            Assert.AreEqual(1, attempts);
            release.TrySetResult(true);
            Assert.AreEqual(7, await Await(background));
        }
        finally { release.TrySetResult(true); lifetime.Cancel(); }
    }

    [TestMethod]
    public async Task ExternalBackgroundCancellationDoesNotRetryAndReleasesCoordinator()
    {
        var coordinator = new ModIndexWorkCoordinator();
        using var cancelled = new CancellationTokenSource();
        var started = Signal();
        var attempts = 0;
        var background = coordinator.RunBackgroundAsync(async token =>
        {
            Interlocked.Increment(ref attempts);
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, cancelled.Token);
        try
        {
            await Await(started.Task);
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => Await(background));
            Assert.AreEqual(1, attempts);
            Assert.AreEqual(9, await Await(coordinator.RunForegroundAsync(_ => Task.FromResult(9))));
            Assert.AreEqual(10, await Await(coordinator.RunBackgroundAsync(_ => Task.FromResult(10))));
        }
        finally { cancelled.Cancel(); }
    }

    [TestMethod]
    public async Task CancelledQueuedForegroundDoesNotExecuteAndCannotBlockLaterWork()
    {
        var coordinator = new ModIndexWorkCoordinator();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var queuedCancellation = new CancellationTokenSource();
        var started = Signal();
        var release = Signal();
        var holder = coordinator.RunForegroundAsync(async token =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return 1;
        }, lifetime.Token);
        try
        {
            await Await(started.Task);
            var queued = coordinator.RunForegroundAsync<int>(
                _ => throw new InvalidOperationException("Cancelled queue entry must never execute."), queuedCancellation.Token);
            queuedCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => Await(queued));
            release.TrySetResult(true);
            Assert.AreEqual(1, await Await(holder));
            Assert.AreEqual(2, await Await(coordinator.RunBackgroundAsync(_ => Task.FromResult(2), lifetime.Token)));
        }
        finally { release.TrySetResult(true); lifetime.Cancel(); queuedCancellation.Cancel(); }
    }

    [TestMethod]
    public async Task ForegroundFailureReleasesCoordinatorAndDoesNotPreventBackgroundRetry()
    {
        var coordinator = new ModIndexWorkCoordinator();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = Signal();
        var attempts = 0;
        var background = coordinator.RunBackgroundAsync(async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
            }
            return 4;
        }, lifetime.Token);
        try
        {
            await Await(started.Task);
            await Assert.ThrowsExactlyAsync<IOException>(() => Await(coordinator.RunForegroundAsync<int>(
                _ => Task.FromException<int>(new IOException("foreground failure")), lifetime.Token)));
            Assert.AreEqual(4, await Await(background));
            Assert.AreEqual(2, attempts);
            Assert.AreEqual(5, await Await(coordinator.RunForegroundAsync(_ => Task.FromResult(5), lifetime.Token)));
        }
        finally { lifetime.Cancel(); }
    }

    [TestMethod]
    public async Task BackgroundFailureIsNotRetriedAndDoesNotPoisonFutureRequests()
    {
        var coordinator = new ModIndexWorkCoordinator();
        var attempts = 0;
        await Assert.ThrowsExactlyAsync<IOException>(() => Await(coordinator.RunBackgroundAsync<int>(_ =>
        {
            Interlocked.Increment(ref attempts);
            throw new IOException("unreadable file");
        })));
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(11, await Await(coordinator.RunForegroundAsync(_ => Task.FromResult(11))));
        Assert.AreEqual(12, await Await(coordinator.RunBackgroundAsync(_ => Task.FromResult(12))));
    }

    [TestMethod]
    public async Task WorkOwnedCancellationIsNotMistakenForForegroundPreemption()
    {
        var coordinator = new ModIndexWorkCoordinator();
        var attempts = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Await(coordinator.RunBackgroundAsync<int>(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<int>(new OperationCanceledException("A dependency lookup cancelled itself."));
        })));
        Assert.AreEqual(1, attempts, "Only the coordinator's foreground-preemption token should cause retries.");
        Assert.AreEqual(13, await Await(coordinator.RunForegroundAsync(_ => Task.FromResult(13))));
    }

    [TestMethod]
    public async Task SynchronousForegroundExceptionReleasesItsLease()
    {
        var coordinator = new ModIndexWorkCoordinator();
        await Assert.ThrowsExactlyAsync<IOException>(() => Await(coordinator.RunForegroundAsync<int>(
            _ => throw new IOException("synchronous failure"))));
        Assert.AreEqual(14, await Await(coordinator.RunBackgroundAsync(_ => Task.FromResult(14))));
    }

    [TestMethod]
    public async Task ExternalForegroundCancellationWhileRunningReleasesWaitingBackground()
    {
        var coordinator = new ModIndexWorkCoordinator();
        using var cancellation = new CancellationTokenSource();
        var started = Signal();
        var foreground = coordinator.RunForegroundAsync(async token =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return 1;
        }, cancellation.Token);
        try
        {
            await Await(started.Task);
            var background = coordinator.RunBackgroundAsync(_ => Task.FromResult(2));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => Await(foreground));
            Assert.AreEqual(2, await Await(background));
        }
        finally { cancellation.Cancel(); }
    }

    [TestMethod]
    public async Task RepeatedForegroundPreemptionsRetryOnlyAfterExclusiveForegroundHasFinished()
    {
        var coordinator = new ModIndexWorkCoordinator();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstStarted = Signal();
        var secondStarted = Signal();
        var attempts = 0;
        var active = 0;
        var background = coordinator.RunBackgroundAsync(async token =>
        {
            Assert.AreEqual(1, Interlocked.Increment(ref active));
            try
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1) firstStarted.TrySetResult(true);
                if (attempt == 2) secondStarted.TrySetResult(true);
                if (attempt < 3) await Task.Delay(Timeout.Infinite, token);
                return 33;
            }
            finally { Interlocked.Decrement(ref active); }
        }, lifetime.Token);
        try
        {
            await Await(firstStarted.Task);
            Assert.AreEqual(1, await Await(coordinator.RunForegroundAsync(_ =>
            {
                Assert.AreEqual(0, active);
                return Task.FromResult(1);
            }, lifetime.Token)));
            await Await(secondStarted.Task);
            Assert.AreEqual(2, await Await(coordinator.RunForegroundAsync(_ =>
            {
                Assert.AreEqual(0, active);
                return Task.FromResult(2);
            }, lifetime.Token)));
            Assert.AreEqual(33, await Await(background));
            Assert.AreEqual(3, attempts);
        }
        finally { lifetime.Cancel(); }
    }
}
