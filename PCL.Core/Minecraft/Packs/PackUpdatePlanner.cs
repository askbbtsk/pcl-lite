using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace PCL.Core.Minecraft.Packs;

/// <summary>File identity recorded for an installed authored pack.</summary>
public sealed record PackFileEntry
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public long Length { get; init; }
    public string? Identity { get; init; }
    public bool Optional { get; init; }
}

/// <summary>Lite-owned provenance snapshot. It intentionally does not reuse another launcher's private schema.</summary>
public sealed record PackBaseline
{
    public int SchemaVersion { get; init; } = 1;
    public string? Source { get; init; }
    public string? ProjectId { get; init; }
    public string? VersionId { get; init; }
    public IReadOnlyDictionary<string, PackFileEntry> Files { get; init; }
        = new ReadOnlyDictionary<string, PackFileEntry>(new Dictionary<string, PackFileEntry>(StringComparer.OrdinalIgnoreCase));
}

public enum PackUpdateActionKind
{
    InstallAuthorFile,
    UpdateAuthorFile,
    RemoveAuthorFile,
    PreservePersonalFile,
}

public enum PackFileOwnership
{
    AuthorUnchanged,
    AuthorChanged,
    AuthorRenamed,
    PersonalModified,
    PersonalAdded,
    Disabled,
    Deleted,
    Unknown,
}

/// <summary>A deterministic decision for one pack file. PayloadPath is supplied by the caller and never trusted as a destination.</summary>
public sealed record PackUpdateAction
{
    public required PackUpdateActionKind Kind { get; init; }
    public required PackFileOwnership Ownership { get; init; }
    public required string DestinationPath { get; init; }
    public string? PreviousPath { get; init; }
    public string? PayloadPath { get; init; }
    public string? ExpectedSha256 { get; init; }
    /// <summary>Hash of the destination as observed when the preview plan was built; null means it was absent.</summary>
    public string? ExpectedCurrentSha256 { get; init; }
    public string? Reason { get; init; }
}

public sealed record PackUpdatePlan
{
    public required string GameRoot { get; init; }
    public required PackBaseline? Previous { get; init; }
    public required PackBaseline Target { get; init; }
    public IReadOnlyList<PackUpdateAction> Actions { get; init; } = Array.Empty<PackUpdateAction>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public enum PackFileChoice
{
    PreservePersonal,
    UseAuthor,
}

/// <summary>Builds a three-way pack update plan from the recorded author baseline, current files, and the target package.</summary>
public static class PackUpdatePlanner
{
    public static PackUpdatePlan Build(string gameRoot, PackBaseline? previous, PackBaseline target,
        IReadOnlyDictionary<string, string> payloadPaths,
        IReadOnlyDictionary<string, PackFileChoice>? choices = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(payloadPaths);
        var root = Path.GetFullPath(gameRoot);
        var warnings = new List<string>();
        var actions = new List<PackUpdateAction>();
        var oldFiles = BuildPathIndex(previous?.Files ?? EmptyFiles, "previous");
        var targetFiles = BuildPathIndex(target.Files, "target");
        var oldMatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetMatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var oldIdentities = UniqueIdentityIndex(oldFiles.Values);
        var targetIdentities = UniqueIdentityIndex(targetFiles.Values);
        var pairs = new List<(PackFileEntry Old, PackFileEntry New)>();

        // Stable platform identities handle author renames without fuzzy filename matching.
        foreach (var pair in targetIdentities)
        {
            if (oldIdentities.TryGetValue(pair.Key, out var oldEntry))
            {
                pairs.Add((oldEntry, pair.Value));
                oldMatched.Add(oldEntry.Path);
                targetMatched.Add(pair.Value.Path);
            }
        }

        // Legacy entries without platform identity are paired only by exact normalized path.
        foreach (var next in targetFiles.Values.OrderBy(static f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (targetMatched.Contains(next.Path)) continue;
            if (oldFiles.TryGetValue(next.Path.Replace('\\', '/'), out var old) && !oldMatched.Contains(old.Path))
            {
                pairs.Add((old, next));
                oldMatched.Add(old.Path);
                targetMatched.Add(next.Path);
            }
        }

        foreach (var (old, next) in pairs)
        {
            var localPath = ResolveWithin(root, old.Path);
            var exists = File.Exists(localPath);
            var selectedChoice = GetChoice(choices, next.Path, old.Path);
            var localHash = exists ? HashFile(localPath) : null;
            var localMatchesBaseline = exists && HashEquals(localHash, old.Sha256);
            var rename = !PathEquals(old.Path, next.Path);
            if (!exists)
            {
                if (selectedChoice != PackFileChoice.UseAuthor)
                {
                    // A missing tracked file is often an intentionally disabled mod or a player deletion.
                    // Keep that state across updates unless the user explicitly chooses the author's version.
                    actions.Add(new PackUpdateAction
                    {
                        Kind = PackUpdateActionKind.PreservePersonalFile,
                        Ownership = PackFileOwnership.Disabled,
                        DestinationPath = old.Path,
                        PreviousPath = old.Path,
                        Reason = "The tracked author file is absent locally; preserve its disabled or deleted state.",
                    });
                    continue;
                }

                var targetPayload = GetPayload(payloadPaths, next.Path);
                actions.Add(Install(next, targetPayload, rename ? PackFileOwnership.AuthorRenamed : PackFileOwnership.Deleted,
                    rename ? old.Path : null, "The user chose to restore the tracked author file.",
                    HashFileIfExists(ResolveWithin(root, next.Path))));
                continue;
            }

            // A path rename must never consume an unrelated player file which
            // happens to occupy the new author destination. The global "use
            // author" choice is explicit authorization to replace it.
            var nextLocalPath = ResolveWithin(root, next.Path);
            var nextExists = File.Exists(nextLocalPath);
            var nextHash = nextExists ? HashFile(nextLocalPath) : null;
            if (rename && nextExists && selectedChoice != PackFileChoice.UseAuthor)
            {
                actions.Add(new PackUpdateAction
                {
                    Kind = PackUpdateActionKind.PreservePersonalFile,
                    Ownership = localMatchesBaseline ? PackFileOwnership.AuthorUnchanged : PackFileOwnership.PersonalModified,
                    DestinationPath = old.Path,
                    PreviousPath = old.Path,
                    Reason = "The renamed author file destination is occupied; preserve the current author file until the conflict is explicitly resolved.",
                });
                actions.Add(new PackUpdateAction
                {
                    Kind = PackUpdateActionKind.PreservePersonalFile,
                    Ownership = PackFileOwnership.PersonalAdded,
                    DestinationPath = next.Path,
                    PreviousPath = next.Path,
                    Reason = "A player file occupies the new author file path and was preserved.",
                });
                warnings.Add($"Author file '{next.Path}' was not installed because a local file already occupies that path.");
                continue;
            }

            if (!localMatchesBaseline && selectedChoice != PackFileChoice.UseAuthor)
            {
                actions.Add(new PackUpdateAction
                {
                    Kind = PackUpdateActionKind.PreservePersonalFile,
                    Ownership = PackFileOwnership.PersonalModified,
                    DestinationPath = old.Path,
                    PreviousPath = old.Path,
                    Reason = "The local file differs from the installed author baseline; preserve it by default.",
                });
                if (rename)
                    actions.Add(Install(next, GetPayload(payloadPaths, next.Path), PackFileOwnership.AuthorRenamed, old.Path,
                        "A modified personal file is retained at its previous path while the renamed author file is installed.", nextHash));
                else if (!HashEquals(old.Sha256, next.Sha256))
                    warnings.Add($"Author update for '{next.Path}' was not applied because the local file was modified.");
                continue;
            }

            if (rename)
            {
                actions.Add(new PackUpdateAction
                {
                    Kind = PackUpdateActionKind.RemoveAuthorFile,
                    Ownership = PackFileOwnership.AuthorRenamed,
                    DestinationPath = old.Path,
                    PreviousPath = old.Path,
                    ExpectedCurrentSha256 = localHash,
                    Reason = "The unchanged author file moved to a new path.",
                });
                actions.Add(Install(next, GetPayload(payloadPaths, next.Path), PackFileOwnership.AuthorRenamed, old.Path, "Install the author file at its new path.", nextHash));
            }
            else if (!HashEquals(old.Sha256, next.Sha256) || !localMatchesBaseline && selectedChoice == PackFileChoice.UseAuthor)
            {
                actions.Add(Install(next, GetPayload(payloadPaths, next.Path), PackFileOwnership.AuthorChanged, old.Path,
                    "Replace the local file with the selected author version.", localHash));
            }
            else
            {
                actions.Add(new PackUpdateAction
                {
                    Kind = PackUpdateActionKind.PreservePersonalFile,
                    Ownership = PackFileOwnership.AuthorUnchanged,
                    DestinationPath = next.Path,
                    PreviousPath = old.Path,
                    Reason = "The author file is unchanged.",
                });
            }
        }

        foreach (var old in oldFiles.Values.OrderBy(static f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (oldMatched.Contains(old.Path)) continue;
            var localPath = ResolveWithin(root, old.Path);
            if (!File.Exists(localPath)) continue;
            var localHash = HashFile(localPath);
            if (HashEquals(localHash, old.Sha256))
            {
                actions.Add(new PackUpdateAction
                {
                    Kind = PackUpdateActionKind.RemoveAuthorFile,
                    Ownership = PackFileOwnership.Deleted,
                    DestinationPath = old.Path,
                    PreviousPath = old.Path,
                    ExpectedCurrentSha256 = localHash,
                    Reason = "The unchanged author file was removed from the target package.",
                });
            }
            else if (GetChoice(choices, old.Path, old.Path) == PackFileChoice.UseAuthor)
            {
                actions.Add(new PackUpdateAction
                {
                    Kind = PackUpdateActionKind.RemoveAuthorFile,
                    Ownership = PackFileOwnership.Deleted,
                    DestinationPath = old.Path,
                    PreviousPath = old.Path,
                    ExpectedCurrentSha256 = localHash,
                    Reason = "The author removed this file and the user chose to accept the author package.",
                });
            }
            else
            {
                actions.Add(new PackUpdateAction
                {
                    Kind = PackUpdateActionKind.PreservePersonalFile,
                    Ownership = PackFileOwnership.PersonalModified,
                    DestinationPath = old.Path,
                    PreviousPath = old.Path,
                    Reason = "An author-removed file contains personal changes and is retained.",
                });
            }
        }

        foreach (var next in targetFiles.Values.OrderBy(static f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (targetMatched.Contains(next.Path)) continue;
            var destination = ResolveWithin(root, next.Path);
            var exists = File.Exists(destination);
            var localHash = exists ? HashFile(destination) : null;
            var replacedTrackedAuthor = exists
                && oldFiles.TryGetValue(next.Path.Replace('\\', '/'), out var oldAtDestination)
                && (!oldMatched.Contains(oldAtDestination.Path)
                    || actions.Any(action => action.Kind == PackUpdateActionKind.RemoveAuthorFile
                        && PathEquals(action.DestinationPath, next.Path)))
                && HashEquals(localHash, oldAtDestination.Sha256);
            if (exists && !replacedTrackedAuthor && GetChoice(choices, next.Path, next.Path) != PackFileChoice.UseAuthor)
            {
                actions.Add(new PackUpdateAction
                {
                    Kind = PackUpdateActionKind.PreservePersonalFile,
                    Ownership = PackFileOwnership.PersonalAdded,
                    DestinationPath = next.Path,
                    PreviousPath = next.Path,
                    Reason = "A player file occupies the new author file path and was preserved.",
                });
                warnings.Add($"Author file '{next.Path}' was not installed because a local file already occupies that path.");
                continue;
            }
            actions.Add(Install(next, GetPayload(payloadPaths, next.Path), PackFileOwnership.PersonalAdded, null,
                "New author file.", localHash));
        }

        // A rename/removal and a new author file may deliberately reuse the same
        // path. The replacement already journals the original file; a separate
        // removal would duplicate that destination and invalidate the transaction.
        var replacedPaths = actions.Where(action => action.Kind is PackUpdateActionKind.InstallAuthorFile
                or PackUpdateActionKind.UpdateAuthorFile)
            .Select(action => action.DestinationPath.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        actions.RemoveAll(action => action.Kind == PackUpdateActionKind.RemoveAuthorFile
            && replacedPaths.Contains(action.DestinationPath.Replace('\\', '/')));

        return new PackUpdatePlan
        {
            GameRoot = root,
            Previous = previous,
            Target = target,
            Actions = actions,
            Warnings = warnings,
        };
    }

    private static IReadOnlyDictionary<string, PackFileEntry> EmptyFiles { get; }
        = new ReadOnlyDictionary<string, PackFileEntry>(new Dictionary<string, PackFileEntry>(StringComparer.OrdinalIgnoreCase));


    private static Dictionary<string, PackFileEntry> BuildPathIndex(IReadOnlyDictionary<string, PackFileEntry> entries, string label)
    {
        var result = new Dictionary<string, PackFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Values)
        {
            ValidateRelativePath(entry.Path);
            if (!IsSha256(entry.Sha256)) throw new InvalidDataException($"Invalid SHA-256 for {label} pack file '{entry.Path}'.");
            if (entry.Length < 0) throw new InvalidDataException($"Invalid length for {label} pack file '{entry.Path}'.");
            var key = entry.Path.Replace('\\', '/');
            if (!result.TryAdd(key, entry)) throw new InvalidDataException($"Duplicate {label} pack path '{entry.Path}'.");
        }
        return result;
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static Dictionary<string, PackFileEntry> UniqueIdentityIndex(IEnumerable<PackFileEntry> entries)
    {
        var groups = entries.Where(static e => !string.IsNullOrWhiteSpace(e.Identity))
            .GroupBy(static e => e.Identity!, StringComparer.OrdinalIgnoreCase);
        return groups.Where(static g => g.Count() == 1).ToDictionary(static g => g.Key, static g => g.Single(), StringComparer.OrdinalIgnoreCase);
    }

    private static PackUpdateAction Install(PackFileEntry next, string payload, PackFileOwnership ownership, string? previousPath, string reason,
        string? expectedCurrentSha256 = null)
        => new()
        {
            Kind = PackUpdateActionKind.InstallAuthorFile,
            Ownership = ownership,
            DestinationPath = next.Path,
            PreviousPath = previousPath,
            PayloadPath = payload,
            ExpectedSha256 = next.Sha256,
            ExpectedCurrentSha256 = expectedCurrentSha256,
            Reason = reason,
        };

    private static string? HashFileIfExists(string path) => File.Exists(path) ? HashFile(path) : null;

    private static string GetPayload(IReadOnlyDictionary<string, string> payloadPaths, string relativePath)
        => payloadPaths.TryGetValue(relativePath, out var payload) && !string.IsNullOrWhiteSpace(payload)
            ? Path.GetFullPath(payload)
            : throw new InvalidDataException($"Missing staged payload for '{relativePath}'.");

    public static string ResolveWithin(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Pack path escapes the instance root: '{relativePath}'.");
        return full;
    }

    public static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Contains('\0'))
            throw new InvalidDataException($"Invalid pack-relative path: '{relativePath}'.");
        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"Invalid pack-relative path: '{relativePath}'.");
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static bool HashEquals(string? left, string? right)
        => string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeHash(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static bool PathEquals(string left, string right)
        => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static PackFileChoice GetChoice(IReadOnlyDictionary<string, PackFileChoice>? choices, string newPath, string oldPath)
    {
        if (choices is null) return PackFileChoice.PreservePersonal;
        if (choices.TryGetValue(newPath, out var choice) || choices.TryGetValue(oldPath, out choice)) return choice;
        foreach (var pair in choices)
            if (PathEquals(pair.Key, newPath) || PathEquals(pair.Key, oldPath)) return pair.Value;
        return PackFileChoice.PreservePersonal;
    }
}





