using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

[TestClass]
public class LunarCredentialConsentTests
{
    [TestMethod]
    public void AcceptedNoticeIsRememberedAndNotShownAgain()
    {
        var gate = new LunarCredentialConsent();
        var acknowledged = false;
        var prompts = 0;
        bool Confirm(int seconds)
        {
            Assert.AreEqual(3, seconds);
            prompts++;
            return true;
        }
        Assert.IsTrue(gate.EnsureAccepted(() => acknowledged, Confirm, () => acknowledged = true));
        // A new gate models reopening/restarting with the persisted setting.
        Assert.IsTrue(new LunarCredentialConsent().EnsureAccepted(
            () => acknowledged, Confirm, () => Assert.Fail("Already acknowledged")));
        Assert.AreEqual(1, prompts);
    }

    [TestMethod]
    public void CancelNeverRemembersAndAllowsRetry()
    {
        var gate = new LunarCredentialConsent();
        Assert.IsFalse(gate.EnsureAccepted(() => false, _ => false, () => Assert.Fail("Cancelled")));
        Assert.IsTrue(gate.EnsureAccepted(() => false, _ => true, () => { }));
    }

    [TestMethod]
    public void ReentrantEntryIsBlockedUntilConfirmationCompletes()
    {
        var gate = new LunarCredentialConsent();
        Assert.IsTrue(gate.EnsureAccepted(() => false, _ =>
        {
            Assert.IsFalse(gate.EnsureAccepted(() => false,
                _ => throw new Exception("Nested prompt"), () => Assert.Fail("Nested consent")));
            return true;
        }, () => { }));
    }

    [TestMethod]
    public void FailedPersistenceDoesNotGrantOrLatchConsent()
    {
        var gate = new LunarCredentialConsent();
        Assert.ThrowsExactly<InvalidOperationException>(() => gate.EnsureAccepted(
            () => false, _ => true, () => throw new InvalidOperationException("Storage unavailable")));
        Assert.IsFalse(gate.EnsureAccepted(() => false, _ => false, () => Assert.Fail()));
    }
}
