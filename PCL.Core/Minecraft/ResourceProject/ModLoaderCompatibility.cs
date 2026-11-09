using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Minecraft.ResourceProject;

public enum ModLoaderCompatibilityResult
{
    Unknown,
    DeclaredCompatible,
    DifferentLoader,
}

/// <summary>Compares explicit local loader declarations; this is not a prediction of game stability.</summary>
public static class ModLoaderCompatibility
{
    private static readonly HashSet<string> KnownLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fabric", "Quilt", "Forge", "NeoForge",
    };

    public static ModLoaderCompatibilityResult Evaluate(IEnumerable<string> declaredLoaders,
        IEnumerable<string> targetLoaders, bool hasCompatibilityBridge = false)
    {
        ArgumentNullException.ThrowIfNull(declaredLoaders);
        ArgumentNullException.ThrowIfNull(targetLoaders);
        var declared = new HashSet<string>(declaredLoaders.Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(targetLoaders.Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        // Quilt accepts Fabric metadata. Forge/NeoForge interoperability depends on versions and is left unknown.
        if (targets.Contains("Quilt")) targets.Add("Fabric");
        if (declared.Overlaps(targets)) return ModLoaderCompatibilityResult.DeclaredCompatible;
        if (declared.Count == 0 || targets.Count == 0 || hasCompatibilityBridge
            || declared.Any(value => !KnownLoaders.Contains(value)) || targets.Any(value => !KnownLoaders.Contains(value)))
            return ModLoaderCompatibilityResult.Unknown;
        if ((declared.Contains("Forge") && targets.Contains("NeoForge"))
            || (declared.Contains("NeoForge") && targets.Contains("Forge")))
            return ModLoaderCompatibilityResult.Unknown;
        return ModLoaderCompatibilityResult.DifferentLoader;
    }
}
