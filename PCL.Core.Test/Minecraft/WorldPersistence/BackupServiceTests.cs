using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Backups;

namespace PCL.Core.Test.Minecraft.Backups;

[TestClass]
public sealed class BackupServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void WorldBackup_RestoresExactWorldFilesAndKeepsOtherWorldsUntouched()
    {
        var world = Path.Combine(_root, "game", "saves", "World A");
        var otherWorld = Path.Combine(_root, "game", "saves", "World B");
        var backups = Path.Combine(_root, "backups");
        Write(Path.Combine(world, "level.dat"), "level-v1");
        Write(Path.Combine(world, "region", "r.0.0.mca"), "regions-v1");
        Write(Path.Combine(otherWorld, "level.dat"), "other-world");

        var snapshot = WorldBackupService.Create(world, backups, "before update");
        Write(Path.Combine(world, "level.dat"), "level-v2");
        Write(Path.Combine(world, "region", "r.0.0.mca"), "regions-v2");
        Write(Path.Combine(world, "new.dat"), "new-file");

        WorldBackupService.Restore(backups, snapshot.Id, world);

        Assert.AreEqual("level-v1", File.ReadAllText(Path.Combine(world, "level.dat")));
        Assert.AreEqual("regions-v1", File.ReadAllText(Path.Combine(world, "region", "r.0.0.mca")));
        Assert.IsFalse(File.Exists(Path.Combine(world, "new.dat")));
        Assert.AreEqual("other-world", File.ReadAllText(Path.Combine(otherWorld, "level.dat")));
    }

    [TestMethod]
    public void WorldBackup_CompareCurrentDetectsWorldChangesAndIgnoresSessionLock()
    {
        var world = Path.Combine(_root, "saves", "World");
        var backups = Path.Combine(_root, "backups");
        Write(Path.Combine(world, "level.dat"), "level-v1");
        Write(Path.Combine(world, "region", "r.0.0.mca"), "region-v1");
        var snapshot = WorldBackupService.Create(world, backups, "before play");

        Write(Path.Combine(world, "level.dat"), "level-v2");
        Write(Path.Combine(world, "region", "new.mca"), "new-region");
        Write(Path.Combine(world, "session.lock"), "volatile lock");

        var differences = WorldBackupService.CompareCurrent(backups, snapshot.Id, world);

        Assert.AreEqual(1, differences.Count(item => item.Kind == BackupDifferenceKind.Added));
        Assert.AreEqual(1, differences.Count(item => item.Kind == BackupDifferenceKind.Modified));
        Assert.IsFalse(differences.Any(item => item.Path == "session.lock"));
        Assert.IsFalse(differences.Any(item => item.Kind == BackupDifferenceKind.Removed));
    }

    [TestMethod]
    public void WorldBackup_RetentionRemovesOldestSnapshotsOnlyForSelectedWorld()
    {
        var worldA = Path.Combine(_root, "saves", "A");
        var worldB = Path.Combine(_root, "saves", "B");
        var backups = Path.Combine(_root, "backups");
        Write(Path.Combine(worldA, "level.dat"), "a");
        Write(Path.Combine(worldB, "level.dat"), "b");

        var firstA = WorldBackupService.Create(worldA, backups, "first");
        var secondA = WorldBackupService.Create(worldA, backups, "second");
        var onlyB = WorldBackupService.Create(worldB, backups, "other");
        WorldBackupService.ApplyRetention(backups, worldA, 1);

        Assert.AreEqual(1, WorldBackupService.List(backups, worldA).Count);
        Assert.AreEqual(secondA.Id, WorldBackupService.List(backups, worldA)[0].Id);
        Assert.AreEqual(1, WorldBackupService.List(backups, worldB).Count);
        Assert.AreEqual(onlyB.Id, WorldBackupService.List(backups, worldB)[0].Id);
        Assert.IsFalse(File.Exists(Path.Combine(backups, firstA.Id + ".zip")));
    }

    [TestMethod]
    public void EnvironmentRestorePoint_RestoresEnvironmentButPreservesCurrentSaves()
    {
        var instance = Path.Combine(_root, "instance");
        var restorePoints = Path.Combine(_root, "restore-points");
        Write(Path.Combine(instance, "mods", "a.jar"), "mod-v1");
        Write(Path.Combine(instance, "config", "options.json"), "config-v1");
        Write(Path.Combine(instance, "saves", "World", "level.dat"), "world-progress-v1");
        Write(Path.Combine(instance, "PCLLiteData", "WorldBackups", "existing.zip"), "backup-v1");
        var point = EnvironmentRestorePointService.Create(instance, restorePoints, "before pack update");
        Assert.IsFalse(point.Files.Any(file => file.Path.StartsWith("PCLLiteData/", StringComparison.OrdinalIgnoreCase)));

        Write(Path.Combine(instance, "mods", "a.jar"), "mod-v2");
        Write(Path.Combine(instance, "mods", "new.jar"), "new-mod");
        Write(Path.Combine(instance, "config", "options.json"), "config-v2");
        Write(Path.Combine(instance, "saves", "World", "level.dat"), "world-progress-v2");
        Write(Path.Combine(instance, "PCLLiteData", "WorldBackups", "existing.zip"), "backup-v2");
        Write(Path.Combine(instance, "PCLLiteData", "new-setting.json"), "keep-current");

        EnvironmentRestorePointService.Restore(restorePoints, point.Id, instance);

        Assert.AreEqual("mod-v1", File.ReadAllText(Path.Combine(instance, "mods", "a.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(instance, "mods", "new.jar")));
        Assert.AreEqual("config-v1", File.ReadAllText(Path.Combine(instance, "config", "options.json")));
        Assert.AreEqual("world-progress-v2", File.ReadAllText(Path.Combine(instance, "saves", "World", "level.dat")));
        Assert.AreEqual("backup-v2", File.ReadAllText(Path.Combine(instance, "PCLLiteData", "WorldBackups", "existing.zip")));
        Assert.AreEqual("keep-current", File.ReadAllText(Path.Combine(instance, "PCLLiteData", "new-setting.json")));
    }
    [TestMethod]
    public void EnvironmentRestorePoint_CompareCurrentReportsAddedModifiedAndRemovedFilesButIgnoresSaves()
    {
        var instance = Path.Combine(_root, "instance");
        var restorePoints = Path.Combine(_root, "restore-points");
        Write(Path.Combine(instance, "mods", "changed.jar"), "old");
        Write(Path.Combine(instance, "mods", "removed.jar"), "old");
        Write(Path.Combine(instance, "config", "same.json"), "same");
        Write(Path.Combine(instance, "saves", "World", "level.dat"), "world-v1");
        var point = EnvironmentRestorePointService.Create(instance, restorePoints, "before crash");

        Write(Path.Combine(instance, "mods", "changed.jar"), "new");
        File.Delete(Path.Combine(instance, "mods", "removed.jar"));
        Write(Path.Combine(instance, "mods", "added.jar"), "new");
        Write(Path.Combine(instance, "saves", "World", "level.dat"), "world-v2");

        var differences = EnvironmentRestorePointService.CompareCurrent(restorePoints, point.Id, instance);

        Assert.AreEqual(1, differences.Count(item => item.Kind == BackupDifferenceKind.Added));
        Assert.AreEqual(1, differences.Count(item => item.Kind == BackupDifferenceKind.Modified));
        Assert.AreEqual(1, differences.Count(item => item.Kind == BackupDifferenceKind.Removed));
        Assert.IsFalse(differences.Any(item => item.Path.StartsWith("saves/", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void CorruptBackupIsRejectedWithoutChangingDestination()
    {
        var world = Path.Combine(_root, "saves", "World");
        var backups = Path.Combine(_root, "backups");
        Write(Path.Combine(world, "level.dat"), "safe");
        var snapshot = WorldBackupService.Create(world, backups, "snapshot");
        Write(Path.Combine(world, "level.dat"), "current");
        File.WriteAllText(Path.Combine(backups, snapshot.Id + ".zip"), "corrupt");

        Assert.ThrowsExactly<InvalidDataException>(() => WorldBackupService.Restore(backups, snapshot.Id, world));
        Assert.AreEqual("current", File.ReadAllText(Path.Combine(world, "level.dat")));
    }

    private static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }
}
