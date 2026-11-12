using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

// No windows, capture sessions, dispatcher loops or screen access.
[TestClass]
public sealed class LiquidGlassAdjustmentTests
{
    [TestMethod]
    public void RefractionCannotFallBelowTheCurrentDefaultAndRejectsOutOfRangeConfiguration()
    {
        Assert.AreEqual(.05, LiquidGlassMaterial.RefractionStrength(5), 1e-12);
        Assert.AreEqual(.05, LiquidGlassMaterial.RefractionStrength(int.MinValue), 1e-12);
        Assert.AreEqual(.15, LiquidGlassMaterial.RefractionStrength(int.MaxValue), 1e-12);
        for (int setting = 5; setting <= 15; setting++)
        {
            Assert.AreEqual(setting / 100d, LiquidGlassMaterial.RefractionStrength(setting), 1e-12);
            Assert.AreEqual(setting, LiquidGlassMaterial.RefractionSetting(setting));
        }
    }

    [TestMethod]
    public void FrostAllowsClearMaterialAndClampsCorruptSettings()
    {
        Assert.AreEqual(0, LiquidGlassMaterial.FrostRadius(int.MinValue));
        Assert.AreEqual(24, LiquidGlassMaterial.FrostRadius(int.MaxValue));
        for (int radius = 0; radius <= 24; radius++)
            Assert.AreEqual(radius, LiquidGlassMaterial.FrostRadius(radius));
    }

    [TestMethod]
    public void DragCapPreservesLowerCapsAndNormalCapReturnsAfterMoving()
    {
        Assert.AreEqual(60, TahoeCaptureResources.FrameRateDuringMove(0, true));
        Assert.AreEqual(0, TahoeCaptureResources.FrameRateDuringMove(0, false));
        foreach (int configured in new[] { int.MinValue, 30, 45, 60, 120, 240, int.MaxValue })
        {
            int normal = System.Math.Clamp(configured, 30, 240);
            Assert.AreEqual(System.Math.Min(normal, 60), TahoeCaptureResources.FrameRateDuringMove(configured, true));
            Assert.AreEqual(normal, TahoeCaptureResources.FrameRateDuringMove(configured, false));
        }
    }

    [TestMethod]
    public void ContinuousMovesKeepBufferAndWarmupWhileResizingStillReallocates()
    {
        var original = new TahoeCaptureRegion(10, 20, 900, 550, 0, 0, 1) { RadiusX = 22, RadiusY = 22 };
        Assert.IsTrue(TahoeCaptureResources.RequiresNewBuffer(null, original));
        for (int version = 2; version < 1000; version++)
        {
            var next = original with { X = version, Y = version * 2, Version = version };
            Assert.IsFalse(TahoeCaptureResources.RequiresNewBuffer(original, next));
        }
        Assert.IsFalse(TahoeCaptureResources.RequiresNewBuffer(original, original with { RadiusX = 30 }));
        Assert.IsTrue(TahoeCaptureResources.RequiresNewBuffer(original, original with { Width = 901 }));
        Assert.IsTrue(TahoeCaptureResources.RequiresNewBuffer(original, original with { Height = 551 }));
    }

    [TestMethod]
    public void DelayedFramesFollowWindowWithoutLosingTheRoundedViewportInset()
    {
        var region = new TahoeCaptureRegion(110, 210, 900, 550, 0, 0, 1)
            { WindowOffsetX = 10, WindowOffsetY = 10 };
        foreach (int moved in new[] { -2000, -10, 0, 10, 100, 3000 })
            Assert.AreEqual((moved + 10, moved + 10),
                TahoeCaptureResources.DesktopOrigin(region, 0, 0, moved, moved));
        // Legacy capture regions still retain their monitor-relative semantics.
        Assert.AreEqual((-1810, 210), TahoeCaptureResources.DesktopOrigin(
            region with { WindowOffsetX = null, WindowOffsetY = null }, -1920, 0, 500, 500));
    }
}
