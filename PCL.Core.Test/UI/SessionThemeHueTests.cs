using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Theme;

namespace PCL.Core.Test.UI;

[TestClass]
public class SessionThemeHueTests
{
    [TestMethod]
    public void JesterIsRandomOncePerProcessAndIndependentOfSavedPurpleHue()
    {
        var hue = SessionThemeHue.Resolve(13, 290);
        Assert.IsTrue(hue is >= 0 and < 360);
        for (var i = 0; i < 100; i++)
            Assert.AreEqual(hue, SessionThemeHue.Resolve(13, i));
    }

    [TestMethod]
    public void OtherThemesKeepTheirConfiguredHue()
    {
        foreach (var preset in new[] { -1, 0, 2, 7, 14 })
            Assert.AreEqual(285, SessionThemeHue.Resolve(preset, 285));
    }

    [TestMethod]
    public void LuckyThemeAlsoKeepsOneRandomHueForTheWholeLaunch()
    {
        var hue = SessionThemeHue.Resolve(12, 0);
        Assert.IsTrue(hue is >= 0 and < 360);
        for (var i = 0; i < 100; i++)
            Assert.AreEqual(hue, SessionThemeHue.Resolve(12, i));
        Assert.IsTrue(PclThemePresets.ByIndex(12)!.Args.TopbarWide,
            "Randomizing hue must preserve Lucky's wide gradient.");
    }
}
