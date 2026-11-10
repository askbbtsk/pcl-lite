using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCL.Core.Minecraft.Resources;

public sealed record ModIndexTarget(string InstancePath, string ModsDirectory, string LoaderContext);

/// <summary>Reuse one physical scan only when both the mod directory and parser context match.</summary>
public static class ModIndexBatchPlan
{
    public static IReadOnlyList<IReadOnlyList<ModIndexTarget>> Group(IEnumerable<ModIndexTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<(string Directory, string Context), List<ModIndexTarget>>();
        foreach (var target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            var instance = Normalize(target.InstancePath);
            if (!instances.Add(instance)) continue;
            var directory = Normalize(target.ModsDirectory);
            var key = (directory.ToUpperInvariant(), target.LoaderContext ?? "");
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = []);
            group.Add(target);
        }
        return groups.Values.Select(group => (IReadOnlyList<ModIndexTarget>)group.ToArray()).ToArray();
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
