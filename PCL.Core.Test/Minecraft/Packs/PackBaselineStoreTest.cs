using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Test.Minecraft.Packs;

[TestClass]
public sealed class PackBaselineStoreTest
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLPackBaselineTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void CaptureSaveLoad_RetainsPackIdentityAndFileHashesOutsideGameRoot()
    {
        var game = Path.Combine(_root, "versions", "pack");
        var storeRoot = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        File.WriteAllText(Path.Combine(game, "mods", "a.jar"), "mod-bytes");
        var store = new PackBaselineStore(storeRoot);
        var baseline = store.Capture(game, ["mods/a.jar", "mods/missing.jar"], "Modrinth", "project-id", "version-id");

        store.Save(game, baseline);
        var loaded = store.Load(game);

        Assert.IsNotNull(loaded);
        Assert.AreEqual("Modrinth", loaded.Source);
        Assert.AreEqual("project-id", loaded.ProjectId);
        Assert.AreEqual("version-id", loaded.VersionId);
        Assert.AreEqual(PackUpdatePlanner.HashFile(Path.Combine(game, "mods", "a.jar")), loaded.Files["mods/a.jar"].Sha256);
        Assert.IsFalse(File.Exists(Path.Combine(game, "PackBaseline.json")));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(storeRoot, "PackBaselines"), "*.json").Length);
    }

    [TestMethod]
    public void Delete_RemovesOnlySelectedInstanceBaseline()
    {
        var first = Path.Combine(_root, "versions", "first");
        var second = Path.Combine(_root, "versions", "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var store = new PackBaselineStore(Path.Combine(_root, "appdata"));
        store.Save(first, store.Capture(first, Array.Empty<string>(), "CurseForge", "one", "1"));
        store.Save(second, store.Capture(second, Array.Empty<string>(), "CurseForge", "two", "2"));

        Assert.IsTrue(store.Delete(first));
        Assert.IsNull(store.Load(first));
        Assert.AreEqual("two", store.Load(second)?.ProjectId);
    }
}
