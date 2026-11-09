using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCL.Core.Minecraft.InstanceMigration;

public enum MigrationLoader { Fabric, Forge, NeoForge }
public enum MigrationModAction { CopyVerifiedLocal, DownloadSameProject, NeedsReview }

public sealed record MigrationCandidate
{
    public required string Platform { get; init; }
    public required string ProjectId { get; init; }
    public required string FileId { get; init; }
    public required string FileName { get; init; }
    public required string Hash { get; init; }
    public IReadOnlyList<string> MinecraftVersions { get; init; } = [];
    public IReadOnlyList<MigrationLoader> Loaders { get; init; } = [];
    public DateTimeOffset PublishedAt { get; init; }
    public bool Stable { get; init; }
}

public sealed record MigrationSourceMod
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public string? Sha1 { get; init; }
    public string? Platform { get; init; }
    public string? ProjectId { get; init; }
    public IReadOnlyList<MigrationCandidate> Candidates { get; init; } = [];
    public string? UnresolvedReason { get; init; }
}

public sealed record MigrationModPlan(MigrationSourceMod Source, MigrationModAction Action,
    MigrationCandidate? Candidate, string? TargetFileName, string? Reason)
{
    public bool Disabled => InstanceMigrationPlanner.DisabledSuffix(Source.Path).Length > 0;
}

public sealed record InstanceMigrationPlan(string MinecraftVersion, MigrationLoader TargetLoader,
    IReadOnlyList<MigrationModPlan> Mods);

/// <summary>Only exact project identities and explicit target support may produce automatic candidates.</summary>
public static class InstanceMigrationPlanner
{
    public static InstanceMigrationPlan Create(string minecraftVersion, MigrationLoader targetLoader,
        IReadOnlyList<MigrationSourceMod> sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        ArgumentNullException.ThrowIfNull(sources);
        if (!Enum.IsDefined(targetLoader)) throw new ArgumentOutOfRangeException(nameof(targetLoader));
        var plans = new List<MigrationModPlan>();
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Platform) || string.IsNullOrWhiteSpace(source.ProjectId))
            { plans.Add(new(source, MigrationModAction.NeedsReview, null, null, source.UnresolvedReason ?? "Unknown project identity.")); continue; }
            var matching = source.Candidates.Where(candidate => candidate.Platform == source.Platform
                && candidate.ProjectId == source.ProjectId && candidate.MinecraftVersions.Contains(minecraftVersion, StringComparer.Ordinal)
                && candidate.Loaders.Contains(targetLoader) && ValidModFileName(candidate.FileName) && ValidHash(candidate.Hash))
                .GroupBy(candidate => candidate.FileId, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            if (matching.Length == 0)
            { plans.Add(new(source, MigrationModAction.NeedsReview, null, null, source.UnresolvedReason ?? "No verified build supports the target Minecraft and loader.")); continue; }
            var stable = matching.Where(candidate => candidate.Stable).ToArray();
            var pool = stable.Length > 0 ? stable : matching;
            var newest = pool.Max(candidate => candidate.PublishedAt);
            var latest = pool.Where(candidate => candidate.PublishedAt == newest).ToArray();
            if (latest.Length != 1)
            { plans.Add(new(source, MigrationModAction.NeedsReview, null, null, "More than one target build has the newest publication time; choose manually.")); continue; }
            var chosen = latest[0];
            var sameBytes = string.Equals(chosen.Hash, source.Sha256, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(source.Sha1) && string.Equals(chosen.Hash, source.Sha1, StringComparison.OrdinalIgnoreCase));
            plans.Add(new(source, sameBytes ? MigrationModAction.CopyVerifiedLocal : MigrationModAction.DownloadSameProject,
                chosen, PreserveDisabledName(chosen.FileName, source.Path), null));
        }
        return new(minecraftVersion, targetLoader, plans);
    }

    public static string DisabledSuffix(string path)
    {
        if (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return path[^9..];
        return path.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ? path[^4..] : "";
    }

    public static string PreserveDisabledName(string downloadedFileName, string sourcePath)
    {
        if (!ValidFileName(downloadedFileName)) throw new InvalidDataException("Invalid target mod filename.");
        var suffix = DisabledSuffix(downloadedFileName);
        var bare = suffix.Length > 0 ? downloadedFileName[..^suffix.Length] : downloadedFileName;
        return bare + DisabledSuffix(sourcePath);
    }

    public static bool ValidFileName(string value) => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".."
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.Contains('/') && !value.Contains('\\')
        && !value.EndsWith(' ') && !value.EndsWith('.') && string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal);

    public static bool ValidHash(string value) => value is { Length: 32 or 40 or 64 } && value.All(char.IsAsciiHexDigit);

    private static bool ValidModFileName(string name)
    {
        if (!ValidFileName(name)) return false;
        var suffix = DisabledSuffix(name);
        var plain = suffix.Length > 0 ? name[..^suffix.Length] : name;
        return Path.GetExtension(plain).ToLowerInvariant() is ".jar" or ".zip" or ".litemod";
    }
}
