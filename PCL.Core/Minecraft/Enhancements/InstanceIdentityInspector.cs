using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCL.Core.Minecraft.Enhancements;

public sealed record InstanceIdentityIssue(string InstanceDirectory, EnhancementReadStatus Status, string? Error);
public sealed record InstanceIdentityReport(Guid? InstanceId, IReadOnlyList<string> DuplicateInstances,
    IReadOnlyList<InstanceIdentityIssue> Issues);

/// <summary>Reports copied enhancement identities without selecting an original or changing any declaration.</summary>
public static class InstanceIdentityInspector
{
    public static InstanceIdentityReport InspectSiblings(string instanceDirectory, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);
        var current = EnhancementPaths.Normalize(instanceDirectory);
        token.ThrowIfCancellationRequested();
        EnhancementPaths.RejectLinks(current);
        var own = new InstanceEnhancementStore(current).ReadDeclaration();
        var issues = new List<InstanceIdentityIssue>();
        if (own.Status is not (EnhancementReadStatus.Valid or EnhancementReadStatus.Missing))
            issues.Add(new(current, own.Status, own.Error));
        var identity = own.Value?.InstanceId;
        if (identity is null || identity == Guid.Empty) return new(identity, [], issues);
        var parent = Path.GetDirectoryName(current);
        if (parent is null) return new(identity, [], issues);
        var duplicates = new List<string>();
        try
        {
            EnhancementPaths.RejectLinks(parent);
            foreach (var sibling in Directory.EnumerateDirectories(parent, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (string.Equals(EnhancementPaths.Normalize(sibling), current, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    EnhancementPaths.RejectLinks(sibling);
                    var declaration = new InstanceEnhancementStore(sibling).ReadDeclaration();
                    if (declaration.Status == EnhancementReadStatus.Valid && declaration.Value?.InstanceId == identity)
                        duplicates.Add(sibling);
                    else if (declaration.Status is not (EnhancementReadStatus.Valid or EnhancementReadStatus.Missing))
                        issues.Add(new(sibling, declaration.Status, declaration.Error));
                }
                catch (Exception error) when (EnhancementPaths.IsExpectedFailure(error))
                { issues.Add(new(sibling, EnhancementReadStatus.Unavailable, error.Message)); }
            }
        }
        catch (Exception error) when (EnhancementPaths.IsExpectedFailure(error))
        { issues.Add(new(parent, EnhancementReadStatus.Unavailable, error.Message)); }
        return new(identity, duplicates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(), issues);
    }
}
