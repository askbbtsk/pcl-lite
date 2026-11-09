using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Test.Minecraft.Packs;

[TestClass]
public sealed class EnhancementExportTests
{
    [TestMethod]
    public void ReservedPairIsFilteredWithoutHidingOrdinaryUserDirectories()
    {
        Assert.IsTrue(PackExportContent.IsInternalPath("PCL/Enhancements/resolved.json"));
        Assert.IsTrue(PackExportContent.IsInternalPath(@"pcl\enhancements\instance.json"));
        Assert.IsTrue(PackExportContent.IsInternalPath(@"E:\game\versions\a\PCL\Enhancements\resolved.json"));
        Assert.IsFalse(PackExportContent.IsInternalPath("mods/Enhancements/config.json"));
        Assert.IsFalse(PackExportContent.IsInternalPath("PCL/Enhancements-user/config.json"));
        Assert.IsFalse(PackExportContent.IsInternalPath("PCL/Logo.png"));
        Assert.IsTrue(PackExportContent.IsInternalPath("PCL/ResourceCollections/abc.json"));
        Assert.IsFalse(PackExportContent.IsInternalPath("mods/ResourceCollections/user.json"));
    }

    [TestMethod]
    public void WholeInstancePclSubtreeAndDirectExtraSelectionsExcludePrivateRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "EnhancementExport", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "instance");
            var privateRoot = Path.Combine(source, "PCL", "Enhancements");
            Directory.CreateDirectory(privateRoot);
            File.WriteAllText(Path.Combine(privateRoot, "resolved.json"), "private");
            File.WriteAllText(Path.Combine(source, "PCL", "Logo.png"), "logo");
            Directory.CreateDirectory(Path.Combine(source, "mods", "Enhancements"));
            File.WriteAllText(Path.Combine(source, "mods", "Enhancements", "user.json"), "user");

            var instanceExport = Path.Combine(root, "whole");
            PackExportContent.CopyTree(source, instanceExport);
            Assert.IsFalse(Directory.Exists(Path.Combine(instanceExport, "PCL", "Enhancements")));
            Assert.AreEqual("logo", File.ReadAllText(Path.Combine(instanceExport, "PCL", "Logo.png")));
            Assert.AreEqual("user", File.ReadAllText(Path.Combine(instanceExport, "mods", "Enhancements", "user.json")));

            var pclExport = Path.Combine(root, "pcl-only");
            PackExportContent.CopyTree(Path.Combine(source, "PCL"), pclExport);
            Assert.IsFalse(Directory.Exists(Path.Combine(pclExport, "Enhancements")));
            Assert.IsTrue(File.Exists(Path.Combine(pclExport, "Logo.png")));

            var extraExport = Path.Combine(root, "direct-extra");
            PackExportContent.CopyTree(privateRoot, extraExport);
            Assert.IsFalse(Directory.Exists(extraExport));
            Assert.AreEqual("private", File.ReadAllText(Path.Combine(privateRoot, "resolved.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
