using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class McVersionClassifierTests
{
    [TestMethod]
    public void MinecraftSpecialVersionTypeKeepsItsDedicatedCategory()
    {
        var version = new JsonObject
        {
            ["id"] = "2.0",
            ["type"] = "special"
        };

        Assert.AreEqual(McVersionCategory.AprilFools, McVersionClassifier.ClassifyVersion(version));
    }

    [TestMethod]
    public void MinecraftSpecialSnapshotKeepsItsGameDescription()
    {
        var version = new JsonObject
        {
            ["id"] = "15w14a",
            ["type"] = "snapshot"
        };

        Assert.AreEqual(McVersionCategory.AprilFools, McVersionClassifier.ClassifyVersion(version));
        Assert.AreEqual("special", version["type"]?.ToString());
        Assert.AreEqual(McVersionClassifier.GetMcFoolName("15w14a"), version["lore"]?.ToString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(version["lore"]?.ToString()));
    }

    [TestMethod]
    public void SnapshotVersionsStillUseTheRegularSnapshotCategory()
    {
        var version = new JsonObject
        {
            ["id"] = "25w10a",
            ["type"] = "snapshot"
        };

        Assert.AreEqual(McVersionCategory.Snapshot, McVersionClassifier.ClassifyVersion(version));
    }
}
