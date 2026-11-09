using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Threading;

namespace PCL.Core.Minecraft.Packs;

public static class PackExportArchive
{
    public static void Create(string sourceDirectory, string outputPath, CancellationToken token = default)
    {
        var destination = Path.GetFullPath(outputPath);
        var source = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive cannot be created inside its source directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in Files(source, token))
                {
                    token.ThrowIfCancellationRequested();
                    PackExportContent.CheckAncestors(sourceDirectory, file);
                    var entry = archive.CreateEntry(Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/'), CompressionLevel.Optimal);
                    using var input = File.OpenRead(file);
                    using var output = entry.Open();
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEnumerable<string> Files(string root, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            PackExportContent.CheckAncestors(root, directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                PackExportContent.CheckAncestors(root, entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else yield return entry;
            }
        }
    }
}
