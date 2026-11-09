using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.ResourceProject;

namespace PCL.Core.Test.Minecraft.ResourceProject;

[TestClass]
public sealed class ResourceRecycleBinServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLResourceRecycleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void RecycleAndRestore_FilePreservesBytesAndOriginalPath()
    {
        var resourceRoot = Path.Combine(_root, "game", "mods");
        var recycleRoot = Path.Combine(_root, "game", "PCL", "ResourceRecycle", "Mod");
        var source = Path.Combine(resourceRoot, "example.jar");
        Directory.CreateDirectory(resourceRoot);
        File.WriteAllBytes(source, [0, 1, 2, 255]);

        var moved = ResourceRecycleBinService.RecycleMany(resourceRoot, recycleRoot, [source]);

        Assert.IsFalse(File.Exists(source));
        Assert.AreEqual(1, moved.Count);
        Assert.AreEqual("example.jar", moved[0].Name);
        Assert.AreEqual(source, moved[0].OriginalPath);
        var listed = ResourceRecycleBinService.List(resourceRoot, recycleRoot);
        Assert.AreEqual(moved[0].Id, listed.Single().Id);
        Assert.AreEqual(source, ResourceRecycleBinService.Restore(resourceRoot, recycleRoot, moved[0].Id).OriginalPath);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(source));
        Assert.AreEqual(0, ResourceRecycleBinService.List(resourceRoot, recycleRoot).Count);
    }

    [TestMethod]
    public void RecycleAndRestore_DirectoryPreservesNestedFiles()
    {
        var resourceRoot = Path.Combine(_root, "game", "resourcepacks");
        var recycleRoot = Path.Combine(_root, "game", "PCL", "ResourceRecycle", "ResourcePack");
        var source = Path.Combine(resourceRoot, "Pack");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "pack.mcmeta"), "metadata");

        var moved = ResourceRecycleBinService.RecycleMany(resourceRoot, recycleRoot, [source]).Single();

        Assert.IsFalse(Directory.Exists(source));
        Assert.IsTrue(moved.IsDirectory);
        ResourceRecycleBinService.Restore(resourceRoot, recycleRoot, moved.Id);
        Assert.AreEqual("metadata", File.ReadAllText(Path.Combine(source, "nested", "pack.mcmeta")));
    }

    [TestMethod]
    public void Restore_ExistingDestinationDoesNotOverwriteOrLoseRecycleEntry()
    {
        var resourceRoot = Path.Combine(_root, "game", "mods");
        var recycleRoot = Path.Combine(_root, "game", "PCL", "ResourceRecycle", "Mod");
        var source = Path.Combine(resourceRoot, "example.jar");
        Directory.CreateDirectory(resourceRoot);
        File.WriteAllText(source, "old");
        var entry = ResourceRecycleBinService.RecycleMany(resourceRoot, recycleRoot, [source]).Single();
        File.WriteAllText(source, "new");

        Assert.ThrowsExactly<IOException>(() => ResourceRecycleBinService.Restore(resourceRoot, recycleRoot, entry.Id));
        Assert.AreEqual("new", File.ReadAllText(source));
        Assert.AreEqual(1, ResourceRecycleBinService.List(resourceRoot, recycleRoot).Count);
    }

    [TestMethod]
    public void Recycle_RejectsPathOutsideResourceRoot()
    {
        var resourceRoot = Path.Combine(_root, "game", "mods");
        var recycleRoot = Path.Combine(_root, "game", "PCL", "ResourceRecycle", "Mod");
        var outside = Path.Combine(_root, "outside.jar");
        Directory.CreateDirectory(resourceRoot);
        File.WriteAllText(outside, "keep");

        Assert.ThrowsExactly<IOException>(() => ResourceRecycleBinService.RecycleMany(resourceRoot, recycleRoot, [outside]));
        Assert.AreEqual("keep", File.ReadAllText(outside));
    }

    [TestMethod]
    public void Purge_PermanentlyRemovesRecycledContent()
    {
        var resourceRoot = Path.Combine(_root, "game", "mods");
        var recycleRoot = Path.Combine(_root, "game", "PCL", "ResourceRecycle", "Mod");
        var source = Path.Combine(resourceRoot, "example.jar");
        Directory.CreateDirectory(resourceRoot);
        File.WriteAllText(source, "old");
        var entry = ResourceRecycleBinService.RecycleMany(resourceRoot, recycleRoot, [source]).Single();

        ResourceRecycleBinService.Purge(resourceRoot, recycleRoot, entry.Id);

        Assert.AreEqual(0, ResourceRecycleBinService.List(resourceRoot, recycleRoot).Count);
        Assert.IsFalse(File.Exists(source));
    }
}
