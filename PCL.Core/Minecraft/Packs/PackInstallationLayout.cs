using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace PCL.Core.Minecraft.Packs;

/// <summary>Moves authored game content into the shared game directory when isolation is explicitly disabled.</summary>
public static class PackInstallationLayout
{
    public static void Apply(string instanceDirectory, string gameDirectory, IEnumerable<string> authoredPaths, bool isolated)
    {
        if (isolated) return;
        var sourceRoot = Path.GetFullPath(instanceDirectory);
        var gameRoot = Path.GetFullPath(gameDirectory);
        var files = authoredPaths.Select(path => path.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !path.StartsWith("PCL/", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("runtime/", StringComparison.OrdinalIgnoreCase) && !PackExportContent.IsInternalPath(path))
            .Select(path => (Source: PackUpdatePlanner.ResolveWithin(sourceRoot, path), Target: PackUpdatePlanner.ResolveWithin(gameRoot, path)))
            .Where(pair => File.Exists(pair.Source)).ToList();
        foreach (var pair in files)
        {
            PackExportContent.CheckAncestors(sourceRoot, pair.Source);
            PackExportContent.CheckAncestors(gameRoot, pair.Target);
            if (Directory.Exists(pair.Target)) throw new IOException("Shared pack destination is a directory: " + pair.Target);
            if (File.Exists(pair.Target) && !string.Equals(Hash(pair.Source), Hash(pair.Target), StringComparison.OrdinalIgnoreCase))
                throw new IOException("A different shared game file already exists; it was left untouched: " + pair.Target);
        }
        // Keep every source until all copies complete, so a copy failure cannot lose the staged instance.
        foreach (var pair in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Target)!);
            File.Copy(pair.Source, pair.Target, overwrite: true);
        }
        foreach (var pair in files) File.Delete(pair.Source);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
