using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PCL.Core.Minecraft.ResourceProject;

public sealed record RecycledResourceEntry(
    string Id,
    string Name,
    string OriginalPath,
    bool IsDirectory,
    DateTimeOffset DeletedUtc);

/// <summary>Instance-local recoverable trash for resource files and folders.</summary>
public static class ResourceRecycleBinService
{
    private const int MaxMetadataBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Metadata(int Schema, string Root, string RelativePath, string Name, bool IsDirectory, DateTimeOffset DeletedUtc);

    public static IReadOnlyList<RecycledResourceEntry> List(string resourceRoot, string recycleRoot)
    {
        var root = NormalizeRoot(resourceRoot);
        if (!Directory.Exists(recycleRoot)) return Array.Empty<RecycledResourceEntry>();
        EnsureNotReparsePoint(recycleRoot);
        var result = new List<RecycledResourceEntry>();
        foreach (var box in Directory.EnumerateDirectories(recycleRoot))
        {
            try
            {
                var entry = ReadEntry(root, recycleRoot, Path.GetFileName(box));
                result.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Invalid or foreign records remain on disk and are not exposed as restorable items.
            }
        }
        return result.OrderByDescending(static entry => entry.DeletedUtc).ToList();
    }

    public static IReadOnlyList<RecycledResourceEntry> RecycleMany(string resourceRoot, string recycleRoot, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = NormalizeRoot(resourceRoot);
        var sources = paths.Select(path => ValidateSource(root, path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0) return Array.Empty<RecycledResourceEntry>();
        Directory.CreateDirectory(recycleRoot);
        EnsureNotReparsePoint(recycleRoot);
        var moved = new List<(string Box, string Source)>();
        var records = new List<RecycledResourceEntry>();
        try
        {
            foreach (var source in sources)
            {
                var record = RecycleOne(root, recycleRoot, source);
                records.Add(record.Entry);
                moved.Add((record.Box, source));
            }
            return records;
        }
        catch (Exception operationError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                try { RestoreCore(root, recycleRoot, Path.GetFileName(item.Box)); }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("Moving resource files to the recycle bin failed and rollback was incomplete.", new[] { operationError }.Concat(rollbackErrors));
            throw;
        }
    }

    public static RecycledResourceEntry Restore(string resourceRoot, string recycleRoot, string id)
    {
        var root = NormalizeRoot(resourceRoot);
        return RestoreCore(root, recycleRoot, id);
    }

    public static void Purge(string resourceRoot, string recycleRoot, string id)
    {
        var root = NormalizeRoot(resourceRoot);
        if (!IsId(id)) throw new IOException("Recycle record id is invalid.");
        var entry = ReadEntry(root, recycleRoot, id);
        var box = Path.Combine(recycleRoot, id);
        var payload = Path.Combine(box, "content");
        if (entry.IsDirectory) Directory.Delete(payload, recursive: true);
        else File.Delete(payload);
        Directory.Delete(box, recursive: true);
    }

    private static (string Box, RecycledResourceEntry Entry) RecycleOne(string root, string recycleRoot, string source)
    {
        var isDirectory = Directory.Exists(source);
        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) throw new IOException("Resource name is invalid.");
        var relative = Path.GetRelativePath(root, source);
        var id = Guid.NewGuid().ToString("N");
        var box = Path.Combine(recycleRoot, id);
        var payload = Path.Combine(box, "content");
        Directory.CreateDirectory(box);
        try
        {
            var metadata = new Metadata(1, root, relative, name, isDirectory, DateTimeOffset.UtcNow);
            var metadataPath = Path.Combine(box, "resource.json");
            var temporaryMetadata = Path.Combine(box, "resource.json.tmp");
            File.WriteAllText(temporaryMetadata, JsonSerializer.Serialize(metadata, JsonOptions));
            File.Move(temporaryMetadata, metadataPath);
            Move(source, payload, isDirectory);
            return (box, new RecycledResourceEntry(id, name, source, isDirectory, metadata.DeletedUtc));
        }
        catch
        {
            if (!File.Exists(payload) && !Directory.Exists(payload))
            {
                try { Directory.Delete(box, recursive: true); } catch { }
            }
            throw;
        }
    }

    private static RecycledResourceEntry RestoreCore(string root, string recycleRoot, string id)
    {
        if (!IsId(id)) throw new IOException("Recycle record id is invalid.");
        EnsureNotReparsePoint(recycleRoot);
        var entry = ReadEntry(root, recycleRoot, id);
        var box = Path.Combine(recycleRoot, id);
        var payload = Path.Combine(box, "content");
        if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
            throw new IOException("The original location now contains an item with the same name; rename it before restoring.");
        EnsureNoReparseAncestors(root, Path.GetDirectoryName(entry.OriginalPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
        Move(payload, entry.OriginalPath, entry.IsDirectory);
        try { Directory.Delete(box, recursive: true); }
        catch (Exception ex) { throw new IOException("The resource was restored, but its recycle record could not be removed.", ex); }
        return entry;
    }

    private static RecycledResourceEntry ReadEntry(string root, string recycleRoot, string id)
    {
        if (!IsId(id)) throw new IOException("Recycle record id is invalid.");
        EnsureNotReparsePoint(recycleRoot);
        var box = Path.Combine(recycleRoot, id);
        EnsureWithin(recycleRoot, box);
        EnsureNotReparsePoint(box);
        var metadataPath = Path.Combine(box, "resource.json");
        var payload = Path.Combine(box, "content");
        EnsureWithin(recycleRoot, metadataPath);
        EnsureWithin(recycleRoot, payload);
        if (!File.Exists(metadataPath) || new FileInfo(metadataPath).Length > MaxMetadataBytes)
            throw new IOException("Recycle record metadata is missing or too large.");
        var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath), JsonOptions)
                       ?? throw new IOException("Recycle record metadata is invalid.");
        if (metadata.Schema != 1 || !PathsEqual(root, NormalizeRoot(metadata.Root)))
            throw new IOException("Recycle record belongs to another resource directory or schema.");
        var original = Path.GetFullPath(Path.Combine(root, metadata.RelativePath));
        EnsureWithin(root, original);
        if (!string.Equals(Path.GetFileName(original), metadata.Name, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recycle record name does not match its destination.");
        EnsureNotReparsePoint(payload);
        if (metadata.IsDirectory ? !Directory.Exists(payload) : !File.Exists(payload))
            throw new IOException("Recycle record payload is missing or has changed type.");
        return new RecycledResourceEntry(id, metadata.Name, original, metadata.IsDirectory, metadata.DeletedUtc);
    }

    private static string ValidateSource(string root, string source)
    {
        var full = Path.GetFullPath(source);
        EnsureWithin(root, full);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("Resource to recycle was not found.", full);
        EnsureNoReparseAncestors(root, full);
        return full;
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Resource root is required.", nameof(root));
        return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void EnsureWithin(string root, string path)
    {
        var normalizedRoot = NormalizeRoot(root);
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (PathsEqual(normalizedRoot, normalizedPath)) throw new IOException("The resource root itself cannot be recycled.");
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Resource path escapes its resource root.");
    }

    private static void EnsureNoReparseAncestors(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var normalizedRoot = NormalizeRoot(root);
        while (!PathsEqual(current, normalizedRoot))
        {
            EnsureNotReparsePoint(current);
            current = Path.GetDirectoryName(current) ?? throw new IOException("Resource path is outside its root.");
        }
        EnsureNotReparsePoint(normalizedRoot);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked files and directories cannot be moved to or from the resource recycle bin.");
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsId(string? id)
        => id is { Length: 32 } && id.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Move(string source, string destination, bool isDirectory)
    {
        if (isDirectory) Directory.Move(source, destination);
        else File.Move(source, destination);
    }
}
