using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.WallpaperEngine;

namespace PCL.Core.Test.UI;

[TestClass]
public class WallpaperEngineSourceTests
{
    [TestMethod]
    public void CurrentUserAndMonitorAreSelectedWithoutReadingOtherProfiles()
    {
        const string json = """
            {
              "another": {"general":{"wallpaperconfig":{"selectedwallpapers":{"Monitor0":{"file":"wrong.mp4"}}}}},
              "player": {
                "general":{"wallpaperconfig":{"layout":0,"selectedwallpapers":{
                  "Monitor0":{"file":"D:/WP/video.mp4"},"Monitor1":{"file":"D:/WP/web/index.html"}}}},
                "wproperties":{"d:\\wp\\web\\index.html":{"Monitor1":{"volume":80,"speed":2}}}
              }
            }
            """;
        var selection = WallpaperEngineSource.ReadSelection(json, "PLAYER", 1)!;
        Assert.AreEqual("D:/WP/web/index.html", selection.File);
        StringAssert.Contains(selection.PropertiesJson, "\"speed\":2");
        Assert.IsNull(WallpaperEngineSource.ReadSelection(json, "unknown", 0));
        Assert.IsNull(WallpaperEngineSource.ReadSelection(json, "player", 4));
    }

    [TestMethod]
    public void CloneLayoutCanReuseItsSingleAssignment()
    {
        const string json = """
            {"general":{"wallpaperconfig":{"layout":1,"selectedwallpapers":{"Monitor0":{"file":"D:/a.mp4"}}}}}
            """;
        Assert.AreEqual("D:/a.mp4", WallpaperEngineSource.ReadSelection(json, "player", 2)!.File);
    }

    [TestMethod]
    public void PresetResolutionPreservesOverridesAndMutesOnlyItsOwnRenderer()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath("D:/workshop/431960/3/project.json")] =
                """{"dependency":"2","preset":{"speed":3,"color":"blue","volume":90}}""",
            [Path.GetFullPath("D:/workshop/431960/2/project.json")] =
                """{"dependency":"1","preset":{"speed":1,"density":5}}""",
            [Path.GetFullPath("D:/workshop/431960/1/project.json")] =
                """{"type":"Scene","file":"scene.json"}"""
        };
        var selection = new WallpaperEngineSelection("D:/workshop/431960/3/project.json",
            """{"color":"green","volume":70}""");
        var project = WallpaperEngineSource.ResolveProject(selection, path => files[path]);
        Assert.AreEqual("scene", project.Kind);
        Assert.AreEqual(Path.GetFullPath("D:/workshop/431960/1/project.json"), project.File);
        StringAssert.Contains(project.PropertiesJson, "\"speed\":3");
        StringAssert.Contains(project.PropertiesJson, "\"density\":5");
        StringAssert.Contains(project.PropertiesJson, "\"color\":\"green\"");
        StringAssert.Contains(project.PropertiesJson, "\"volume\":0");
        StringAssert.Contains(selection.PropertiesJson, "\"volume\":70");
    }

    [TestMethod]
    public void PresetCyclesAndExecutableProjectsAreRejected()
    {
        var selection = new WallpaperEngineSelection("D:/workshop/431960/1/project.json", "{}");
        Assert.ThrowsExactly<InvalidDataException>(() =>
            WallpaperEngineSource.ResolveProject(selection, _ => """{"dependency":"1"}"""));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            WallpaperEngineSource.ResolveProject(selection, _ => """{"type":"Application"}"""));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            WallpaperEngineSource.ResolveProject(selection, _ => """{"dependency":"../other"}"""));
    }

    [TestMethod]
    [DataRow("D:/video.mp4", "video")]
    [DataRow("D:/video.webm", "video")]
    [DataRow("D:/wallpaper/scene.pkg", "scene")]
    [DataRow("D:/wallpaper/index.html", "web")]
    public void DirectFilesDoNotRequireASeparateRenderingEngine(string path, string kind)
    {
        var project = WallpaperEngineSource.ResolveProject(
            new(path, "{}"), _ => throw new AssertFailedException("Unexpected manifest read."));
        Assert.AreEqual(kind, project.Kind);
        Assert.AreEqual("{\"volume\":0}", project.PropertiesJson);
    }

    [TestMethod]
    public void RenderingResolutionIsBoundedAndNeverZero()
    {
        Assert.AreEqual((960, 540), WallpaperEngineSource.RenderSize(1920, 1080, 50));
        Assert.AreEqual((480, 270), WallpaperEngineSource.RenderSize(1920, 1080, -5));
        Assert.AreEqual((1920, 1080), WallpaperEngineSource.RenderSize(1920, 1080, 200));
        Assert.AreEqual((3840, 2160), WallpaperEngineSource.RenderSize(7680, 4320, 100));
        Assert.AreEqual((16, 16), WallpaperEngineSource.RenderSize(1, 1, 25));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WallpaperEngineSource.RenderSize(0, 10, 100));
    }

    [TestMethod]
    public void LargestOverlapWinsAndTiesKeepPreviousMonitor()
    {
        (double, double, double, double)[] monitors = [(0, 0, 1920, 1080), (1920, 0, 1920, 1080)];
        Assert.AreEqual(0, WallpaperEngineSource.SelectMonitor(100, 100, 800, 600, monitors, 1));
        Assert.AreEqual(1, WallpaperEngineSource.SelectMonitor(1700, 100, 800, 600, monitors, 0));
        Assert.AreEqual(1, WallpaperEngineSource.SelectMonitor(1520, 100, 800, 600, monitors, 1));
        Assert.AreEqual(0, WallpaperEngineSource.SelectMonitor(1520, 100, 800, 600, monitors, 0));
        Assert.AreEqual(-1, WallpaperEngineSource.SelectMonitor(0, 0, 1, 1, []));
    }
}
