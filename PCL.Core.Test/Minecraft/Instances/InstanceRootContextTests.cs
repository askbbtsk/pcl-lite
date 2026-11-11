using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Instances;

namespace PCL.Core.Test.Minecraft.Instances;

[TestClass]
public sealed class InstanceRootContextTests
{
    [TestMethod]
    public void SourcePathOwnsTheRootAndTrailingSeparatorsAreNormalized()
    {
        var root = Path.Combine(Path.GetTempPath(), "root-context", ".minecraft");
        var instance = Path.Combine(root, "versions", "pack");
        Assert.AreEqual(Path.GetFullPath(root) + Path.DirectorySeparatorChar, InstanceRootContext.Require(instance, root));
        Assert.AreEqual(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
            InstanceRootContext.Require(instance + Path.DirectorySeparatorChar, root + Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void ForeignRootAndNonVersionDirectoriesFailBeforeTheyCanWriteAnything()
    {
        var root = Path.Combine(Path.GetTempPath(), "root-context", ".minecraft");
        Assert.Throws<InvalidDataException>(() => InstanceRootContext.Require(
            Path.Combine(root, "versions", "pack"), root + "-other"));
        Assert.Throws<InvalidDataException>(() => InstanceRootContext.Require(
            Path.Combine(root, "not-versions", "pack"), root));
        Assert.Throws<InvalidDataException>(() => InstanceRootContext.Require(
            Path.Combine(root, "versions", "pack", "child"), root));
    }
}
