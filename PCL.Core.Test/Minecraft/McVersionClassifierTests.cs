using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class McVersionClassifierTests
{
    [TestMethod]
    public void RemovedSpecialVersionTypeIsOmittedFromSupportedCategories()
    {
        var version = new JsonObject
        {
            ["id"] = "legacy-special",
            ["type"] = "special"
        };

        Assert.IsNull(McVersionClassifier.ClassifyVersion(version));
    }

    [TestMethod]
    public void RemovedSpecialSnapshotIdsAreNotListed()
    {
        foreach (var id in new[] { "2.0", "15w14a", "20w14infinite", "24w14potato", "26w14a" })
        {
            var version = new JsonObject
            {
                ["id"] = id,
                ["type"] = "snapshot"
            };

            Assert.IsNull(McVersionClassifier.ClassifyVersion(version), $"{id} should not be listed");
        }
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
