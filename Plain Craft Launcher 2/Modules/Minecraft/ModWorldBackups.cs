using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Backups;

namespace PCL;

/// <summary>UI host helpers for per-world snapshots. Game-running checks and UI dispatch stay in the launcher.</summary>
internal static class ModWorldBackups
{
    private const string BackupFolderName = "PCLLiteData";
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, string> _launchRestorePoints = new(StringComparer.OrdinalIgnoreCase);

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
        EnvironmentRestorePointService.ApplyRetention(RestorePointRoot, instanceDirectory, 10);
        return snapshot;
    }

    /// <summary>Creates a pre-launch restore point for an isolated instance, skipping a duplicate unchanged snapshot.</summary>
    public static BackupSnapshot? CreateAutomaticEnvironmentRestorePoint(McInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var instanceDirectory = Path.GetFullPath(instance.PathInstance).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gameDirectory = Path.GetFullPath(instance.PathIndie).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(instanceDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase)) return null;

        var latest = ListRestorePoints(instanceDirectory).FirstOrDefault(static item => !item.RetainFromAutomaticRetention);
        if (latest is not null && EnvironmentRestorePointService.CompareCurrent(RestorePointRoot, latest.Id, instanceDirectory).Count == 0)
            return latest;
        var snapshot = EnvironmentRestorePointService.Create(instanceDirectory, RestorePointRoot,
            "Automatic restore point before game launch");
        EnvironmentRestorePointService.ApplyRetention(RestorePointRoot, instanceDirectory, 10);
        return snapshot;
    }

    public static void RegisterLaunchRestorePoint(string instancePath, string snapshotId)
    {
        if (!string.IsNullOrWhiteSpace(instancePath) && !string.IsNullOrWhiteSpace(snapshotId))
            _launchRestorePoints[Path.GetFullPath(instancePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)] = snapshotId;
    }

    public static void CompleteNormalLaunch(string instancePath)
    {
        if (!string.IsNullOrWhiteSpace(instancePath))
            _launchRestorePoints.TryRemove(Path.GetFullPath(instancePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), out _);
    }

    /// <summary>After crash analysis completes, offers a file-level comparison and restores the pre-launch point on explicit confirmation.</summary>
    public static void OfferCrashRecovery(string instancePath, string instanceName)
    {
        var target = Path.GetFullPath(instancePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!_launchRestorePoints.TryRemove(target, out var snapshotId)) return;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var differences = EnvironmentRestorePointService.CompareCurrent(RestorePointRoot, snapshotId, target);
                if (differences.Count == 0) return;
                var added = differences.Count(item => item.Kind == BackupDifferenceKind.Added);
                var modified = differences.Count(item => item.Kind == BackupDifferenceKind.Modified);
                var removed = differences.Count(item => item.Kind == BackupDifferenceKind.Removed);
                var lines = differences.Take(18).Select(item => Lang.Text("Instance.RestorePoint.Compare.Entry",
                    item.Kind switch
                    {
                        BackupDifferenceKind.Added => Lang.Text("Instance.RestorePoint.Compare.Added"),
                        BackupDifferenceKind.Modified => Lang.Text("Instance.RestorePoint.Compare.Modified"),
                        _ => Lang.Text("Instance.RestorePoint.Compare.Removed")
                    }, item.Path));
                var message = Lang.Text("Instance.RestorePoint.CrashRecovery.Message", instanceName, added, modified, removed)
                    + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines)
                    + (differences.Count > 18 ? Environment.NewLine + Lang.Text("Instance.RestorePoint.Compare.More", differences.Count - 18) : "");
                var restore = false;
                ModBase.RunInUiWait(() => restore = ModMain.MyMsgBox(message,
                    Lang.Text("Instance.RestorePoint.CrashRecovery.Title"),
                    Lang.Text("Instance.RestorePoint.CrashRecovery.Restore"),
                    Lang.Text("Common.Action.Cancel"), isWarn: true) == 1);
                if (!restore) return;

                RestoreRestorePoint(target, snapshotId);
                ModBase.RunInUiWait(() => HintService.Hint(Lang.Text("Instance.RestorePoint.Restored"), HintType.Success));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "崩溃后对比或恢复实例环境失败", ModBase.LogLevel.Hint);
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Instance.RestorePoint.Failed"), HintType.Error));
            }
        }, "Crash Environment Recovery");
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

    public static string DeletedBackupRoot(string worldPath)
        => Path.Combine(_BackupRoot(worldPath), "deleted");

    private static string _BackupRoot(string worldPath)
    {
        var world = Path.GetFullPath(worldPath);
        var savesDirectory = Directory.GetParent(world)
            ?? throw new InvalidDataException("World path has no saves directory.");
        return Path.Combine(_GameDataRootFromSaves(savesDirectory.FullName), "WorldBackups");
    }

    public sealed record Preferences(bool AutomaticEnabled, int RetentionCount);

    public static IReadOnlyList<BackupSnapshot> List(string worldPath)
        => WorldBackupService.List(_BackupRoot(worldPath), worldPath);

    public static Preferences GetPreferences(string instancePath, string savesPath)
    {
        var path = _PreferencesPath(instancePath, savesPath);
        if (!File.Exists(path))
            return new Preferences(Config.Launch.WorldBackupAutoEnabled,
                Math.Clamp(Config.Launch.WorldBackupRetention, 1, 50));
        try
        {
            var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path), _jsonOptions);
            return value is { RetentionCount: >= 1 and <= 50 } ? value : LegacyPreferences();
        }
        catch
        {
            return LegacyPreferences();
        }
    }

    private static Preferences LegacyPreferences()
        => new(Config.Launch.WorldBackupAutoEnabled, Math.Clamp(Config.Launch.WorldBackupRetention, 1, 50));

    public static string PreferencesPathForInstance(string instancePath, string savesPath)
        => _PreferencesPath(instancePath, savesPath);

    public static void SavePreferences(string instancePath, string savesPath, Preferences preferences)
    {
        if (preferences.RetentionCount is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(preferences));
        var path = _PreferencesPath(instancePath, savesPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, _jsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static BackupSnapshot CreateManual(string instancePath, string worldPath)
    {
        EnsureWorldOffline(instancePath);
        var root = _BackupRoot(worldPath);
        var snapshot = WorldBackupService.Create(worldPath, root, "Manual backup", retainFromAutomaticRetention: true);
        WorldBackupService.ApplyRetention(root, worldPath,
            GetPreferences(instancePath, Path.GetDirectoryName(Path.GetFullPath(worldPath))!).RetentionCount);
        return snapshot;
    }

    /// <summary>Creates only missing or changed world snapshots before a normal game launch.</summary>
    public static IReadOnlyList<BackupSnapshot> CreateAutomaticBeforeAllWorlds(string instancePath, string savesPath)
    {
        var preferences = GetPreferences(instancePath, savesPath);
        if (!preferences.AutomaticEnabled || !Directory.Exists(savesPath)) return Array.Empty<BackupSnapshot>();
        EnsureWorldOffline(instancePath);
        var snapshots = new List<BackupSnapshot>();
        foreach (var worldPath in _EnumerateValidWorlds(savesPath))
        {
            var root = _BackupRoot(worldPath);
            var latest = WorldBackupService.List(root, worldPath).FirstOrDefault();
            if (latest is not null)
            {
                try
                {
                    if (WorldBackupService.CompareCurrent(root, latest.Id, worldPath).Count == 0) continue;
                }
                catch (InvalidDataException ex)
                {
                    ModBase.Log(ex, "忽略损坏的世界备份，创建新备份", ModBase.LogLevel.Hint);
                }
            }
            var snapshot = WorldBackupService.Create(worldPath, root, "Automatic backup before game launch");
            WorldBackupService.ApplyRetention(root, worldPath, preferences.RetentionCount);
            snapshots.Add(snapshot);
        }
        return snapshots;
    }

    /// <summary>Call after all game processes for this instance have exited successfully.</summary>
    public static IReadOnlyList<BackupSnapshot> CreateAutomaticAfterExit(string instancePath, string savesPath)
    {
        var preferences = GetPreferences(instancePath, savesPath);
        if (!preferences.AutomaticEnabled || !Directory.Exists(savesPath)) return Array.Empty<BackupSnapshot>();
        EnsureWorldOffline(instancePath);
        var snapshots = new List<BackupSnapshot>();
        foreach (var worldPath in _EnumerateValidWorlds(savesPath))
        {
            var root = _BackupRoot(worldPath);
            var latest = WorldBackupService.List(root, worldPath).FirstOrDefault();
            if (latest is not null)
            {
                try
                {
                    if (WorldBackupService.CompareCurrent(root, latest.Id, worldPath).Count == 0) continue;
                }
                catch (InvalidDataException ex)
                {
                    ModBase.Log(ex, "忽略损坏的世界备份，创建新备份", ModBase.LogLevel.Hint);
                }
            }
            var snapshot = WorldBackupService.Create(worldPath, root, "Automatic backup after game exit");
            WorldBackupService.ApplyRetention(root, worldPath, preferences.RetentionCount);
            snapshots.Add(snapshot);
        }
        return snapshots;
    }

    private static IEnumerable<string> _EnumerateValidWorlds(string savesPath)
    {
        foreach (var worldPath in Directory.EnumerateDirectories(savesPath)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(worldPath) & FileAttributes.ReparsePoint) == 0
                && File.Exists(Path.Combine(worldPath, "level.dat")))
                yield return worldPath;
        }
    }

    public static void Restore(string instancePath, string worldPath, string snapshotId)
    {
        EnsureWorldOffline(instancePath);
        WorldBackupService.Restore(_BackupRoot(worldPath), snapshotId, worldPath);
    }

    public static bool Delete(string worldPath, string snapshotId)
        => WorldBackupService.Delete(_BackupRoot(worldPath), snapshotId, worldPath);

    private static string _PreferencesPath(string instancePath, string savesPath)
    {
        var normalized = Path.GetFullPath(instancePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return Path.Combine(_GameDataRootFromSaves(savesPath), "WorldBackupSettings", hash + ".json");
    }

    public static string Text(string id, params object?[] args)
    {
        var key = id switch
        {
            "settingsTitle" => "Instance.Saves.Backup.Settings.Title",
            "autoSetting" => "Instance.Saves.Backup.Settings.Automatic",
            "retention" => "Instance.Saves.Backup.Settings.Retention",
            "card" => "Instance.Saves.Backup.Card.Title",
            "button" => "Instance.Saves.Backup.Button.Tooltip",
            "create" => "Instance.Saves.Backup.Create",
            "empty" => "Instance.Saves.Backup.Empty",
            "restore" => "Instance.Saves.Backup.Restore",
            "delete" => "Instance.Saves.Backup.Delete",
            "created" => "Instance.Saves.Backup.Created",
            "restored" => "Instance.Saves.Backup.Restored",
            "deleted" => "Instance.Saves.Backup.Deleted",
            "busy" => "Instance.Saves.Backup.GameRunning",
            "failed" => "Instance.Saves.Backup.OperationFailed",
            "confirmRestore" => "Instance.Saves.Backup.ConfirmRestore",
            "confirmDelete" => "Instance.Saves.Backup.ConfirmDelete",
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

