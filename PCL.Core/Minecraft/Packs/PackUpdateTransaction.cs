using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace PCL.Core.Minecraft.Packs;

/// <summary>Applies a prepared pack update as a rollback-capable file transaction.</summary>
public static class PackUpdateTransaction
{
    private sealed record JournalEntry(string RelativePath, string? OriginalSha256, string? ExpectedCurrentSha256, string? BackupFile);
    private sealed record TransactionJournal(int Schema, string Root, bool Committed, IReadOnlyList<JournalEntry> Entries);
    private static readonly JsonSerializerOptions JournalOptions = new() { WriteIndented = true };

    /// <summary>Rolls back interrupted pack updates before the game is launched.</summary>
    public static int RecoverInterrupted(string gameRoot)
    {
        var root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(root) ?? throw new InvalidDataException("Pack root has no parent directory.");
        if (!Directory.Exists(parent)) return 0;
        var prefix = "." + Path.GetFileName(root) + ".pcl-update-";
        var recovered = 0;
        foreach (var transactionRoot in Directory.EnumerateDirectories(parent, prefix + "*", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(transactionRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Interrupted pack transaction is a reparse point.");
            var transactionName = Path.GetFileName(transactionRoot);
            if (!transactionName.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(transactionName[prefix.Length..], "N", out _)) continue;
            var journalPath = Path.Combine(transactionRoot, "transaction.json");
            if (!File.Exists(journalPath))
            {
                if (Directory.EnumerateFiles(transactionRoot).Any(path => Path.GetFileName(path) != "transaction.json.tmp"))
                    throw new IOException($"Unrecognized interrupted update data was retained at '{transactionRoot}'.");
                Directory.Delete(transactionRoot, recursive: true);
                continue;
            }
            if ((File.GetAttributes(journalPath) & FileAttributes.ReparsePoint) != 0
                || new FileInfo(journalPath).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Interrupted pack transaction journal is invalid.");
            var journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllText(journalPath), JournalOptions)
                ?? throw new InvalidDataException("Interrupted pack transaction journal is empty.");
            if (journal.Schema != 1 || !string.Equals(Path.GetFullPath(journal.Root).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Interrupted pack transaction journal does not match this instance.");
            if (journal.Committed)
            {
                Directory.Delete(transactionRoot, recursive: true);
                continue;
            }

            foreach (var entry in journal.Entries.Reverse())
            {
                PackUpdatePlanner.ValidateRelativePath(entry.RelativePath);
                var destination = PackUpdatePlanner.ResolveWithin(root, entry.RelativePath);
                EnsureNoReparsePointEscape(root, destination);
                var current = File.Exists(destination) ? PackUpdatePlanner.HashFile(destination) : null;
                if (PackUpdatePlanner.HashEquals(current, entry.OriginalSha256)) continue;
                if (!PackUpdatePlanner.HashEquals(current, entry.ExpectedCurrentSha256))
                    throw new IOException($"Interrupted pack recovery found an external change at '{entry.RelativePath}'; original data remains in '{transactionRoot}'.");
                if (entry.OriginalSha256 is null)
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(entry.BackupFile) || Path.GetFileName(entry.BackupFile) != entry.BackupFile)
                    throw new InvalidDataException("Interrupted pack transaction has an invalid backup reference.");
                var backup = Path.Combine(transactionRoot, entry.BackupFile);
                if (!File.Exists(backup) || !PackUpdatePlanner.HashEquals(PackUpdatePlanner.HashFile(backup), entry.OriginalSha256))
                    throw new InvalidDataException($"Interrupted pack transaction lost its original source for '{entry.RelativePath}'.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(backup, destination, overwrite: true);
            }
            Directory.Delete(transactionRoot, recursive: true);
            recovered++;
        }
        return recovered;
    }

    private static void WriteJournal(string transactionRoot, TransactionJournal journal)
    {
        var path = Path.Combine(transactionRoot, "transaction.json");
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, journal, JournalOptions);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public static void Apply(PackUpdatePlan plan, CancellationToken cancellationToken = default,
        Action<int, int>? progress = null, Action? commitMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var root = Path.GetFullPath(plan.GameRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Pack updates cannot target an instance root that is a reparse point.");
        var transactionRoot = Path.Combine(Path.GetDirectoryName(root)!, $".{Path.GetFileName(root)}.pcl-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transactionRoot);
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var createdFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actions = plan.Actions.Where(static action => action.Kind != PackUpdateActionKind.PreservePersonalFile).ToArray();
        var expectedCurrent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var succeeded = false;
        var rollbackComplete = true;

        try
        {
            // Validate every payload before touching the live instance.
            foreach (var action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = PackUpdatePlanner.ResolveWithin(root, action.DestinationPath);
                if (Directory.Exists(destination))
                    throw new InvalidDataException($"Pack destination is an existing directory: '{action.DestinationPath}'.");
                if (action.Kind == PackUpdateActionKind.InstallAuthorFile)
                {
                    var payload = action.PayloadPath ?? throw new InvalidDataException("An install action has no payload.");
                    if (!File.Exists(payload)) throw new FileNotFoundException("A staged pack payload is missing.", payload);
                    if ((File.GetAttributes(payload) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException($"Staged payload is a reparse point: '{payload}'.");
                    var actual = PackUpdatePlanner.HashFile(payload);
                    if (!PackUpdatePlanner.HashEquals(actual, action.ExpectedSha256))
                        throw new InvalidDataException($"Staged pack payload hash mismatch for '{action.DestinationPath}'.");
                }
                EnsureNoReparsePointEscape(root, destination);
                if (expectedCurrent.TryGetValue(destination, out var existingExpected))
                {
                    if (!PackUpdatePlanner.HashEquals(existingExpected, action.ExpectedCurrentSha256))
                        throw new InvalidDataException($"Conflicting preview snapshots for '{action.DestinationPath}'.");
                }
                else
                {
                    expectedCurrent.Add(destination, action.ExpectedCurrentSha256);
                }
            }

            // Another process may change a destination while the preview is open.
            // Refuse before the first live write if the preview snapshot is stale.
            foreach (var pair in expectedCurrent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = File.Exists(pair.Key) ? PackUpdatePlanner.HashFile(pair.Key) : null;
                if (!PackUpdatePlanner.HashEquals(current, pair.Value))
                    throw new IOException($"Pack destination changed after preview: '{Path.GetRelativePath(root, pair.Key)}'. Rebuild the update plan before applying it.");
            }

            var journalEntries = new List<JournalEntry>();
            var backupIndex = 0;
            foreach (var pair in expectedCurrent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var related = actions.First(action => string.Equals(PackUpdatePlanner.ResolveWithin(root, action.DestinationPath), pair.Key, StringComparison.OrdinalIgnoreCase));
                var original = pair.Value;
                string? backupFile = null;
                if (original is not null)
                {
                    backupFile = $"source-{backupIndex++:D8}.bin";
                    var backup = Path.Combine(transactionRoot, backupFile);
                    File.Copy(pair.Key, backup, overwrite: false);
                    if (!PackUpdatePlanner.HashEquals(PackUpdatePlanner.HashFile(backup), original))
                        throw new IOException($"Pack destination changed while preserving its source: '{related.DestinationPath}'.");
                    backups[pair.Key] = backup;
                }
                var newHash = related.Kind == PackUpdateActionKind.RemoveAuthorFile ? null : related.ExpectedSha256;
                journalEntries.Add(new JournalEntry(Path.GetRelativePath(root, pair.Key).Replace('\\', '/'), original, newHash, backupFile));
            }
            var journal = new TransactionJournal(1, root, false, journalEntries);
            WriteJournal(transactionRoot, journal);

            var index = 0;
            foreach (var action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = PackUpdatePlanner.ResolveWithin(root, action.DestinationPath);
                var current = File.Exists(destination) ? PackUpdatePlanner.HashFile(destination) : null;
                if (!expectedCurrent.TryGetValue(destination, out var expected) || !PackUpdatePlanner.HashEquals(current, expected))
                    throw new IOException($"Pack destination changed during update: '{action.DestinationPath}'.");
                _EnsureParentDirectory(root, Path.GetDirectoryName(destination)!, createdDirectories);
                if (action.Kind == PackUpdateActionKind.RemoveAuthorFile)
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    expectedCurrent[destination] = null;
                }
                else
                {
                    var payload = action.PayloadPath!;
                    var temp = Path.Combine(transactionRoot, $"new-{index:D8}.tmp");
                    try
                    {
                        File.Copy(payload, temp, overwrite: false);
                        if (!PackUpdatePlanner.HashEquals(PackUpdatePlanner.HashFile(temp), action.ExpectedSha256))
                            throw new InvalidDataException($"Pack payload changed while applying '{action.DestinationPath}'.");
                        File.Move(temp, destination, overwrite: true);
                        if (!backups.ContainsKey(destination)) createdFiles.Add(destination);
                        expectedCurrent[destination] = action.ExpectedSha256;
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }
                progress?.Invoke(++index, actions.Length);
            }
            cancellationToken.ThrowIfCancellationRequested();
            commitMetadata?.Invoke();
            WriteJournal(transactionRoot, journal with { Committed = true });
            succeeded = true;
        }
        catch (Exception originalError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var created in createdFiles)
            {
                try
                {
                    if (!File.Exists(created)) continue;
                    var current = PackUpdatePlanner.HashFile(created);
                    if (!expectedCurrent.TryGetValue(created, out var expected) || !PackUpdatePlanner.HashEquals(current, expected))
                        throw new IOException($"Rollback preserved an external change at '{created}'.");
                    File.Delete(created);
                }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            foreach (var backup in backups.Reverse())
            {
                try
                {
                    var current = File.Exists(backup.Key) ? PackUpdatePlanner.HashFile(backup.Key) : null;
                    if (!expectedCurrent.TryGetValue(backup.Key, out var expected) || !PackUpdatePlanner.HashEquals(current, expected))
                        throw new IOException($"Rollback preserved an external change at '{backup.Key}'.");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup.Key)!);
                    File.Copy(backup.Value, backup.Key, overwrite: true);
                }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            foreach (var directory in createdDirectories.OrderByDescending(static p => p.Length))
            {
                try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            rollbackComplete = rollbackErrors.Count == 0;
            if (!rollbackComplete)
                throw new AggregateException($"Pack update failed and rollback was incomplete. Recovery data was retained at '{transactionRoot}'.", new[] { originalError }.Concat(rollbackErrors));
            throw;
        }
        finally
        {
            if (succeeded || rollbackComplete)
            {
                try { if (Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, recursive: true); }
                catch { /* cleanup failure does not invalidate a completed file transaction */ }
            }
        }
    }

    private static void EnsureNoReparsePointEscape(string root, string destination)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(destination);
        var current = target;
        while (!string.IsNullOrEmpty(current) && current.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Pack update refuses to follow a reparse point: '{current}'.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static void _EnsureParentDirectory(string root, string parent, ISet<string> created)
    {
        var missing = new Stack<string>();
        var current = parent;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            var next = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(next) || !Path.GetFullPath(current).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pack destination parent escapes the instance root.");
            current = next;
        }
        while (missing.Count > 0)
        {
            var directory = missing.Pop();
            Directory.CreateDirectory(directory);
            created.Add(directory);
        }
    }
}

/// <summary>Captures installed pack files and compares final artifact trees independently of timestamps and ACLs.</summary>
public static class PackArtifactTree
{
    public static IReadOnlyDictionary<string, string> Capture(string root, IEnumerable<string>? excludedRelativePaths = null)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new DirectoryNotFoundException(fullRoot);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Artifact comparison refuses a reparse-point root.");
        var excluded = new HashSet<string>((excludedRelativePaths ?? Array.Empty<string>()).Select(NormalizeRelative), StringComparer.Ordinal);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Artifact comparison refuses reparse point '{Path.GetRelativePath(fullRoot, childDirectory)}'.");
                pending.Push(childDirectory);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Artifact comparison refuses reparse point '{Path.GetRelativePath(fullRoot, file)}'.");
                var relative = NormalizeRelative(Path.GetRelativePath(fullRoot, file));
                if (excluded.Contains(relative)) continue;
                result.Add(relative, PackUpdatePlanner.HashFile(file));
            }
        }
        return result;
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/');

    public static IReadOnlyList<string> Compare(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var differences = new List<string>();
        foreach (var path in expected.Keys.Union(actual.Keys, StringComparer.Ordinal).OrderBy(static p => p, StringComparer.Ordinal))
        {
            var hasExpected = expected.TryGetValue(path, out var expectedHash);
            var hasActual = actual.TryGetValue(path, out var actualHash);
            if (!hasExpected) differences.Add($"Unexpected file: {path}");
            else if (!hasActual) differences.Add($"Missing file: {path}");
            else if (!PackUpdatePlanner.HashEquals(expectedHash, actualHash)) differences.Add($"Content differs: {path}");
        }
        return differences;
    }
}


