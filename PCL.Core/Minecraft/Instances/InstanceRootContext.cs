using System;
using System.IO;

namespace PCL.Core.Minecraft.Instances;

/// <summary>Legacy installers use the selected root; reject a mismatched source before any mutation.</summary>
public static class InstanceRootContext
{
    public static string Require(string instanceDirectory, string selectedRoot)
    {
        var instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory));
        var versions = Path.GetDirectoryName(instance);
        if (versions is null || !string.Equals(Path.GetFileName(versions), "versions", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The instance must be a direct child of a Minecraft versions directory.");
        var root = Path.GetDirectoryName(versions) ?? throw new InvalidDataException("Minecraft root is missing.");
        if (!string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedRoot)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select this instance's Minecraft directory before modifying it: " + root);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    }
}
