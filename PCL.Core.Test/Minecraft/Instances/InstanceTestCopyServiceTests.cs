using System;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Instances;

namespace PCL.Core.Test.Minecraft.Instances;

[TestClass]
public sealed class InstanceTestCopyServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLInstanceCopyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void Create_ClonesRunnableInstanceRenamesRootFilesAndOmitsLauncherLocalData()
    {
        var versions = Path.Combine(_root, "versions");
        var source = Path.Combine(versions, "Pack");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Pack.json"), "{\"id\":\"Pack\",\"inheritsFrom\":\"1.20.1\"}");
        File.WriteAllText(Path.Combine(source, "Pack.jar"), "jar-bytes");
        File.WriteAllText(Path.Combine(source, "mods.jar"), "other-file");
        Write(Path.Combine(source, "PCLLiteData", "restore.json"), "launcher-data");
        Write(Path.Combine(source, "config", "settings.json"), "settings");

        var clone = InstanceTestCopyService.Create(source, Path.Combine(versions, "Pack Test"), "Pack", "Pack Test");

        Assert.AreEqual(Path.Combine(versions, "Pack Test"), clone);
        Assert.AreEqual("jar-bytes", File.ReadAllText(Path.Combine(clone, "Pack Test.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(clone, "Pack.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(clone, "Pack.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(clone, "PCLLiteData")));
        Assert.AreEqual("settings", File.ReadAllText(Path.Combine(clone, "config", "settings.json")));
        Assert.AreEqual("Pack Test", JsonNode.Parse(File.ReadAllText(Path.Combine(clone, "Pack Test.json")))!["id"]!.GetValue<string>());
        Assert.AreEqual("Pack", JsonNode.Parse(File.ReadAllText(Path.Combine(source, "Pack.json")))!["id"]!.GetValue<string>());
    }

    [TestMethod]
    public void Create_RejectsExistingDestinationWithoutChangingSource()
    {
        var versions = Path.Combine(_root, "versions");
        var source = Path.Combine(versions, "Pack");
        var destination = Path.Combine(versions, "Pack Test");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "Pack.json"), "{\"id\":\"Pack\"}");

        Assert.ThrowsExactly<IOException>(() => InstanceTestCopyService.Create(source, destination, "Pack", "Pack Test"));
        Assert.AreEqual("Pack", JsonNode.Parse(File.ReadAllText(Path.Combine(source, "Pack.json")))!["id"]!.GetValue<string>());
    }
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
