using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Enhancements;
using PCL.Core.Minecraft.Instances;

namespace PCL.Core.Test.Minecraft.Enhancements;

[TestClass]
public sealed class InstanceEnhancementClonerTests
{
    [TestMethod]
    public void ExplicitTestCloneRekeysDeclarationsAndExcludesReportsAndRecoveryDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "PCLCloneEnhancements", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "versions", "source");
            var destination = Path.Combine(root, "versions", "copy");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "source.json"), "{\"id\":\"source\"}");
            var sourceStore = new InstanceEnhancementStore(source);
            var originalId = Guid.NewGuid();
            sourceStore.SaveDeclaration(new() { InstanceId = originalId, Components = [new() { Id = "protected", Disabled = true, Important = true }] });
            File.WriteAllText(sourceStore.ResolvedPath, "stale report");
            Directory.CreateDirectory(Path.Combine(sourceStore.DirectoryPath, "PackTransactions"));
            File.WriteAllText(Path.Combine(sourceStore.DirectoryPath, "PackTransactions", "transaction.json"), "unfinished");
            InstanceTestCopyService.Create(source, destination, "source", "copy");
            var copied = new InstanceEnhancementStore(destination);
            Assert.AreNotEqual(originalId, copied.ReadDeclaration().Value!.InstanceId);
            Assert.IsTrue(copied.ReadDeclaration().Value!.Components[0].Disabled);
            Assert.IsTrue(copied.ReadDeclaration().Value!.Components[0].Important);
            Assert.IsFalse(File.Exists(copied.ResolvedPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(copied.DirectoryPath, "PackTransactions")));
            Assert.AreEqual(originalId, sourceStore.ReadDeclaration().Value!.InstanceId);
            Assert.AreEqual("stale report", File.ReadAllText(sourceStore.ResolvedPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
