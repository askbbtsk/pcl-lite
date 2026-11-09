using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace PCL.Core.Minecraft.Enhancements;

public sealed record InstanceComparisonSnapshot(IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<ComponentDescriptor> Components, IReadOnlyList<string> Unavailable);
public sealed record InstanceDifference(string Category, string Name, string? Left, string? Right);

/// <summary>Read-only comparison of actual mod/config bytes, rather than a cached desired-state declaration.</summary>
public static class InstanceComparison
{
    public static InstanceComparisonSnapshot Capture(string gameDirectory, IReadOnlyList<ComponentDescriptor> components,
        CancellationToken token = default)
    {
        var root = Path.GetFullPath(gameDirectory);
        EnhancementPaths.RejectLinks(root);
        var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unavailable = new List<string>();
        foreach (var folder in new[] { "mods", "config" })
        {
            var path = Path.Combine(root, folder);
            if (!Directory.Exists(path)) continue;
            var pending = new Stack<string>(); pending.Push(path);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    EnhancementPaths.RejectLinks(directory);
                    foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
                        try
                        {
                            EnhancementPaths.RejectLinks(child);
                            if (Directory.Exists(child)) { pending.Push(child); continue; }
                            if (relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                            using var stream = new FileStream(child, FileMode.Open, FileAccess.Read, FileShare.Read);
                            files[relative] = Convert.ToHexString(SHA256.HashData(stream));
                        }
                        catch (Exception error) when (EnhancementPaths.IsExpectedFailure(error)) { unavailable.Add(relative); }
                    }
                }
                catch (Exception error) when (EnhancementPaths.IsExpectedFailure(error))
                { unavailable.Add(Path.GetRelativePath(root, directory).Replace('\\', '/')); }
            }
        }
        var options = Path.Combine(root, "options.txt");
        if (File.Exists(options))
        {
            try
            {
                EnhancementPaths.RejectLinks(options);
                using var input = new FileStream(options, FileMode.Open, FileAccess.Read, FileShare.Read);
                files["options.txt"] = Convert.ToHexString(SHA256.HashData(input));
            }
            catch (Exception error) when (EnhancementPaths.IsExpectedFailure(error)) { unavailable.Add("options.txt"); }
        }
        return new(files, components, unavailable);
    }

    public static IReadOnlyList<InstanceDifference> Compare(InstanceComparisonSnapshot left, InstanceComparisonSnapshot right)
    {
        var differences = new List<InstanceDifference>();
        foreach (var path in left.Files.Keys.Union(right.Files.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            left.Files.TryGetValue(path, out var before); right.Files.TryGetValue(path, out var after);
            if (IsUnavailable(left, path) || IsUnavailable(right, path))
            {
                differences.Add(new("unknown", path, null, null));
                continue;
            }
            if (before != after) differences.Add(new("file", path, before, after));
        }
        var leftComponents = left.Components.GroupBy(component => component.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => string.Join(" | ", group.Select(component => component.Version)), StringComparer.Ordinal);
        var rightComponents = right.Components.GroupBy(component => component.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => string.Join(" | ", group.Select(component => component.Version)), StringComparer.Ordinal);
        foreach (var id in leftComponents.Keys.Union(rightComponents.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            leftComponents.TryGetValue(id, out var before); rightComponents.TryGetValue(id, out var after);
            if (before != after) differences.Add(new("component", id, before, after));
        }
        return differences;
    }

    private static bool IsUnavailable(InstanceComparisonSnapshot snapshot, string relative)
        => snapshot.Unavailable.Any(path => relative.Equals(path, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
}
