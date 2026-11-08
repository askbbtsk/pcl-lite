using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCL.Core.Minecraft.Packs;

/// <summary>Applies a prepared pack update as a rollback-capable file transaction.</summary>
public static class PackUpdateTransaction
{
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
            }

            var index = 0;
            foreach (var action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = PackUpdatePlanner.ResolveWithin(root, action.DestinationPath);
                _EnsureParentDirectory(root, Path.GetDirectoryName(destination)!, createdDirectories);
                if (File.Exists(destination))
                {
                    var backup = Path.Combine(transactionRoot, backups.Count.ToString("D8", System.Globalization.CultureInfo.InvariantCulture));
                    File.Copy(destination, backup, overwrite: false);
                    backups.Add(destination, backup);
                }

                if (action.Kind == PackUpdateActionKind.RemoveAuthorFile)
                {
                    if (File.Exists(destination)) File.Delete(destination);
                }
                else
                {
                    var payload = action.PayloadPath!;
                    var temp = destination + $".pcl-{Guid.NewGuid():N}.tmp";
                    try
                    {
                        File.Copy(payload, temp, overwrite: false);
                        if (!PackUpdatePlanner.HashEquals(PackUpdatePlanner.HashFile(temp), action.ExpectedSha256))
                            throw new InvalidDataException($"Pack payload changed while applying '{action.DestinationPath}'.");
                        File.Move(temp, destination, overwrite: true);
                        if (!backups.ContainsKey(destination)) createdFiles.Add(destination);
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
            succeeded = true;
        }
        catch (Exception originalError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var created in createdFiles)
            {
                try { if (File.Exists(created)) File.Delete(created); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
            }
            foreach (var backup in backups.Reverse())
            {
                try
                {
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


