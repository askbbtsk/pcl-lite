using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Minecraft.ResourceProject;

public sealed record DependencyProjectCandidate(string Id, string Source, string Slug);

/// <summary>Only treats a mod id as a platform project when its slug has one exact cross-platform match.</summary>
public static class DependencyProjectMatcher
{
    public static DependencyProjectCandidate? FindUniqueExactSlug(IEnumerable<DependencyProjectCandidate> candidates,
        string dependencyId)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyId);
        var matches = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id)
                                && !string.IsNullOrWhiteSpace(candidate.Source)
                                && string.Equals(candidate.Slug, dependencyId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(candidate => (candidate.Source, candidate.Id), new CandidateKeyComparer())
            .Select(static group => group.First())
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private sealed class CandidateKeyComparer : IEqualityComparer<(string Source, string Id)>
    {
        public bool Equals((string Source, string Id) left, (string Source, string Id) right)
            => StringComparer.OrdinalIgnoreCase.Equals(left.Source, right.Source)
               && StringComparer.OrdinalIgnoreCase.Equals(left.Id, right.Id);

        public int GetHashCode((string Source, string Id) value)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Id));
    }
}
