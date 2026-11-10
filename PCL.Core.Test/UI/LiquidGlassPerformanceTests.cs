using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class LiquidGlassPerformanceTests
{
    [TestMethod]
    public void EverySliderPositionRoundTripsAndNeverProducesAnUnintendedFrameCap()
    {
        for (var slider = 0; slider <= LiquidGlassPerformance.UnlimitedSliderValue; slider++)
        {
            var fps = LiquidGlassPerformance.FrameRateFromSlider(slider);
            Assert.IsTrue(fps == 0 || fps is >= 30 and <= 240);
            Assert.AreEqual(slider, LiquidGlassPerformance.SliderFromFrameRate(fps));
        }
        Assert.AreEqual(30, LiquidGlassPerformance.FrameRateFromSlider(0));
        Assert.AreEqual(240, LiquidGlassPerformance.FrameRateFromSlider(210));
        Assert.AreEqual(0, LiquidGlassPerformance.FrameRateFromSlider(211));
    }

    [TestMethod]
    public void InvalidPersistedValuesAreClampedWithoutEnablingUnlimitedByAccident()
    {
        foreach (var value in new[] { int.MinValue, -100, -1, 1, 29 })
            Assert.AreEqual(30, LiquidGlassPerformance.NormalizeFrameRate(value));
        Assert.AreEqual(240, LiquidGlassPerformance.NormalizeFrameRate(int.MaxValue));
        Assert.AreEqual(0, LiquidGlassPerformance.NormalizeFrameRate(0));
        Assert.AreEqual(30, LiquidGlassPerformance.FrameRateFromSlider(int.MinValue));
        Assert.AreEqual(0, LiquidGlassPerformance.FrameRateFromSlider(int.MaxValue));
    }

    [TestMethod]
    public void PresetsChangeActualRenderingParametersAndHaveIncreasingDefaultCaps()
    {
        var expectedCaps = new[] { 30, 60, 90, 120 };
        for (var preset = 0; preset < 4; preset++)
        {
            var profile = LiquidGlassPerformance.GetProfile(preset);
            Assert.AreEqual(expectedCaps[preset], profile.FrameRate);
            Assert.IsTrue(profile.BlurRadius >= 0 && profile.Dispersion >= 0);
            if (preset > 0)
            {
                Assert.IsTrue(profile.BlurRadius > LiquidGlassPerformance.GetProfile(preset - 1).BlurRadius);
                Assert.IsTrue(profile.Dispersion > LiquidGlassPerformance.GetProfile(preset - 1).Dispersion);
            }
        }
        Assert.IsFalse(LiquidGlassPerformance.GetProfile(0).Motion);
        Assert.AreEqual(LiquidGlassPerformance.GetProfile(1), LiquidGlassPerformance.GetProfile(-100));
    }

    [TestMethod]
    public void FrameGateAppliesToEveryFrameIncludingMovingWindowAndUnlimitedHasNoArtificialDelay()
    {
        const long frequency = 1_000_000;
        foreach (var fps in new[] { 30, 60, 90, 120, 240 })
        {
            Assert.IsFalse(LiquidGlassPerformance.ShouldRender(0, frequency, fps));
            Assert.IsFalse(LiquidGlassPerformance.ShouldRender((long)(frequency / (double)fps) - 1, frequency, fps));
            Assert.IsTrue(LiquidGlassPerformance.ShouldRender((long)Math.Ceiling(frequency / (double)fps), frequency, fps));
        }
        Assert.IsTrue(LiquidGlassPerformance.ShouldRender(0, frequency, 0));
        Assert.IsFalse(LiquidGlassPerformance.ShouldRender(-1, frequency, 0));
        Assert.IsFalse(LiquidGlassPerformance.ShouldRender(1, 0, 60));
    }

    [TestMethod]
    public void CaptureNeedsBothExplicitConsentsAndWallpaperModeNeverRequestsWindowExclusion()
    {
        for (var combination = 0; combination < 16; combination++)
        {
            var enabled = (combination & 1) != 0;
            var performanceConsent = (combination & 2) != 0;
            var desktopRequested = (combination & 4) != 0;
            var desktopConsent = (combination & 8) != 0;
            Assert.AreEqual(combination == 15,
                LiquidGlassPerformance.ShouldCaptureDesktop(enabled, performanceConsent, desktopRequested, desktopConsent));
        }
        Assert.IsFalse(LiquidGlassPerformance.ShouldCaptureDesktop(true, true, false, true));
        Assert.IsFalse(LiquidGlassPerformance.ShouldCaptureDesktop(true, false, true, false),
            "Old experimental settings cannot silently start capturing after upgrade.");
    }
}
