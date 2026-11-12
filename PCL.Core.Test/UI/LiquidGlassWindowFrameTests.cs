using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class LiquidGlassWindowFrameTests
{
    [TestMethod]
    public void GlassModeHidesAndRestoresLegacyOuterCornerShadow()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "Plain Craft Launcher 2", "FormMain.xaml"));
        string source = File.ReadAllText(Path.Combine(root, "Plain Craft Launcher 2", "Modules", "UI",
            "Theme", "LiquidGlassCore.cs"));
        string sampler = File.ReadAllText(Path.Combine(root, "Plain Craft Launcher 2", "Modules", "UI",
            "Theme", "LiquidGlassScreenshotCapturePlatform.cs"));

        Assert.IsTrue(xaml.Contains("x:Name=\"PanWindowShadow\"", StringComparison.Ordinal));
        Assert.IsTrue(Regex.IsMatch(xaml, "GlassFrameThickness=\"0\""));
        Assert.IsFalse(source.Contains("GlassFrameThicknessProperty", StringComparison.Ordinal),
            "Glass rendering must not change the system WindowChrome frame; doing so can paint a permanent outer ring.");
        Assert.IsTrue(sampler.Contains("SetWindowRgn(host, shape, true)", StringComparison.Ordinal),
            "The desktop sampler must stay clipped to the rounded viewport without changing the top-level DWM frame.");
        Assert.IsTrue(source.Contains("_windowShadowVisibility = window.PanWindowShadow.Visibility;",
            StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("window.PanWindowShadow.Visibility = Visibility.Collapsed;",
            StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("window.PanWindowShadow.Visibility = _windowShadowVisibility;",
            StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Plain Craft Launcher 2", "FormMain.xaml")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the PCL Lite source tree.");
    }
}
