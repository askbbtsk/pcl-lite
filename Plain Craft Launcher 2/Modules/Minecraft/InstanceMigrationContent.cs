using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using PCL.Core.Minecraft.Packs;

namespace PCL;

/// <summary>Copies only explicitly selected game content; no launcher preferences, Java paths, caches or identities.</summary>
internal static class InstanceMigrationContent
{
    internal static void Copy(string sourceRoot, string targetRoot, IReadOnlyList<string> selected, CancellationToken token)
    {
        foreach (var relative in selected)
        {
            token.ThrowIfCancellationRequested();
            if (relative is not ("config" or "options.txt" or "resourcepacks" or "shaderpacks" or "saves"))
                throw new InvalidDataException("Unexpected optional migration path.");
            var source = Path.Combine(sourceRoot, relative);
            PackExportContent.CheckAncestors(sourceRoot, source);
            var files = CaptureFiles(sourceRoot, relative, token);
            var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var from = Path.Combine(sourceRoot, file); var to = Path.Combine(targetRoot, file);
                PackExportContent.CheckAncestors(sourceRoot, from);
                PackExportContent.CheckAncestors(targetRoot, to);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = input.Length; var modified = File.GetLastWriteTimeUtc(from);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[64 * 1024]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); digest.AppendData(buffer, 0, count); }
                    output.Flush(flushToDisk: true);
                }
                if (input.Length != length || File.GetLastWriteTimeUtc(from) != modified)
                    throw new IOException("Source content changed during migration: " + file);
                var hash = Convert.ToHexString(digest.GetHashAndReset());
                using var verify = new FileStream(to, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (verify.Length != length || Convert.ToHexString(SHA256.HashData(verify)) != hash)
                    throw new IOException("Copied content verification failed: " + file);
                captured[file] = hash;
            }
            // A changing tree is not a snapshot. Preserve the source and abort the uncommitted target.
            if (!files.SequenceEqual(CaptureFiles(sourceRoot, relative, token), StringComparer.OrdinalIgnoreCase))
                throw new IOException("Source content tree changed during migration: " + relative);
            foreach (var pair in captured)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(sourceRoot, pair.Key);
                PackExportContent.CheckAncestors(sourceRoot, path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Convert.ToHexString(SHA256.HashData(stream)) != pair.Value)
                    throw new IOException("Source content changed during migration: " + pair.Key);
            }
        }
    }

    private static string[] CaptureFiles(string root, string relative, CancellationToken token)
    {
        var from = Path.Combine(root, relative);
        if (File.Exists(from)) return [relative];
        if (!Directory.Exists(from)) return [];
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(from);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            foreach (var child in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var path = Path.GetRelativePath(root, child);
                if (PackExportContent.IsInternalPath(path)) continue;
                PackExportContent.CheckAncestors(root, child);
                if (Directory.Exists(child)) pending.Push(child); else files.Add(path);
            }
        }
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
