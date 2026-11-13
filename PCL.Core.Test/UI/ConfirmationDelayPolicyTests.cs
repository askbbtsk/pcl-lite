using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class ConfirmationDelayPolicyTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    [TestMethod]
    public void QueueTimeDoesNotCount_AndFiveSecondsAreRequiredFromShown()
    {
        var clock = new Clock();
        var delay = new ConfirmationDelayPolicy(TimeSpan.FromSeconds(5), clock);
        clock.Ticks = TimeSpan.FromHours(1).Ticks;
        Assert.IsFalse(delay.CanConfirm);
        Assert.AreEqual(5, delay.RemainingSeconds);
        delay.MarkShown();
        clock.Ticks += TimeSpan.FromSeconds(4.999).Ticks;
        Assert.IsFalse(delay.CanConfirm);
        Assert.AreEqual(1, delay.RemainingSeconds);
        delay.MarkShown(); // Repeated Loaded/visibility notifications cannot reset or skip the gate.
        clock.Ticks += TimeSpan.FromMilliseconds(1).Ticks;
        Assert.IsTrue(delay.CanConfirm);
        Assert.AreEqual(0, delay.RemainingSeconds);
    }

    [TestMethod]
    public void ModMigrationRequiresThreeFullSecondsFromVisibility()
    {
        var clock = new Clock();
        var delay = new ConfirmationDelayPolicy(TimeSpan.FromSeconds(3), clock);
        delay.MarkShown();
        clock.Ticks = TimeSpan.FromMilliseconds(2999).Ticks;
        Assert.IsFalse(delay.CanConfirm);
        clock.Ticks += TimeSpan.FromMilliseconds(1).Ticks;
        Assert.IsTrue(delay.CanConfirm);
    }

    [TestMethod]
    public void LunarRiskConfirmationWaitsThreeSecondsAndCanAlwaysBeCancelled()
    {
        var clock = new Clock();
        var delay = new ConfirmationDelayPolicy(
            TimeSpan.FromSeconds(PCL.Core.App.Lunar.LunarCredentialConsent.ConfirmationSeconds), clock);
        clock.Ticks = TimeSpan.FromHours(1).Ticks;
        Assert.IsFalse(delay.CanConfirm, "Time before showing cannot satisfy the countdown.");
        delay.MarkShown();
        clock.Ticks += TimeSpan.FromMilliseconds(2999).Ticks;
        Assert.IsFalse(delay.CanConfirm);
        Assert.AreEqual(1, delay.RemainingSeconds);
        delay.Cancel();
        clock.Ticks += TimeSpan.FromDays(1).Ticks;
        Assert.IsFalse(delay.CanConfirm, "Cancelling during the delay must remain cancellation.");
    }

    [TestMethod]
    public void CancelledConfirmationNeverBecomesAvailableEvenAfterTimePasses()
    {
        var clock = new Clock();
        var delay = new ConfirmationDelayPolicy(TimeSpan.FromSeconds(5), clock);
        delay.MarkShown(); delay.Cancel();
        clock.Ticks += TimeSpan.FromDays(1).Ticks;
        Assert.IsFalse(delay.CanConfirm);
        delay.MarkShown();
        Assert.IsFalse(delay.CanConfirm);
    }

    [TestMethod]
    public void CancellationBeforeVisibilityCannotBeRearmedByLoaded()
    {
        var clock = new Clock();
        var delay = new ConfirmationDelayPolicy(TimeSpan.FromSeconds(5), clock);
        delay.Cancel(); delay.MarkShown(); clock.Ticks = TimeSpan.FromHours(1).Ticks;
        Assert.IsFalse(delay.CanConfirm);
    }
}
