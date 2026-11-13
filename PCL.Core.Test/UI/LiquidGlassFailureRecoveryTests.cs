using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public class LiquidGlassFailureRecoveryTests
{
    [TestMethod]
    public void OomClosesGlassOnceAndSameWpfExceptionIsHandledIdempotently()
    {
        var recovery = new LiquidGlassFailureRecovery();
        var error = new OutOfMemoryException();
        var calls = 0;
        Assert.IsTrue(recovery.TryRecover(error, true, () => calls++));
        Assert.IsTrue(recovery.TryRecover(error, false, () => calls++));
        Assert.AreEqual(1, calls);
        Assert.IsFalse(recovery.TryRecover(new OutOfMemoryException(), true, () => calls++));
    }

    [TestMethod]
    public void RecognizesReportedRenderFailureAndWrappedOomButNotOtherErrors()
    {
        Assert.IsTrue(LiquidGlassFailureRecovery.IsGraphicsResourceFailure(
            new COMException("UCEERR_RENDERTHREADFAILURE", unchecked((int)0x88980406))));
        Assert.IsTrue(LiquidGlassFailureRecovery.IsGraphicsResourceFailure(
            new InvalidOperationException("outer", new OutOfMemoryException())));
        Assert.IsFalse(LiquidGlassFailureRecovery.IsGraphicsResourceFailure(new NullReferenceException()));
        var recovery = new LiquidGlassFailureRecovery();
        Assert.IsFalse(recovery.TryRecover(new NullReferenceException(), true, () => Assert.Fail()));
        Assert.IsFalse(recovery.TryRecover(new OutOfMemoryException(), false, () => Assert.Fail()));
        Assert.IsTrue(recovery.TryRecover(new OutOfMemoryException(), true, () => { }));
    }

    [TestMethod]
    public void FailedReleaseDoesNotClaimSuccessOrEnterRetryLoop()
    {
        var recovery = new LiquidGlassFailureRecovery();
        Assert.IsFalse(recovery.TryRecover(new OutOfMemoryException(), true,
            () => throw new InvalidOperationException("release failed")));
        Assert.IsFalse(recovery.TryRecover(new OutOfMemoryException(), true, () => Assert.Fail()));
    }
}
