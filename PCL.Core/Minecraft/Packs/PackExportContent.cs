using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCL.Core.Minecraft.Packs;

public static class PackExportContent
{
    public static bool IsInternalPath(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => new[] { "PCLLiteData", "ResourceRecycle", "WorldBackups", "PackRestorePoints", "PackBaselines", "PackUpdateBackups",
            ".git", "logs", "crash-reports", "quickPlay", "webcache", "Cache" }.Contains(part, StringComparer.OrdinalIgnoreCase)
            || part.Contains(".pcl-update-", StringComparison.OrdinalIgnoreCase)
            || part.Contains(".restore-stage-", StringComparison.OrdinalIgnoreCase)
            || part.Contains(".restore-rollback-", StringComparison.OrdinalIgnoreCase)
            || part.StartsWith(".pcl-import-", StringComparison.OrdinalIgnoreCase)
            || part.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || part.Equals("accounts.json", StringComparison.OrdinalIgnoreCase)
            || part.Equals("launcher_accounts.json", StringComparison.OrdinalIgnoreCase)
            || part.Equals("ModpackOrigin.json", StringComparison.OrdinalIgnoreCase)
            || part.Equals("CloneOrigin.json", StringComparison.OrdinalIgnoreCase)
            || part.Equals("Setup.ini", StringComparison.OrdinalIgnoreCase)
            || part.Equals("usercache.json", StringComparison.OrdinalIgnoreCase));
    }

    public static string ResolveInstanceJava(string instanceDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Java must use an instance-relative path.");
        var root = Path.GetFullPath(instanceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Java path escapes the instance directory.");
        CheckAncestors(root, full);
        return full;
    }

    public static void CheckAncestors(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Pack export cannot follow links.");
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), boundary, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Path is outside its source root.");
        }
    }

    public static void CopyTree(string source, string destination, CancellationToken token = default)
    {
        if (!Directory.Exists(source)) return;
        var root = Path.GetFullPath(source);
        if (IsInternalPath(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)))) return;
        var targetRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        if (targetRoot.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || targetRoot.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Export destination cannot be inside its source.");
        CheckAncestors(root, root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(root, child);
                if (IsInternalPath(relative)) continue;
                CheckAncestors(root, child);
                var target = Path.Combine(destination, relative);
                if (Directory.Exists(child)) pending.Push(child);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(child, target, true);
                }
            }
        }
    }

    public static bool ReadIsolation(string? declared, string? legacy, bool fallback = true)
        => bool.TryParse(declared, out var result) ? result : legacy switch { "1" => true, "2" => false, _ => fallback };
}
