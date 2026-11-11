using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.InstanceMigration;

namespace PCL.Core.Test.Minecraft.InstanceMigration;

[TestClass]
public sealed class InstanceModificationTests
{
    private string _root = null!;
    private string _instance = null!;
    private string _game = null!;
    private string _stage = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLModificationTests", Guid.NewGuid().ToString("N"));
        _instance = Path.Combine(_root, "versions", "original"); _game = Path.Combine(_root, "shared"); _stage = Path.Combine(_root, "stage");
        Directory.CreateDirectory(_instance); Directory.CreateDirectory(Path.Combine(_game, "mods")); Directory.CreateDirectory(_stage);
        File.WriteAllText(Path.Combine(_instance, "original.json"), "original-json");
        File.WriteAllText(Path.Combine(_instance, "Setup.ini"), "user-java-and-config");
        Directory.CreateDirectory(Path.Combine(_game, "saves")); File.WriteAllText(Path.Combine(_game, "saves", "world.dat"), "world");
        File.WriteAllText(Path.Combine(_stage, "target.json"), "target-json");
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [TestMethod]
    public void InPlaceCoreAndModReplacementPreserveOriginalBytesDisabledAndUserConfiguration()
    {
        var source = Path.Combine(_game, "mods", "fabric.jar"); File.WriteAllText(source, "original-mod");
        var payload = Path.Combine(_stage, "neoforge.jar"); File.WriteAllText(payload, "target-mod");
        var plan = InstanceModificationTransaction.Prepare(_instance, _game, CorePayload(),
            [new(source, Hash(source), payload, "neoforge.jar")]);
        Assert.AreEqual("fabric.jar.disabled", plan.RetiredFiles.Single());
        InstanceModificationTransaction.Apply(_instance, _game, plan);
        Assert.AreEqual("target-json", File.ReadAllText(Path.Combine(_instance, "original.json")));
        Assert.AreEqual("original-mod", File.ReadAllText(source + ".disabled"));
        Assert.IsFalse(File.Exists(source));
        Assert.AreEqual("target-mod", File.ReadAllText(Path.Combine(_game, "mods", "neoforge.jar")));
        Assert.AreEqual("user-java-and-config", File.ReadAllText(Path.Combine(_instance, "Setup.ini")));
        Assert.AreEqual("world", File.ReadAllText(Path.Combine(_game, "saves", "world.dat")));
    }

    [TestMethod]
    public void SameFilenameReplacementArchivesOldBytesBeforeReplacing()
    {
        var source = Path.Combine(_game, "mods", "mod.jar"); File.WriteAllText(source, "original");
        var payload = Path.Combine(_stage, "mod.jar"); File.WriteAllText(payload, "new");
        var plan = InstanceModificationTransaction.Prepare(_instance, _game, CorePayload(), [new(source, Hash(source), payload, "mod.jar")]);
        InstanceModificationTransaction.Apply(_instance, _game, plan);
        Assert.AreEqual("new", File.ReadAllText(source)); Assert.AreEqual("original", File.ReadAllText(source + ".disabled"));
    }

    [TestMethod]
    public void PreviouslyDisabledReplacementNeverEnablesTheNewBuild()
    {
        var source = Path.Combine(_game, "mods", "mod.jar.disabled"); File.WriteAllText(source, "disabled-original");
        var payload = Path.Combine(_stage, "new.jar.disabled"); File.WriteAllText(payload, "new-disabled");
        var plan = InstanceModificationTransaction.Prepare(_instance, _game, CorePayload(), [new(source, Hash(source), payload, "new.jar.disabled")]);
        InstanceModificationTransaction.Apply(_instance, _game, plan);
        Assert.AreEqual("new-disabled", File.ReadAllText(Path.Combine(_game, "mods", "new.jar.disabled")));
        Assert.AreEqual("disabled-original", File.ReadAllText(Path.Combine(_game, "mods", plan.RetiredFiles.Single())));
        Assert.IsFalse(File.Exists(Path.Combine(_game, "mods", "new.jar")));
    }

    [TestMethod]
    public void ExistingDifferingTargetOrDisabledArchiveRejectsWithoutChangingSource()
    {
        var source = Path.Combine(_game, "mods", "mod.jar"); File.WriteAllText(source, "original");
        var payload = Path.Combine(_stage, "new.jar"); File.WriteAllText(payload, "new");
        File.WriteAllText(source + ".disabled", "unrelated-disabled");
        Assert.ThrowsExactly<IOException>(() => InstanceModificationTransaction.Prepare(_instance, _game, CorePayload(), [new(source, Hash(source), payload, "new.jar")]));
        Assert.AreEqual("original", File.ReadAllText(source)); Assert.AreEqual("original-json", File.ReadAllText(Path.Combine(_instance, "original.json")));
        File.Delete(source + ".disabled"); File.WriteAllText(Path.Combine(_game, "mods", "new.jar"), "user-file");
        Assert.ThrowsExactly<IOException>(() => InstanceModificationTransaction.Prepare(_instance, _game, CorePayload(), [new(source, Hash(source), payload, "new.jar")]));
        Assert.AreEqual("user-file", File.ReadAllText(Path.Combine(_game, "mods", "new.jar")));
    }

    [TestMethod]
    public void CancellationAndPreviewStalenessNeverSwitchCoreOrMods()
    {
        var source = Path.Combine(_game, "mods", "mod.jar"); File.WriteAllText(source, "original");
        var payload = Path.Combine(_stage, "new.jar"); File.WriteAllText(payload, "new");
        var plan = InstanceModificationTransaction.Prepare(_instance, _game, CorePayload(), [new(source, Hash(source), payload, "new.jar")]);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => InstanceModificationTransaction.Apply(_instance, _game, plan, cancel.Token));
        Assert.AreEqual("original", File.ReadAllText(source)); Assert.AreEqual("original-json", File.ReadAllText(Path.Combine(_instance, "original.json")));
        File.WriteAllText(source, "external-change");
        Assert.ThrowsExactly<IOException>(() => InstanceModificationTransaction.Apply(_instance, _game, plan));
        Assert.AreEqual("external-change", File.ReadAllText(source)); Assert.AreEqual("original-json", File.ReadAllText(Path.Combine(_instance, "original.json")));
        Assert.IsFalse(File.Exists(Path.Combine(_game, "mods", "new.jar")));
    }

    [TestMethod]
    public void NoMigrationLeavesAllModsAndSavesAloneWhileOnlyCoreChanges()
    {
        var mod = Path.Combine(_game, "mods", "unknown.jar"); File.WriteAllText(mod, "local-user-mod");
        var plan = InstanceModificationTransaction.Prepare(_instance, _game, CorePayload(), []);
        InstanceModificationTransaction.Apply(_instance, _game, plan);
        Assert.AreEqual("target-json", File.ReadAllText(Path.Combine(_instance, "original.json")));
        Assert.AreEqual("local-user-mod", File.ReadAllText(mod)); Assert.AreEqual(0, plan.RetiredFiles.Count);
        Assert.AreEqual("world", File.ReadAllText(Path.Combine(_game, "saves", "world.dat")));
    }

    private IReadOnlyList<ModificationPayload> CorePayload() => [new("original.json", Path.Combine(_stage, "target.json"), Hash(Path.Combine(_instance, "original.json")), true)];

    [TestMethod]
    public void SameLoaderCoreUpgradeAndApiReplacementAreOneRecoverablePlan()
    {
        var api = Path.Combine(_game, "mods", "fabric-api-old.jar");
        var fresh = Path.Combine(_stage, "fabric-api-new.jar");
        File.WriteAllText(api, "old-api-bytes");
        File.WriteAllText(fresh, "new-api-bytes");
        var plan = InstanceModificationTransaction.Prepare(_instance, _game, CorePayload(),
            [new(api, Hash(api), fresh, "fabric-api-new.jar")]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            InstanceModificationTransaction.Apply(_instance, _game, plan, cancelled.Token));
        Assert.AreEqual("old-api-bytes", File.ReadAllText(api));
        Assert.AreEqual("original-json", File.ReadAllText(Path.Combine(_instance, "original.json")));
        InstanceModificationTransaction.Apply(_instance, _game, plan);
        Assert.AreEqual("old-api-bytes", File.ReadAllText(api + ".disabled"));
        Assert.AreEqual("new-api-bytes", File.ReadAllText(Path.Combine(_game, "mods", "fabric-api-new.jar")));
        Assert.AreEqual("target-json", File.ReadAllText(Path.Combine(_instance, "original.json")));
    }

    private static string Hash(string file) => InstanceModificationTransaction.HashOrMissing(file)!;
}
