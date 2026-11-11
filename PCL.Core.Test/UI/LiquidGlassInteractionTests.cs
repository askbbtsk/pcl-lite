using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class LiquidGlassInteractionTests
{
    [TestMethod]
    public void MoveAndResizeBecomeFrostedUntilExitAndUnrelatedMessagesDoNotResumeCapture()
    {
        Assert.IsTrue(LiquidGlassInteraction.Moving(false, 0x0231));
        Assert.IsTrue(LiquidGlassInteraction.Moving(true, 0x0231));
        Assert.IsTrue(LiquidGlassInteraction.Moving(true, 0x0003)); // WM_MOVE
        Assert.IsTrue(LiquidGlassInteraction.Moving(true, 0x0005)); // WM_SIZE
        Assert.IsFalse(LiquidGlassInteraction.Moving(true, 0x0232));
        Assert.IsFalse(LiquidGlassInteraction.Moving(false, 0x0232));
        Assert.IsFalse(LiquidGlassInteraction.Moving(false, 0x0003));
    }

    [TestMethod]
    public void RefreshActionsFitInsideGlassPillAndOriginalLayoutReturnsWhenDisabled()
    {
        Assert.AreEqual(18d, LiquidGlassInteraction.NavigationActionInset(5, true));
        Assert.AreEqual(24d, LiquidGlassInteraction.NavigationActionInset(24, true));
        Assert.AreEqual(5d, LiquidGlassInteraction.NavigationActionInset(5, false));
    }
}
