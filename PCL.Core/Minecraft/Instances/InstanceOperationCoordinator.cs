using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.Minecraft.ResourceCollections;

namespace PCL.Core.Minecraft.Instances;

/// <summary>Cooperating-process coordination. This does not lock an externally launched game.</summary>
public static class InstanceOperationCoordinator
{
    public static IDisposable Acquire(string coordinationDirectory, params string[] roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinationDirectory);
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Length == 0) throw new ArgumentException("At least one operation root is required.", nameof(roots));
        var identities = roots.Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var directory = Path.GetFullPath(coordinationDirectory);
        RejectLinks(directory);
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var handles = new List<FileStream>();
        try
        {
            foreach (var identity in identities)
            {
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())));
                var path = Path.Combine(directory, digest + ".lock");
                RejectLinks(path);
                // Keep the empty lock file after release; deleting it would allow inode races.
                handles.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            return new Lease(handles);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.TrimEndingDirectorySeparator(new DirectoryInfo(Path.GetFullPath(path)).FullName);
        RejectLinks(full);
        // Expand the existing ancestor too: a not-yet-created child under PROGRA~1 must share
        // the same operation gate as that child under its long-name spelling.
        var suffix = new Stack<string>();
        var ancestor = full;
        while (!Directory.Exists(ancestor))
        {
            var parent = Path.GetDirectoryName(ancestor);
            if (parent is null) return full;
            suffix.Push(Path.GetFileName(ancestor));
            ancestor = parent;
        }
        ancestor = ResourceCollectionPaths.ExpandLongName(ancestor, out var unresolved);
        if (unresolved) throw new IOException("Unable to resolve a short-name operation root safely.");
        while (suffix.Count > 0) ancestor = Path.Combine(ancestor, suffix.Pop());
        return Path.TrimEndingDirectorySeparator(ancestor);
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Operation coordination cannot follow links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class Lease(List<FileStream> handles) : IDisposable
    {
        public void Dispose()
        {
            lock (handles)
            {
                foreach (var handle in handles) handle.Dispose();
                handles.Clear();
            }
        }
    }
}
