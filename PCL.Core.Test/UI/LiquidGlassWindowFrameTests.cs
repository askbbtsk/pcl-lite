using System;
using System.IO;
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

        Assert.IsTrue(xaml.Contains("x:Name=\"PanWindowShadow\"", StringComparison.Ordinal));
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
