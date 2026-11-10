using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class LiquidGlassCompositionTests
{
    [TestMethod]
    public void GlassModeFadesOnlyTheBackgroundAcrossTheEntireSliderRange()
    {
        for (var value = 0; value <= 600; value++)
        {
            Assert.AreEqual(1d, LiquidGlassComposition.WindowOpacity(true, value));
            Assert.AreEqual(value / 600d, LiquidGlassComposition.BackgroundOpacity(true, value), 1e-10);
        }
        Assert.AreEqual(0d, LiquidGlassComposition.BackgroundOpacity(true, 0));
        Assert.AreEqual(1d, LiquidGlassComposition.BackgroundOpacity(true, 600));
    }

    [TestMethod]
    public void DisablingGlassRestoresTheExistingWindowOpacityWithoutMutatingTheSetting()
    {
        foreach (var setting in new[] { 0, 10, 300, 599, 600 })
        {
            Assert.AreEqual(setting / 1000d + .4d, LiquidGlassComposition.WindowOpacity(false, setting), 1e-10);
            Assert.AreEqual(1d, LiquidGlassComposition.BackgroundOpacity(false, setting));
            Assert.AreEqual(1d, LiquidGlassComposition.WindowOpacity(true, setting));
            Assert.AreEqual(setting / 1000d + .4d, LiquidGlassComposition.WindowOpacity(false, setting), 1e-10);
        }
    }

    [TestMethod]
    public void InvalidPersistedOpacityNeverProducesInvalidWpfValues()
    {
        Assert.AreEqual(0d, LiquidGlassComposition.BackgroundOpacity(true, int.MinValue));
        Assert.AreEqual(1d, LiquidGlassComposition.BackgroundOpacity(true, int.MaxValue));
        Assert.AreEqual(.4d, LiquidGlassComposition.WindowOpacity(false, int.MinValue));
        Assert.AreEqual(1d, LiquidGlassComposition.WindowOpacity(false, int.MaxValue));
    }
}
