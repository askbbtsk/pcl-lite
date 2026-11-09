using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Test.App.Lunar;

[TestClass]
public sealed class LunarSessionCoordinatorTests
{
    [TestMethod]
    public void OldExitAndBufferedOutputCannotEndOrContaminateReplacementGame()
    {
        var sessions = new LunarSessionCoordinator();
        var status = "idle";
        var ipcOwner = "none";
        var old = sessions.Begin(() => status = "starting old");
        sessions.RunIfCurrent(old, () => ipcOwner = "old");
        var current = sessions.Begin(() => { status = "starting new"; ipcOwner = "new"; });
        var oldNotificationCount = 0;
        Assert.IsFalse(sessions.Complete(old, () => { status = "game ended"; ipcOwner = "none"; oldNotificationCount++; }));
        Assert.IsFalse(sessions.RunIfCurrent(old, () => status = "old buffered output"));
        Assert.AreEqual("starting new", status);
        Assert.AreEqual("new", ipcOwner);
        Assert.AreEqual(0, oldNotificationCount);
        Assert.IsTrue(sessions.RunIfCurrent(current, () => status = "running"));
        Assert.IsTrue(sessions.Complete(current, () => { status = "ended new"; ipcOwner = "none"; }));
        Assert.AreEqual("ended new", status);
        Assert.IsFalse(sessions.Complete(current, () => Assert.Fail("Duplicate exit must not be published.")));
        Assert.IsFalse(sessions.RunIfCurrent(current, () => Assert.Fail("Exited game must not publish new state.")));
    }

    [TestMethod]
    public void LaunchEntryCannotReplaceActiveSession_ButCanBeginAfterFinalStateIsWritten()
    {
        var sessions = new LunarSessionCoordinator();
        var status = "idle";
        var busy = false;
        var old = sessions.TryBegin(() => { status = "launching"; busy = true; });
        Assert.AreNotEqual(0L, old);
        Assert.AreEqual(0L, sessions.TryBegin(() => Assert.Fail("A second click must not start another launch.")));
        Assert.IsTrue(sessions.Complete(old, () => { status = "ended"; busy = false; }));
        var replacement = sessions.TryBegin(() =>
        {
            Assert.AreEqual("ended", status);
            Assert.IsFalse(busy);
            status = "new launch";
        });
        Assert.AreNotEqual(0L, replacement);
        Assert.IsFalse(sessions.IsLatest(old));
        Assert.IsTrue(sessions.IsCurrent(replacement));
    }

    [TestMethod]
    public void QueuedExitAndStopNotificationsAreDiscardedAfterReplacement_ActualExitStillPublished()
    {
        var sessions = new LunarSessionCoordinator();
        var pending = new List<Action>();
        var log = new List<string>();
        var old = sessions.TryBegin(() => { });
        sessions.Complete(old, () => { });
        pending.Add(() => { if (sessions.IsLatest(old)) log.Add("old exit"); });
        pending.Add(() => { if (sessions.IsLatest(old)) log.Add("old stop finished"); });
        var replacement = sessions.TryBegin(() => { });
        foreach (var notification in pending) notification();
        Assert.AreEqual(0, log.Count);
        Assert.IsFalse(sessions.RunIfCurrent(old, () => log.Add("stale stop reset")));
        Assert.IsTrue(sessions.Complete(replacement, () => { }));
        if (sessions.IsLatest(replacement)) log.Add("actual replacement exit");
        CollectionAssert.AreEqual(new[] { "actual replacement exit" }, log);
    }

    [TestMethod]
    public async Task CompletionDoesNotHoldGateDuringLaterNotificationOrDialog()
    {
        var sessions = new LunarSessionCoordinator();
        var old = sessions.TryBegin(() => { });
        Assert.IsTrue(sessions.Complete(old, () => { }));
        using var notificationStarted = new ManualResetEventSlim();
        using var releaseNotification = new ManualResetEventSlim();
        var dialog = Task.Run(() =>
        {
            // Host notifications run after Complete, rather than inside its state callback.
            notificationStarted.Set();
            releaseNotification.Wait(TimeSpan.FromSeconds(5));
        });
        Assert.IsTrue(notificationStarted.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var launch = Task.Run(() => sessions.TryBegin(() => { }));
            Assert.AreSame(launch, await Task.WhenAny(launch, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.AreNotEqual(0L, await launch);
        }
        finally { releaseNotification.Set(); await dialog; }
    }
}
