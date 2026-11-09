using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace PCL.Core.Minecraft.ResourceCollections;

/// <summary>Stores local-only path indices under the supplied launcher's data root.</summary>
public sealed class ResourceCollectionStore
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string DirectoryPath { get; }
    public ResourceCollectionStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DirectoryPath = Path.Combine(ResourceCollectionPaths.Normalize(dataDirectory), "ResourceCollections");
    }

    public IReadOnlyList<ResourceCollectionReadResult> List()
    {
        ResourceCollectionPaths.RejectLinks(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return [];
        return Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(10000)
            .Select(path => Load(Guid.ParseExact(Path.GetFileNameWithoutExtension(path), "N"))).ToArray();
    }

    public ResourceCollectionReadResult Load(Guid id)
    {
        try
        {
            var path = PathFor(id);
            ResourceCollectionPaths.RejectLinks(path);
            if (!File.Exists(path)) return new(ResourceCollectionReadStatus.Missing);
            var bytes = ReadBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("schemaVersion", out var schema)
                || !schema.TryGetInt32(out var value)) return new(ResourceCollectionReadStatus.Invalid, ContentSha256: hash);
            if (value != 1) return new(ResourceCollectionReadStatus.UnsupportedSchema, ContentSha256: hash);
            var document = JsonSerializer.Deserialize<ResourceCollectionDocument>(bytes, Options);
            return document is not null && document.Id == id && IsValid(document)
                ? new(ResourceCollectionReadStatus.Valid, document, hash)
                : new(ResourceCollectionReadStatus.Invalid, ContentSha256: hash);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { return new(ResourceCollectionReadStatus.Invalid, Error: ex.Message); }
        catch (Exception ex) when (ResourceCollectionPaths.ExpectedFailure(ex))
        { return new(ResourceCollectionReadStatus.Unavailable, Error: ex.Message); }
    }

    /// <summary>Pass the hash from Load when editing. A null expected hash creates a new collection only.</summary>
    public void Save(ResourceCollectionDocument document, string? expectedContentSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsValid(document)) throw new InvalidDataException("Invalid collection document.");
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(document.Id);
        ResourceCollectionPaths.RejectLinks(path);
        Directory.CreateDirectory(DirectoryPath);
        var lockPath = Path.Combine(DirectoryPath, ".write.lock");
        ResourceCollectionPaths.RejectLinks(lockPath);
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var before = File.Exists(path) ? Hash(path) : null;
        if (!string.Equals(before, expectedContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Collection changed; reload before saving.");
        if (before is not null && Load(document.Id).Status != ResourceCollectionReadStatus.Valid)
            throw new IOException("Invalid or newer collection content was preserved.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Collection is too large.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            cancellationToken.ThrowIfCancellationRequested();
            ResourceCollectionPaths.RejectLinks(path);
            var current = File.Exists(path) ? Hash(path) : null;
            if (!string.Equals(before, current, StringComparison.OrdinalIgnoreCase)) throw new IOException("Collection changed while saving.");
            File.Move(temp, path, overwrite: before is not null);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string PathFor(Guid id) => id != Guid.Empty ? Path.Combine(DirectoryPath, id.ToString("N") + ".json")
        : throw new ArgumentException("Collection identity is required.", nameof(id));

    private static bool IsValid(ResourceCollectionDocument value) => value.SchemaVersion == 1 && value.Id != Guid.Empty
        && !string.IsNullOrWhiteSpace(value.Name) && value.Name.Length <= 256 && value.Entries is { Count: <= 10000 }
        && value.Notices is not null && value.Entries.TrueForAll(entry => entry is not null
            && entry.OriginalPaths is { Count: > 0 } && entry.OriginalPaths.All(path => path is not null));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(ReadBytes(path)));

    private static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Collection is too large.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
}
