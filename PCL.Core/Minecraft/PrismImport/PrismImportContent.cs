using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Minecraft.PrismImport;

public sealed record PrismCopiedFile(string SourcePath, string RelativeTargetPath, string Sha256);
public sealed record PrismCopyResult(IReadOnlyList<PrismCopiedFile> Files, IReadOnlyList<string> Skipped);

public static class PrismImportContent
{
    private static readonly HashSet<string> AllowedGameEntries = new(StringComparer.OrdinalIgnoreCase)
    { "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "saves", "options.txt", "optionsof.txt",
        "optionsshaders.txt", "servers.dat", "datapacks", "kubejs", "scripts", "resources" };

    /// <summary>Copies an explicit instance snapshot, excluding launcher state, credentials, logs and runtime executables.</summary>
    public static PrismCopyResult Copy(PrismReadResult source, string stagingDirectory, CancellationToken token = default)
    {
        var stage = Path.GetFullPath(stagingDirectory); PrismInstanceReader.RejectLinks(stage);
        var skipped = new List<string>();
        var selections = new List<(string Source, string Relative)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(source.GameDirectory))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (!AllowedGameEntries.Contains(name) || PackExportContent.IsInternalPath(name))
            { skipped.Add(Path.GetRelativePath(source.InstanceDirectory, entry)); continue; }
            Select(entry, name, selections, skipped, token);
        }
        var localLibraries = Path.Combine(source.InstanceDirectory, "libraries");
        PrismInstanceReader.RejectLinks(localLibraries);
        if (Directory.Exists(localLibraries)) Select(localLibraries, "libraries", selections, skipped, token);
        var copied = new List<PrismCopiedFile>();
        foreach (var file in selections)
        {
            token.ThrowIfCancellationRequested(); PrismInstanceReader.RejectLinks(file.Source);
            var target = PackUpdatePlanner.ResolveWithin(stage, file.Relative);
            PrismInstanceReader.RejectLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
            var size = input.Length; var modified = File.GetLastWriteTimeUtc(file.Source);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); }
                output.Flush(flushToDisk: true);
            }
            if (input.Length != size || File.GetLastWriteTimeUtc(file.Source) != modified)
                throw new IOException("Prism source changed while copying: " + file.Source);
            var expected = Convert.ToHexString(hash.GetHashAndReset());
            using var verified = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (verified.Length != size || Convert.ToHexString(SHA256.HashData(verified)) != expected)
                throw new IOException("Prism copied content verification failed: " + file.Relative);
            copied.Add(new(file.Source, file.Relative, expected));
        }
        VerifySource(copied, token);
        return new(copied, skipped);
    }

    public static void VerifySource(IReadOnlyList<PrismCopiedFile> files, CancellationToken token = default)
    {
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested(); PrismInstanceReader.RejectLinks(file.SourcePath);
            using var stream = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(stream)) != file.Sha256)
                throw new IOException("Prism source changed before commit: " + file.SourcePath);
        }
    }

    public static void VerifySource(PrismReadResult source, PrismCopyResult copied, CancellationToken token = default)
    {
        var current = new List<(string Source, string Relative)>(); var ignored = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(source.GameDirectory))
            if (AllowedGameEntries.Contains(Path.GetFileName(entry))) Select(entry, Path.GetFileName(entry), current, ignored, token);
        var libraries = Path.Combine(source.InstanceDirectory, "libraries"); PrismInstanceReader.RejectLinks(libraries);
        if (Directory.Exists(libraries)) Select(libraries, "libraries", current, ignored, token);
        if (!current.Select(file => file.Source).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).SequenceEqual(
                copied.Files.Select(file => file.SourcePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Prism source file tree changed before commit.");
        VerifySource(copied.Files, token);
    }

    private static void Select(string source, string relative, List<(string Source, string Relative)> files,
        List<string> skipped, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PrismInstanceReader.RejectLinks(source);
        if (PackExportContent.IsInternalPath(relative)) { skipped.Add(relative); return; }
        if (!Directory.Exists(source))
        {
            if (relative.Replace('\\', '/').StartsWith("libraries/", StringComparison.OrdinalIgnoreCase)
                && !relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) { skipped.Add(relative); return; }
            files.Add((source, relative)); return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(source).OrderBy(path => path, StringComparer.Ordinal))
            Select(child, Path.Combine(relative, Path.GetFileName(child)), files, skipped, token);
    }
}
