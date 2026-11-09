using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PCL.Core.Minecraft.Java;
using PCL.Core.Minecraft.Java.Parser;

namespace PCL.Core.Minecraft.Packs;

public sealed record InstanceJavaBundle(string RuntimeRoot, string RelativeExePath, JavaInstallation Installation);

public static class InstanceJavaBundler
{
    public static InstanceJavaBundle? Find(string instanceDirectory, string? preferredExe = null,
        Func<string, JavaInstallation?>? parse = null, Func<JavaInstallation, bool>? compatible = null)
    {
        var root = Path.GetFullPath(instanceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root)) return null;
        PackExportContent.CheckAncestors(root, root);
        parse ??= new PeHeaderParser().Parse;
        var candidates = new List<InstanceJavaBundle>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if (depth > 5 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var executable = Path.Combine(directory, "bin", "java.exe");
            if (depth > 0 && File.Exists(executable) && Directory.Exists(Path.Combine(directory, "lib")))
            {
                PackExportContent.CheckAncestors(root, executable);
                var installation = parse(executable);
                if (installation is not null && installation.Version.Major > 0 && (compatible?.Invoke(installation) ?? true))
                    candidates.Add(new InstanceJavaBundle(directory, Path.GetRelativePath(root, executable), installation));
                continue;
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var relative = Path.GetRelativePath(root, child);
                if (PackExportContent.IsInternalPath(relative)) continue;
                if (new[] { "mods", "saves", "resourcepacks", "shaderpacks", "assets", "libraries", "config", "screenshots" }
                    .Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;
                pending.Push((child, depth + 1));
            }
        }
        return candidates.OrderByDescending(candidate => preferredExe is not null
                && string.Equals(Path.GetFullPath(candidate.Installation.JavaExePath), Path.GetFullPath(preferredExe), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidate => candidate.Installation.Version)
            .ThenBy(candidate => candidate.RelativeExePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    public static void Copy(InstanceJavaBundle candidate, string instanceDirectory, string destination, CancellationToken token = default)
    {
        var executable = PackExportContent.ResolveInstanceJava(instanceDirectory, candidate.RelativeExePath);
        var runtime = Directory.GetParent(Path.GetDirectoryName(executable)!)!.FullName;
        if (!string.Equals(runtime, Path.GetFullPath(candidate.RuntimeRoot), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executable) || !Directory.Exists(Path.Combine(runtime, "lib")))
            throw new InvalidDataException("The instance Java runtime changed before export.");
        PackExportContent.CopyTree(runtime, destination, token);
    }
}
