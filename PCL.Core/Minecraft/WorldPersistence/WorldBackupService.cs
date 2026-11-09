using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace PCL.Core.Minecraft.Backups;

public enum BackupSnapshotKind
{
    World = 1,
    Environment = 2,
}

public sealed record BackupFileRecord
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public long Length { get; init; }
}

/// <summary>Lite-owned backup index. Archive bytes are independent and validated against every entry on restore.</summary>
public sealed record BackupSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required BackupSnapshotKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public required string SourceKey { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public string? Reason { get; init; }
    /// <summary>Manual snapshots survive automatic retention trimming.</summary>
    public bool RetainFromAutomaticRetention { get; init; }
    public required string ArchiveSha256 { get; init; }
    public long ArchiveLength { get; init; }
    public IReadOnlyList<BackupFileRecord> Files { get; init; } = Array.Empty<BackupFileRecord>();
}

public enum BackupDifferenceKind
{
    Added,
    Removed,
    Modified,
}

public sealed record BackupDifference(string Path, BackupDifferenceKind Kind);

/// <summary>
/// Safe archive primitives for world backups and environment restore points.
/// Callers must ensure the game is no longer writing to the source/destination tree.
/// </summary>
public static class WorldBackupService
{
    public static BackupSnapshot Create(string worldDirectory, string backupRoot, string? reason = null,
        CancellationToken cancellationToken = default, bool retainFromAutomaticRetention = false)
        => BackupArchive.Create(worldDirectory, backupRoot, BackupSnapshotKind.World, reason, excludeSaves: false,
            retainFromAutomaticRetention, cancellationToken);

    public static IReadOnlyList<BackupSnapshot> List(string backupRoot, string worldDirectory)
        => BackupArchive.List(backupRoot, BackupSnapshotKind.World, worldDirectory);

    public static IReadOnlyList<BackupDifference> CompareCurrent(string backupRoot, string snapshotId, string worldDirectory,
        CancellationToken cancellationToken = default)
        => BackupArchive.CompareCurrent(backupRoot, snapshotId, worldDirectory, BackupSnapshotKind.World,
            excludeSaves: false, preserveLauncherData: false, cancellationToken);

    public static void Restore(string backupRoot, string snapshotId, string worldDirectory,
        CancellationToken cancellationToken = default)
        => BackupArchive.Restore(backupRoot, snapshotId, worldDirectory, BackupSnapshotKind.World,
            preserveSaves: false, preserveLauncherData: false, cancellationToken);

    public static bool Delete(string backupRoot, string snapshotId, string worldDirectory)
        => BackupArchive.Delete(backupRoot, snapshotId, worldDirectory, BackupSnapshotKind.World);

    public static void ApplyRetention(string backupRoot, string worldDirectory, int maxSnapshots)
        => BackupArchive.ApplyRetention(backupRoot, BackupSnapshotKind.World, worldDirectory, maxSnapshots);
}

/// <summary>Restores launcher-managed instance files while leaving current saves untouched.</summary>
public static class EnvironmentRestorePointService
{
    public static BackupSnapshot Create(string instanceDirectory, string restorePointRoot, string? reason = null,
        CancellationToken cancellationToken = default, bool retainFromAutomaticRetention = false)
        => BackupArchive.Create(instanceDirectory, restorePointRoot, BackupSnapshotKind.Environment, reason, excludeSaves: true,
            retainFromAutomaticRetention, cancellationToken);

    public static IReadOnlyList<BackupSnapshot> List(string restorePointRoot, string instanceDirectory)
        => BackupArchive.List(restorePointRoot, BackupSnapshotKind.Environment, instanceDirectory);

    public static IReadOnlyList<BackupDifference> CompareCurrent(string restorePointRoot, string snapshotId,
        string instanceDirectory, CancellationToken cancellationToken = default)
        => BackupArchive.CompareCurrent(restorePointRoot, snapshotId, instanceDirectory, BackupSnapshotKind.Environment,
            excludeSaves: true, preserveLauncherData: true, cancellationToken);

    public static void Restore(string restorePointRoot, string snapshotId, string instanceDirectory,
        CancellationToken cancellationToken = default)
        => BackupArchive.Restore(restorePointRoot, snapshotId, instanceDirectory, BackupSnapshotKind.Environment,
            preserveSaves: true, preserveLauncherData: true, cancellationToken);

    public static bool Delete(string restorePointRoot, string snapshotId, string instanceDirectory)
        => BackupArchive.Delete(restorePointRoot, snapshotId, instanceDirectory, BackupSnapshotKind.Environment);

    public static void ApplyRetention(string restorePointRoot, string instanceDirectory, int maxSnapshots)
        => BackupArchive.ApplyRetention(restorePointRoot, BackupSnapshotKind.Environment, instanceDirectory, maxSnapshots);
}

internal static class BackupArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static BackupSnapshot Create(string sourcePath, string storePath, BackupSnapshotKind kind, string? reason,
        bool excludeSaves, bool retainFromAutomaticRetention, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        _RejectReparsePoint(source);
        var store = Path.GetFullPath(storePath);
        _EnsureSeparateTrees(source, store);
        Directory.CreateDirectory(store);
        _RejectReparsePoint(store);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(store, $".stage-{id}");
        Directory.CreateDirectory(staging);
        try
        {
            var archivePath = Path.Combine(staging, id + ".zip");
            var records = new List<BackupFileRecord>();
            using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var sourceFile in _EnumerateFiles(source, excludeSaves))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = _NormalizeRelative(Path.GetRelativePath(source, sourceFile));
                    _ValidateRelativePath(relative);
                    var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                    long length = 0;
                    using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1024 * 128];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        hash.AppendData(buffer, 0, read);
                        length += read;
                    }
                    records.Add(new BackupFileRecord
                    {
                        Path = relative,
                        Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                        Length = length,
                    });
                }
            }

            var snapshot = new BackupSnapshot
            {
                Id = id,
                Kind = kind,
                SourcePath = source,
                SourceKey = _SourceKey(source),
                CreatedUtc = DateTimeOffset.UtcNow,
                Reason = reason,
                RetainFromAutomaticRetention = retainFromAutomaticRetention,
                ArchiveSha256 = _HashFile(archivePath),
                ArchiveLength = new FileInfo(archivePath).Length,
                Files = records,
            };
            var metadataPath = Path.Combine(staging, id + ".json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(archivePath, Path.Combine(store, id + ".zip"));
            try
            {
                File.Move(metadataPath, Path.Combine(store, id + ".json"));
            }
            catch
            {
                File.Delete(Path.Combine(store, id + ".zip"));
                throw;
            }
            return snapshot;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static IReadOnlyList<BackupSnapshot> List(string storePath, BackupSnapshotKind kind, string sourcePath)
    {
        var store = Path.GetFullPath(storePath);
        if (!Directory.Exists(store)) return Array.Empty<BackupSnapshot>();
        var sourceKey = _SourceKey(Path.GetFullPath(sourcePath));
        return Directory.EnumerateFiles(store, "*.json", SearchOption.TopDirectoryOnly)
            .Select(_ReadManifest)
            .Where(snapshot => snapshot.Kind == kind && string.Equals(snapshot.SourceKey, sourceKey, StringComparison.Ordinal))
            .OrderByDescending(static snapshot => snapshot.CreatedUtc)
            .ToArray();
    }

    public static void Restore(string storePath, string snapshotId, string destinationPath, BackupSnapshotKind kind,
        bool preserveSaves, bool preserveLauncherData, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(snapshotId, "N", out _)) throw new InvalidDataException("Invalid backup snapshot identifier.");
        var store = Path.GetFullPath(storePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!Directory.Exists(store)) throw new DirectoryNotFoundException(store);
        var snapshot = _ReadManifest(Path.Combine(store, snapshotId + ".json"));
        if (snapshot.Id != snapshotId || snapshot.Kind != kind || snapshot.SourceKey != _SourceKey(destination))
            throw new InvalidDataException("The backup snapshot does not match this target directory.");
        var archivePath = Path.Combine(store, snapshotId + ".zip");
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length != snapshot.ArchiveLength
            || !HashEquals(_HashFile(archivePath), snapshot.ArchiveSha256))
            throw new InvalidDataException("The backup archive failed its integrity check.");

        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Target has no parent directory.");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, $".{Path.GetFileName(destination)}.restore-stage-{Guid.NewGuid():N}");
        var rollback = Path.Combine(parent, $".{Path.GetFileName(destination)}.restore-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var originalMoved = false;
        var stageCommitted = false;
        try
        {
            _ExtractAndVerify(archivePath, stage, snapshot, cancellationToken);
            if (preserveSaves && Directory.Exists(destination))
            {
                var sourceSaves = Path.Combine(destination, "saves");
                var stageSaves = Path.Combine(stage, "saves");
                if (Directory.Exists(sourceSaves)) _CopyDirectoryChecked(sourceSaves, stageSaves, cancellationToken);
            }

            if (preserveLauncherData && Directory.Exists(destination))
            {
                var launcherData = Path.Combine(destination, "PCLLiteData");
                if (Directory.Exists(launcherData))
                    _CopyDirectoryChecked(launcherData, Path.Combine(stage, "PCLLiteData"), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                _RejectReparsePoint(destination);
                Directory.Move(destination, rollback);
                originalMoved = true;
            }
            try
            {
                Directory.Move(stage, destination);
                stageCommitted = true;
            }
            catch
            {
                if (originalMoved && !Directory.Exists(destination))
                {
                    Directory.Move(rollback, destination);
                    originalMoved = false;
                }
                throw;
            }
        }
        catch (Exception operationError)
        {
            if (originalMoved && !Directory.Exists(destination))
            {
                try
                {
                    Directory.Move(rollback, destination);
                    originalMoved = false;
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("Backup restore failed and the original target could not be restored.", operationError, rollbackError);
                }
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); }
                catch { }
            }
            if (stageCommitted && originalMoved && Directory.Exists(rollback))
            {
                try { Directory.Delete(rollback, recursive: true); }
                catch { /* Restore succeeded; preserve an undeleted rollback copy for manual recovery. */ }
            }
        }
    }

    public static IReadOnlyList<BackupDifference> CompareCurrent(string storePath, string snapshotId,
        string sourcePath, BackupSnapshotKind kind, bool excludeSaves, bool preserveLauncherData,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        var snapshot = List(storePath, kind, source)
            .FirstOrDefault(item => string.Equals(item.Id, snapshotId, StringComparison.Ordinal));
        if (snapshot is null) throw new InvalidDataException("The restore point does not belong to this instance.");
        _RejectReparsePoint(source);
        var archivePath = Path.Combine(Path.GetFullPath(storePath), snapshot.Id + ".zip");
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length != snapshot.ArchiveLength
            || !HashEquals(_HashFile(archivePath), snapshot.ArchiveSha256))
            throw new InvalidDataException("The restore point archive failed its integrity check.");

        var expected = snapshot.Files.ToDictionary(static item => item.Path, StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _EnumerateFiles(source, excludeSaves))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = _NormalizeRelative(Path.GetRelativePath(source, file));
            if (preserveLauncherData && relative.StartsWith("PCLLiteData/", StringComparison.OrdinalIgnoreCase)) continue;
            current[relative] = _HashFile(file);
        }

        var differences = new List<BackupDifference>();
        foreach (var (path, oldFile) in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!current.TryGetValue(path, out var currentHash))
                differences.Add(new BackupDifference(path, BackupDifferenceKind.Removed));
            else if (!HashEquals(oldFile.Sha256, currentHash))
                differences.Add(new BackupDifference(path, BackupDifferenceKind.Modified));
        }
        foreach (var path in current.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expected.ContainsKey(path)) differences.Add(new BackupDifference(path, BackupDifferenceKind.Added));
        }
        return differences.OrderBy(static item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool Delete(string storePath, string snapshotId, string sourcePath, BackupSnapshotKind kind)
    {
        if (!Guid.TryParseExact(snapshotId, "N", out _)) throw new InvalidDataException("Invalid backup snapshot identifier.");
        var store = Path.GetFullPath(storePath);
        if (!Directory.Exists(store)) return false;
        var manifestPath = Path.Combine(store, snapshotId + ".json");
        if (!File.Exists(manifestPath)) return false;
        var snapshot = _ReadManifest(manifestPath);
        if (snapshot.Id != snapshotId || snapshot.Kind != kind || snapshot.SourceKey != _SourceKey(Path.GetFullPath(sourcePath)))
            throw new InvalidDataException("The backup snapshot does not belong to this target.");
        var archivePath = Path.Combine(store, snapshotId + ".zip");
        if (File.Exists(archivePath)) File.Delete(archivePath);
        File.Delete(manifestPath);
        return true;
    }

    public static void ApplyRetention(string storePath, BackupSnapshotKind kind, string sourcePath, int maxSnapshots)
    {
        if (maxSnapshots < 0) throw new ArgumentOutOfRangeException(nameof(maxSnapshots));
        var snapshots = List(storePath, kind, sourcePath);
        foreach (var snapshot in snapshots.Where(static item => !item.RetainFromAutomaticRetention).Skip(maxSnapshots))
        {
            var store = Path.GetFullPath(storePath);
            foreach (var extension in new[] { ".zip", ".json" })
            {
                var path = Path.Combine(store, snapshot.Id + extension);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private static void _ExtractAndVerify(string archivePath, string stage, BackupSnapshot snapshot, CancellationToken token)
    {
        var expected = snapshot.Files.ToDictionary(static item => item.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = _NormalizeRelative(entry.FullName);
            _ValidateRelativePath(relative);
            if (!expected.TryGetValue(relative, out var record) || !seen.Add(relative))
                throw new InvalidDataException($"Unexpected or duplicate file in backup archive: '{relative}'.");
            if (entry.Length != record.Length) throw new InvalidDataException($"Length mismatch in backup archive: '{relative}'.");
            var outputPath = _ResolveWithin(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var input = entry.Open();
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 128];
            long length = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                length += read;
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (length != record.Length || !HashEquals(actual, record.Sha256))
                throw new InvalidDataException($"Backup file integrity mismatch: '{relative}'.");
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("Backup archive is missing manifest files.");
    }

    private static IEnumerable<string> _EnumerateFiles(string root, bool excludeSaves)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                _RejectReparsePoint(child);
                var relative = _NormalizeRelative(Path.GetRelativePath(root, child));
                if (excludeSaves && (string.Equals(relative, "saves", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(relative, "PCLLiteData", StringComparison.OrdinalIgnoreCase))) continue;
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                _RejectReparsePoint(file);
                var relative = _NormalizeRelative(Path.GetRelativePath(root, file));
                if (excludeSaves && (relative.StartsWith("saves/", StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("PCLLiteData/", StringComparison.OrdinalIgnoreCase))) continue;
                if (!excludeSaves && string.Equals(relative, "session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }
    }

    private static void _CopyDirectoryChecked(string source, string destination, CancellationToken token)
    {
        _RejectReparsePoint(source);
        Directory.CreateDirectory(destination);
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                token.ThrowIfCancellationRequested();
                _RejectReparsePoint(directory);
                var targetDirectory = Path.Combine(destination, Path.GetRelativePath(source, directory));
                Directory.CreateDirectory(targetDirectory);
                pending.Push(directory);
            }
            foreach (var file in Directory.EnumerateFiles(current))
            {
                token.ThrowIfCancellationRequested();
                _RejectReparsePoint(file);
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }

    private static BackupSnapshot _ReadManifest(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Backup manifest not found.", path);
        var snapshot = JsonSerializer.Deserialize<BackupSnapshot>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Backup manifest is empty.");
        if (snapshot.SchemaVersion != 1 || !Guid.TryParseExact(snapshot.Id, "N", out _) || !IsHash(snapshot.ArchiveSha256)
            || snapshot.ArchiveLength < 0 || string.IsNullOrWhiteSpace(snapshot.SourcePath)
            || snapshot.SourceKey != _SourceKey(Path.GetFullPath(snapshot.SourcePath)))
            throw new InvalidDataException("Backup manifest contains invalid identity or fields.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        {
            _ValidateRelativePath(file.Path);
            if (!IsHash(file.Sha256) || file.Length < 0 || !seen.Add(file.Path))
                throw new InvalidDataException($"Backup manifest contains an invalid or duplicate entry: '{file.Path}'.");
        }
        return snapshot;
    }

    private static void _EnsureSeparateTrees(string source, string store)
    {
        var sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var storeFull = Path.GetFullPath(store).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (sourceFull.StartsWith(storeFull, StringComparison.OrdinalIgnoreCase)
            || storeFull.StartsWith(sourceFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup storage and source trees must be separate.");
    }

    private static string _SourceKey(string sourcePath)
    {
        var canonical = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string _HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool HashEquals(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool IsHash(string value) => value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static void _ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException($"Invalid backup path '{path}'.");
        var normalized = _NormalizeRelative(path);
        if (normalized.StartsWith('/') || normalized.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"Invalid backup path '{path}'.");
    }

    private static string _ResolveWithin(string root, string relative)
    {
        _ValidateRelativePath(relative);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup entry escapes extraction root.");
        return target;
    }

    private static void _RejectReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Backup operation refuses reparse point '{path}'.");
    }

    private static string _NormalizeRelative(string path) => path.Replace('\\', '/');
}
