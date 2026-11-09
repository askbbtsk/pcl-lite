using System.IO;
using System.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Backups;

namespace PCL;

/// <summary>UI host helpers for per-world snapshots. Game-running checks and UI dispatch stay in the launcher.</summary>
internal static class ModWorldBackups
{
    private const string BackupFolderName = "PCLLiteData";

    private static string _GameDataRootFromSaves(string savesPath)
    {
        var saves = new DirectoryInfo(Path.GetFullPath(savesPath));
        if (!string.Equals(saves.Name, "saves", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected the Minecraft saves directory.");
        var gameRoot = saves.Parent ?? throw new InvalidDataException("Saves directory has no game root.");
        return Path.Combine(gameRoot.FullName, BackupFolderName);
    }

    /// <summary>Storage root for the effective game directory (the launcher's PathIndie value).</summary>
    public static string BackupRootForInstance(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        return Path.Combine(Path.GetFullPath(gameDirectory), BackupFolderName, "WorldBackups");
    }

    public static string BackupRootForInstance(McInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return BackupRootForInstance(instance.PathIndie);
    }

    private static string RestorePointRoot
        => Path.Combine(Paths.SharedLocalData, "Minecraft", "PackRestorePoints");

    public static IReadOnlyList<BackupSnapshot> ListRestorePoints(string instanceDirectory)
        => EnvironmentRestorePointService.List(RestorePointRoot, instanceDirectory);

    public static IReadOnlyList<BackupDifference> CompareRestorePoint(string instanceDirectory, string snapshotId)
    {
        EnsureInstanceOffline(instanceDirectory);
        return EnvironmentRestorePointService.CompareCurrent(RestorePointRoot, snapshotId, instanceDirectory);
    }

    public static BackupSnapshot CreateRestorePoint(string instanceDirectory, string reason)
    {
        EnsureInstanceOffline(instanceDirectory);
        var snapshot = EnvironmentRestorePointService.Create(instanceDirectory, RestorePointRoot, reason,
            retainFromAutomaticRetention: true);
        return snapshot;
    }

    public static void RestoreRestorePoint(string instanceDirectory, string snapshotId)
    {
        EnsureInstanceOffline(instanceDirectory);
        EnvironmentRestorePointService.Restore(RestorePointRoot, snapshotId, instanceDirectory);
    }

    public static bool DeleteRestorePoint(string instanceDirectory, string snapshotId)
    {
        EnsureInstanceOffline(instanceDirectory);
        return EnvironmentRestorePointService.Delete(RestorePointRoot, snapshotId, instanceDirectory);
    }

    private static string _BackupRoot(string worldPath)
    {
        var world = Path.GetFullPath(worldPath);
        var savesDirectory = Directory.GetParent(world)
            ?? throw new InvalidDataException("World path has no saves directory.");
        return Path.Combine(_GameDataRootFromSaves(savesDirectory.FullName), "WorldBackups");
    }

    public static IReadOnlyList<BackupSnapshot> List(string worldPath)
        => WorldBackupService.List(_BackupRoot(worldPath), worldPath);

    public static BackupSnapshot CreateManual(string instancePath, string worldPath)
    {
        EnsureWorldOffline(instancePath);
        var root = _BackupRoot(worldPath);
        var snapshot = WorldBackupService.Create(worldPath, root, "Manual backup", retainFromAutomaticRetention: true);
        return snapshot;
    }

    public static void Restore(string instancePath, string worldPath, string snapshotId)
    {
        EnsureWorldOffline(instancePath);
        WorldBackupService.Restore(_BackupRoot(worldPath), snapshotId, worldPath);
    }

    public static bool Delete(string worldPath, string snapshotId)
        => WorldBackupService.Delete(_BackupRoot(worldPath), snapshotId, worldPath, path =>
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Config.Launch.DeleteToRecycleBin ? Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin
                    : Microsoft.VisualBasic.FileIO.RecycleOption.DeletePermanently));

    public static string DescribeReason(BackupSnapshot snapshot)
    {
        var reason = snapshot.Reason?.Trim() ?? "";
        if (reason.Equals("Manual restore point", StringComparison.OrdinalIgnoreCase))
            return Lang.Text("Instance.Backup.Reason.ManualRestorePoint");
        if (reason.Equals("Manual backup", StringComparison.OrdinalIgnoreCase))
            return Lang.Text("Instance.Backup.Reason.Manual");
        if (reason.Length == 0 || reason.StartsWith("Automatic", StringComparison.OrdinalIgnoreCase)
            || reason.StartsWith("before pack update", StringComparison.OrdinalIgnoreCase))
            return Lang.Text("Instance.Backup.Reason.Normal");
        return reason;
    }

    public static string Text(string id, params object?[] args)
    {
        var key = id switch
        {
            "card" => "Instance.Saves.Backup.Card.Title",
            "button" => "Instance.Saves.Backup.Button.Tooltip",
            "create" => "Instance.Saves.Backup.Create",
            "empty" => "Instance.Saves.Backup.Empty",
            "restore" => "Instance.Saves.Backup.Restore",
            "delete" => "Instance.Saves.Backup.Delete",
            "created" => "Instance.Saves.Backup.Created",
            "restored" => "Instance.Saves.Backup.Restored",
            "busy" => "Instance.Saves.Backup.GameRunning",
            "failed" => "Instance.Saves.Backup.OperationFailed",
            "confirmRestore" => "Instance.Saves.Backup.ConfirmRestore",
            _ => id,
        };
        return args.Length == 0 ? Lang.Text(key) : Lang.Text(key, args);
    }

    private static void EnsureWorldOffline(string instancePath)
    {
        var normalized = Path.GetFullPath(instancePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var running = ModWatcher.mcWatcherList.Any(watcher =>
            (watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running)
            && watcher.version is not null
            && (string.Equals(Path.GetFullPath(watcher.version.PathInstance).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFullPath(watcher.version.PathIndie).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    normalized, StringComparison.OrdinalIgnoreCase)));
        if (running) throw new InvalidOperationException(Text("busy"));
    }

    private static void EnsureInstanceOffline(string instancePath)
    {
        var normalized = Path.GetFullPath(instancePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var running = ModWatcher.mcWatcherList.Any(watcher =>
            (watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running)
            && watcher.version is not null
            && string.Equals(Path.GetFullPath(watcher.version.PathInstance).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalized, StringComparison.OrdinalIgnoreCase));
        if (running) throw new InvalidOperationException(Text("busy"));
    }
}

