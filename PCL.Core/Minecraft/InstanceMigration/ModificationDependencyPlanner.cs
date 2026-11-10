using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PCL.Core.Minecraft.InstanceMigration;

public sealed record ModificationDependencyReference(string Platform, string ProjectId, string? FileId = null);
public sealed record ModificationDependencyFile
{
    public required string Platform { get; init; }
    public required string ProjectId { get; init; }
    public required string FileId { get; init; }
    public required string FileName { get; init; }
    public required string Hash { get; init; }
    public IReadOnlyList<string> MinecraftVersions { get; init; } = [];
    public IReadOnlyList<string> Loaders { get; init; } = [];
    public IReadOnlyList<ModificationDependencyReference> Dependencies { get; init; } = [];
    public bool Stable { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
}
public sealed record ModificationDependencyRoot(string SourcePath, ModificationDependencyFile Target, bool Disabled);
public sealed record ModificationDependencyRejection(string SourcePath, string Reason);
public sealed record ModificationDependencyBatch(IReadOnlyList<string> AcceptedSources,
    IReadOnlyList<ModificationDependencyFile> Downloads, IReadOnlyList<ModificationDependencyRejection> Rejected);

/// <summary>Plans the whole selected set before downloading; an unresolved root is skipped without losing other roots.</summary>
public static class ModificationDependencyPlanner
{
    public static ModificationDependencyBatch Plan(IReadOnlyList<ModificationDependencyRoot> roots,
        string minecraftVersion, string targetLoader,
        Func<string, string, IReadOnlyList<ModificationDependencyFile>> projectFiles,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(projectFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLoader);
        token.ThrowIfCancellationRequested();
        if (roots.Count > 10000 || roots.Any(root => root is null || root.Target is null
                || string.IsNullOrWhiteSpace(root.SourcePath))
            || roots.Select(root => root.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Count)
            throw new ArgumentException("Selected roots must have unique source paths and valid target declarations.", nameof(roots));
        var cached = new Dictionary<string, IReadOnlyList<ModificationDependencyFile>>(StringComparer.Ordinal);
        var choices = roots.Where(root => !root.Disabled).GroupBy(root => ProjectKey(root.Target), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(root => root.Target).ToArray(), StringComparer.Ordinal);
        var accepted = new List<string>(); var rejected = new List<ModificationDependencyRejection>();
        var merged = new Dictionary<string, ModificationDependencyFile>(StringComparer.Ordinal);
        var acceptedRootFiles = new HashSet<string>(StringComparer.Ordinal);
        var acceptedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            var closure = new Dictionary<string, ModificationDependencyFile>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            string? problem = null;
            bool Visit(ModificationDependencyFile file, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 32) { problem = "Dependency nesting exceeds the supported limit."; return false; }
                if (!Compatible(file, minecraftVersion, targetLoader))
                { problem = $"{file.ProjectId}/{file.FileId}: no explicit {minecraftVersion} + {targetLoader} support."; return false; }
                if (string.IsNullOrWhiteSpace(file.ProjectId) || string.IsNullOrWhiteSpace(file.FileId)
                    || file.Platform is not ("Modrinth" or "CurseForge")
                    || !ValidArtifactName(file.FileName) || !InstanceMigrationPlanner.ValidHash(file.Hash))
                { problem = $"{file.ProjectId}/{file.FileId}: invalid artifact filename or digest."; return false; }
                var key = ProjectKey(file);
                if (closure.TryGetValue(key, out var previous))
                {
                    if (previous.FileId == file.FileId) return true;
                    problem = $"{file.ProjectId}: conflicting required file versions {previous.FileId} and {file.FileId}."; return false;
                }
                closure[key] = file;
                if (!visiting.Add(FileKey(file))) return true;
                if (root.Disabled) return true; // Disabled artifacts must not activate their dependencies.
                foreach (var reference in file.Dependencies)
                {
                    token.ThrowIfCancellationRequested();
                    if (reference is null || string.IsNullOrWhiteSpace(reference.ProjectId)
                        || reference.Platform is not ("Modrinth" or "CurseForge"))
                    { problem = file.ProjectId + ": required dependency identity is invalid."; return false; }
                    var dependencyKey = reference.Platform + ":" + reference.ProjectId;
                    ModificationDependencyFile? next;
                    if (closure.TryGetValue(dependencyKey, out var existing)) next = existing;
                    else if (choices.TryGetValue(dependencyKey, out var selected))
                    {
                        var unique = selected.Select(item => item.FileId).Distinct(StringComparer.Ordinal).ToArray();
                        if (unique.Length != 1) { problem = reference.ProjectId + ": more than one selected version of the same project."; return false; }
                        next = selected[0];
                    }
                    else
                    {
                        if (!cached.TryGetValue(dependencyKey, out var files))
                        {
                            try { files = projectFiles(reference.Platform, reference.ProjectId); }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error)
                            { problem = $"{reference.ProjectId}: dependency lookup failed: {error.Message}"; return false; }
                            if (files is null)
                            { problem = reference.ProjectId + ": dependency lookup returned no metadata."; return false; }
                            cached[dependencyKey] = files;
                        }
                        next = files.Where(candidate => candidate is not null
                                && candidate.Platform == reference.Platform && candidate.ProjectId == reference.ProjectId
                                && (reference.FileId is null || candidate.FileId == reference.FileId)
                                && Compatible(candidate, minecraftVersion, targetLoader)
                                && ValidArtifactName(candidate.FileName) && InstanceMigrationPlanner.ValidHash(candidate.Hash))
                            .OrderByDescending(candidate => candidate.Stable).ThenByDescending(candidate => candidate.PublishedAt)
                            .ThenBy(candidate => candidate.FileId, StringComparer.Ordinal).FirstOrDefault();
                    }
                    if (next is null || (reference.FileId is not null && next.FileId != reference.FileId))
                    {
                        problem = $"{reference.ProjectId}{(reference.FileId is null ? "" : "/" + reference.FileId)}: no compatible required file for {minecraftVersion} + {targetLoader}.";
                        return false;
                    }
                    if (!Visit(next, depth + 1)) return false;
                }
                visiting.Remove(FileKey(file));
                return true;
            }
            if (!Visit(root.Target, 0)) { rejected.Add(new(root.SourcePath, problem ?? "Dependency resolution failed.")); continue; }
            var destination = InstanceMigrationPlanner.PreserveDisabledName(root.Target.FileName, root.SourcePath);
            if (acceptedDestinations.Contains(destination))
            {
                rejected.Add(new(root.SourcePath, destination + ": target filename is already supplied by another selected source."));
                continue;
            }
            if (root.Disabled)
            {
                // Keeping a disabled replacement does not satisfy another enabled mod's dependency.
                accepted.Add(root.SourcePath);
                acceptedDestinations.Add(destination);
                continue;
            }
            var conflict = closure.FirstOrDefault(pair => merged.TryGetValue(pair.Key, out var old) && old.FileId != pair.Value.FileId);
            if (conflict.Key is not null)
            { rejected.Add(new(root.SourcePath, conflict.Value.ProjectId + ": conflicts with another accepted root's dependency version.")); continue; }
            var collision = closure.Values.Concat(merged.Values).GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Select(FileKey).Distinct(StringComparer.Ordinal).Count() > 1);
            if (collision is not null)
            { rejected.Add(new(root.SourcePath, collision.Key + ": different required artifacts have the same destination filename.")); continue; }
            foreach (var pair in closure) merged[pair.Key] = pair.Value;
            accepted.Add(root.SourcePath); acceptedRootFiles.Add(FileKey(root.Target));
            acceptedDestinations.Add(destination);
        }
        // Every artifact appears at most once; files already chosen as root replacements are not downloaded again as dependencies.
        return new(accepted, merged.Values.Where(file => !acceptedRootFiles.Contains(FileKey(file))).ToArray(), rejected);
    }

    public static string FileKey(ModificationDependencyFile file) => file.Platform + ":" + file.ProjectId + ":" + file.FileId;
    private static string ProjectKey(ModificationDependencyFile file) => file.Platform + ":" + file.ProjectId;
    private static bool Compatible(ModificationDependencyFile file, string minecraftVersion, string loader)
        => file is not null && file.MinecraftVersions is not null && file.Loaders is not null
            && file.Dependencies is not null && file.MinecraftVersions.Contains(minecraftVersion, StringComparer.Ordinal)
            && file.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase);

    private static bool ValidArtifactName(string name)
    {
        if (!InstanceMigrationPlanner.ValidFileName(name)) return false;
        var suffix = InstanceMigrationPlanner.DisabledSuffix(name);
        var plain = suffix.Length == 0 ? name : name[..^suffix.Length];
        return System.IO.Path.GetExtension(plain).ToLowerInvariant() is ".jar" or ".zip" or ".litemod";
    }
}
