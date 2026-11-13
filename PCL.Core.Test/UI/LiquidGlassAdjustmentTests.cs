using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

// No windows, capture sessions, dispatcher loops or screen access.
[TestClass]
public sealed class LiquidGlassAdjustmentTests
{
    [TestMethod]
    public void RefractionRangesFromZeroToTwoHundredPercentAndKeepsDefault()
    {
        Assert.AreEqual(.05, LiquidGlassMaterial.RefractionStrength(5), 1e-12);
        Assert.AreEqual(0, LiquidGlassMaterial.RefractionStrength(int.MinValue), 1e-12);
        Assert.AreEqual(.10, LiquidGlassMaterial.RefractionStrength(int.MaxValue), 1e-12);
        for (int setting = 0; setting <= 10; setting++)
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
    public void ExactPercentSupportsEveryStepAndPreservesLegacyDefaults()
    {
        Assert.AreEqual(100, LiquidGlassMaterial.ResolveRefractionPercent(-1, 5));
        Assert.AreEqual(200, LiquidGlassMaterial.ResolveRefractionPercent(-1, 15));
        for (var percent = 0; percent <= 200; percent++)
        {
            Assert.AreEqual(percent, LiquidGlassMaterial.ResolveRefractionPercent(percent, 5));
            Assert.AreEqual(percent / 2000d, LiquidGlassMaterial.RefractionFromPercent(percent), 1e-12);
        }
        Assert.AreEqual(0, LiquidGlassMaterial.ResolveRefractionPercent(int.MinValue, 5));
        Assert.AreEqual(200, LiquidGlassMaterial.ResolveRefractionPercent(int.MaxValue, 5));
    }

    [TestMethod]
    public void CardTintControlsOnlyOpacityAndPreservesLegacyColor()
    {
        var original = System.Windows.Media.Color.FromArgb(196, 30, 40, 50);
        Assert.AreEqual(original, LiquidGlassMaterial.CardTint(original, -1));
        Assert.AreEqual(0, LiquidGlassMaterial.CardTint(original, 0).A);
        Assert.AreEqual(255, LiquidGlassMaterial.CardTint(original, 100).A);
        for (var percent = 0; percent <= 100; percent++)
        {
            var color = LiquidGlassMaterial.CardTint(original, percent);
            Assert.AreEqual(original.R, color.R);
            Assert.AreEqual(original.G, color.G);
            Assert.AreEqual(original.B, color.B);
            Assert.AreEqual((byte)Math.Round(percent * 2.55), color.A);
        }
        Assert.AreEqual(77, LiquidGlassMaterial.CardOpacityPercent(-1, true));
        Assert.AreEqual(80, LiquidGlassMaterial.CardOpacityPercent(-1, false));
    }

    [TestMethod]
    public void BrightnessDefaultsToIdentityClampsAndPreservesAlpha()
    {
        var color = System.Windows.Media.Color.FromArgb(117, 80, 100, 160);
        Assert.AreEqual(color, LiquidGlassMaterial.AdjustBrightness(color, 0));
        for (int value = -50; value <= 50; value++)
        {
            var adjusted = LiquidGlassMaterial.AdjustBrightness(color, value);
            Assert.AreEqual(color.A, adjusted.A);
            Assert.IsTrue(value >= 0 ? adjusted.R >= color.R : adjusted.R <= color.R);
        }
        Assert.AreEqual(LiquidGlassMaterial.AdjustBrightness(color, -50),
            LiquidGlassMaterial.AdjustBrightness(color, int.MinValue));
        Assert.AreEqual(LiquidGlassMaterial.AdjustBrightness(color, 50),
            LiquidGlassMaterial.AdjustBrightness(color, int.MaxValue));
    }

    [TestMethod]
    public void DragUsesTheConfiguredFrameRateIncludingUnlimited()
    {
        Assert.AreEqual(0, TahoeCaptureResources.FrameRateDuringMove(0, true));
        Assert.AreEqual(0, TahoeCaptureResources.FrameRateDuringMove(0, false));
        foreach (int configured in new[] { int.MinValue, 30, 45, 60, 120, 240, int.MaxValue })
        {
            int normal = System.Math.Clamp(configured, 30, 240);
            Assert.AreEqual(normal, TahoeCaptureResources.FrameRateDuringMove(configured, true));
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
