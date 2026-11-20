using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO;

namespace PCL.Core.Test.IO;

[TestClass]
public sealed class ContainedFilePathTests
{
    [TestMethod]
    public void ResolveRejectsRootedAndParentPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.ThrowsExactly<InvalidDataException>(() => ContainedFilePath.Resolve(root, ".."));
        Assert.ThrowsExactly<InvalidDataException>(() => ContainedFilePath.Resolve(root, Path.GetFullPath(root)));
    }

    [TestMethod]
    public void ResolvePreservesContainedFileNames()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.AreEqual(Path.Combine(root, "instance"), ContainedFilePath.Resolve(root, "instance"));
    }

    [TestMethod]
    [DataRow("../outside.dll")]
    [DataRow("sub/../../outside.dll")]
    [DataRow("sub\\..\\outside.dll")]
    [DataRow("C:\\outside.dll")]
    [DataRow("\\\\server\\share\\outside.dll")]
    [DataRow("sub/file.dll:payload")]
    [DataRow("sub./file.dll")]
    [DataRow("sub /file.dll")]
    public void ArchiveNamesCannotEscapeOrRedirectTheDestination(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.ThrowsExactly<InvalidDataException>(() => ContainedFilePath.Resolve(root, name));
    }

    [TestMethod]
    public void NestedArchivePathsRetainTheirLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.AreEqual(Path.Combine(root, "x64", "native.dll"),
            ContainedFilePath.Resolve(root, "x64/native.dll"));
    }
}
