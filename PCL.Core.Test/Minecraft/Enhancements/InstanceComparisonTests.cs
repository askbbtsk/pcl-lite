using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Enhancements;

namespace PCL.Core.Test.Minecraft.Enhancements;

[TestClass]
public sealed class InstanceComparisonTests
{
    [TestMethod]
    public void UnreadableDirectoryCannotBeReportedAsMissingFiles()
    {
        var left = new InstanceComparisonSnapshot(new System.Collections.Generic.Dictionary<string, string>
            { ["config/file.cfg"] = "a" }, [], []);
        var right = new InstanceComparisonSnapshot(new System.Collections.Generic.Dictionary<string, string>(), [], ["config"]);
        var difference = InstanceComparison.Compare(left, right);
        Assert.AreEqual("unknown", difference.Single().Category);
        Assert.IsNull(difference.Single().Left);
        Assert.IsNull(difference.Single().Right);
    }

    [TestMethod]
    public void ActualUserEditsAndDisabledFileNamesAppearWithoutReadingStaleResolutionCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "PCLComparison", Guid.NewGuid().ToString("N"));
        try
        {
            var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
            foreach (var path in new[] { a, b })
            {
                Directory.CreateDirectory(Path.Combine(path, "mods")); Directory.CreateDirectory(Path.Combine(path, "config"));
                File.WriteAllText(Path.Combine(path, "config", "same.cfg"), "same");
            }
            File.WriteAllText(Path.Combine(a, "mods", "mod.jar"), "mod");
            File.WriteAllText(Path.Combine(b, "mods", "mod.jar.disabled"), "mod");
            File.WriteAllText(Path.Combine(a, "config", "changed.cfg"), "one");
            File.WriteAllText(Path.Combine(b, "config", "changed.cfg"), "two");
            var snapshotA = InstanceComparison.Capture(a, [new() { Id = "loader", Version = "1" }]);
            var snapshotB = InstanceComparison.Capture(b, [new() { Id = "loader", Version = "2" }]);
            var difference = InstanceComparison.Compare(snapshotA, snapshotB);
            Assert.AreEqual(4, difference.Count);
            Assert.IsTrue(difference.Any(item => item.Name == "config/changed.cfg"));
            Assert.IsTrue(difference.Any(item => item.Name == "mods/mod.jar.disabled"));
            Assert.IsFalse(difference.Any(item => item.Name.StartsWith(root, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual("one", File.ReadAllText(Path.Combine(a, "config", "changed.cfg")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
