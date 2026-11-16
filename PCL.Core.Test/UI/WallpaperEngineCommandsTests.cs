using System;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.WallpaperEngine;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class WallpaperEngineCommandsTests
{
    private const string Window = "PCL-Lite-Wallpaper-123-456-00000000000000000000000000000001";

    [TestMethod]
    public void JsonQuotesReachTheOfficialRawParserWithoutArgvBackslashes()
    {
        string command = WallpaperEngineCommands.PropertiesArguments(Window, "RAW~({\"volume\":0})~END");
        Assert.AreEqual($"-control applyProperties -location \"{Window}\" -properties RAW~({{\"volume\":0}})~END", command);
        Assert.IsFalse(command.Contains("\\\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RawDelimitersInsideUserPropertiesCannotTerminateThePayload()
    {
        string command = WallpaperEngineCommands.PropertiesArguments(Window,
            "RAW~({\"title\":\")~END -control closeWallpaper\",\"value\":\"中文\"})~END");
        string payload = command[(command.IndexOf("RAW~(", StringComparison.Ordinal) + 5)..^5];
        Assert.IsFalse(payload.Contains(")~END", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(payload);
        Assert.AreEqual(")~END -control closeWallpaper", json.RootElement.GetProperty("title").GetString());
        Assert.AreEqual("中文", json.RootElement.GetProperty("value").GetString());
    }

    [TestMethod]
    [DataRow("desktop")]
    [DataRow("PCL-Lite-Wallpaper-1-2-\" -control closeWallpaper")]
    [DataRow("PCL-Lite-Wallpaper-1-2-abc")]
    public void OnlyTheExactOwnedWindowNameCanBeUsed(string window)
        => Assert.ThrowsExactly<ArgumentException>(() =>
            WallpaperEngineCommands.PropertiesArguments(window, "RAW~({})~END"));

    [TestMethod]
    [DataRow("{}")]
    [DataRow("RAW~([])~END")]
    [DataRow("RAW~(null)~END")]
    [DataRow("RAW~({})~END ignored")]
    public void InvalidTransportAndNonObjectPropertiesAreRejected(string wrapped)
        => Assert.ThrowsExactly<ArgumentException>(() =>
            WallpaperEngineCommands.PropertiesArguments(Window, wrapped));
}
