using System;
using System.Collections.Generic;
using System.Linq;
using PCL.Plugin.SDK;

namespace PCL.Core.Plugins;

internal sealed record PluginDependencyCandidate(PluginManifest Manifest, string Directory, bool Enabled);

internal sealed record PluginDependencyResolution(
    IReadOnlyList<PluginDependencyCandidate> LoadOrder,
    IReadOnlyDictionary<string, string> Blocked);

/// <summary>Builds a deterministic dependency-first load order and blocks invalid dependency chains.</summary>
internal static class PluginDependencyResolver
{
    public static PluginDependencyResolution Resolve(IReadOnlyCollection<PluginDependencyCandidate> plugins)
    {
        var byId = plugins.ToDictionary(candidate => candidate.Manifest.Id, StringComparer.Ordinal);
        var enabled = plugins.Where(candidate => candidate.Enabled)
            .ToDictionary(candidate => candidate.Manifest.Id, StringComparer.Ordinal);
        var blocked = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var candidate in enabled.Values)
        {
            foreach (var (dependencyId, versionRange) in candidate.Manifest.Dependencies)
            {
                if (!byId.TryGetValue(dependencyId, out var dependency))
                {
                    blocked.TryAdd(candidate.Manifest.Id, $"Required plugin dependency is missing: {dependencyId} ({versionRange}).");
                    break;
                }
                if (!dependency.Enabled)
                {
                    blocked.TryAdd(candidate.Manifest.Id, $"Required plugin dependency is disabled: {dependencyId}.");
                    break;
                }
                if (!PluginVersionRange.Satisfies(dependency.Manifest.Version, versionRange))
                {
                    blocked.TryAdd(candidate.Manifest.Id,
                        $"Plugin dependency version mismatch: {dependencyId} {dependency.Manifest.Version} does not satisfy {versionRange}.");
                    break;
                }
            }
        }

        _PropagateBlockedDependencies(enabled, blocked);
        var indegree = enabled.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var dependents = enabled.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var candidate in enabled.Values)
        {
            if (blocked.ContainsKey(candidate.Manifest.Id)) continue;
            foreach (var dependencyId in candidate.Manifest.Dependencies.Keys)
            {
                if (blocked.ContainsKey(dependencyId)) continue;
                indegree[candidate.Manifest.Id]++;
                dependents[dependencyId].Add(candidate.Manifest.Id);
            }
        }

        var ready = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, count) in indegree)
            if (count == 0 && !blocked.ContainsKey(id)) ready.Add(id);
        var ordered = new List<PluginDependencyCandidate>(enabled.Count);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            ordered.Add(enabled[id]);
            foreach (var dependentId in dependents[id])
            {
                indegree[dependentId]--;
                if (indegree[dependentId] == 0) ready.Add(dependentId);
            }
        }

        foreach (var id in _FindCycleMembers(enabled, blocked))
            blocked[id] = "Plugin dependency cycle detected.";
        _PropagateBlockedDependencies(enabled, blocked);
        ordered.RemoveAll(candidate => blocked.ContainsKey(candidate.Manifest.Id));
        return new(ordered, blocked);
    }

    private static HashSet<string> _FindCycleMembers(IReadOnlyDictionary<string, PluginDependencyCandidate> enabled,
        IReadOnlyDictionary<string, string> blocked)
    {
        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cycleMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in enabled.Keys)
            if (!blocked.ContainsKey(id)) _Visit(id);
        return cycleMembers;

        void _Visit(string id)
        {
            if (state.TryGetValue(id, out var currentState)) return;
            state[id] = 1;
            stack.Add(id);
            foreach (var dependencyId in enabled[id].Manifest.Dependencies.Keys)
            {
                if (blocked.ContainsKey(dependencyId)) continue;
                if (!state.TryGetValue(dependencyId, out var dependencyState))
                {
                    _Visit(dependencyId);
                }
                else if (dependencyState == 1)
                {
                    var cycleStart = stack.LastIndexOf(dependencyId);
                    for (var index = cycleStart; index < stack.Count; index++) cycleMembers.Add(stack[index]);
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }
    }

    private static void _PropagateBlockedDependencies(IReadOnlyDictionary<string, PluginDependencyCandidate> enabled,
        IDictionary<string, string> blocked)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in enabled.Values)
            {
                if (blocked.ContainsKey(candidate.Manifest.Id)) continue;
                var failedDependency = candidate.Manifest.Dependencies.Keys.FirstOrDefault(blocked.ContainsKey);
                if (failedDependency is null) continue;
                blocked[candidate.Manifest.Id] = $"Plugin dependency could not be loaded: {failedDependency}.";
                changed = true;
            }
        } while (changed);
    }
}
