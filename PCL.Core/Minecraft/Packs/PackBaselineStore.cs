using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using PCL.Core.Utils;

namespace PCL.Core.Minecraft.Packs;

/// <summary>Stores pack provenance outside the Minecraft instance so it does not leak into exported game files.</summary>
public sealed class PackBaselineStore
{
    private const int CurrentSchema = 1;
    private readonly string _root;
    private readonly object _gate = new();

    public PackBaselineStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public PackBaseline Capture(string gameRoot, IEnumerable<string> authoredRelativePaths, string source,
        string? projectId, string? versionId, Func<string, string?>? identityForPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentNullException.ThrowIfNull(authoredRelativePaths);
        var root = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = new SortedDictionary<string, PackFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in authoredRelativePaths)
        {
            PackUpdatePlanner.ValidateRelativePath(rawPath);
            var relative = rawPath.Replace('\\', '/');
            var fullPath = PackUpdatePlanner.ResolveWithin(root, relative);
            if (!File.Exists(fullPath)) continue; // skipped optional downloads are not in the installed baseline
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Cannot record pack file through reparse point '{relative}'.");
            files[relative] = new PackFileEntry
            {
                Path = relative,
                Sha256 = PackUpdatePlanner.HashFile(fullPath),
                Length = new FileInfo(fullPath).Length,
                Identity = identityForPath?.Invoke(relative) ?? $"path:{relative}",
            };
        }
        return new PackBaseline
        {
            SchemaVersion = CurrentSchema,
            Source = source,
            ProjectId = projectId,
            VersionId = versionId,
            Files = files,
        };
    }

    public void Save(string instancePath, PackBaseline baseline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.SchemaVersion != CurrentSchema) throw new InvalidDataException("Unsupported pack baseline schema.");
        var path = GetPath(instancePath);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var json = JsonSerializer.Serialize(baseline, JsonCompat.SerializerOptions);
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    public PackBaseline? Load(string instancePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        var path = GetPath(instancePath);
        lock (_gate)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var baseline = JsonSerializer.Deserialize<PackBaseline>(File.ReadAllText(path), JsonCompat.SerializerOptions);
                if (baseline is null || baseline.SchemaVersion != CurrentSchema || baseline.Files is null) return null;
                // Reject malformed or path-escaping persisted data before it reaches the planner.
                foreach (var file in baseline.Files.Values) PackUpdatePlanner.ValidateRelativePath(file.Path);
                return baseline;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public bool Delete(string instancePath)
    {
        var path = GetPath(instancePath);
        lock (_gate)
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }

    /// <summary>Prepare a baseline write for the same durable transaction as the game files.</summary>
    public TransactionFileChange PrepareTransaction(string instancePath, PackBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.SchemaVersion != CurrentSchema) throw new InvalidDataException("Unsupported pack baseline schema.");
        foreach (var entry in baseline.Files.Values) PackUpdatePlanner.ValidateRelativePath(entry.Path);
        var path = GetPath(instancePath);
        Directory.CreateDirectory(_root);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(baseline, JsonCompat.SerializerOptions));
        return new("metadata", Path.GetRelativePath(_root, path).Replace('\\', '/'),
            File.Exists(path) ? PackUpdatePlanner.HashFile(path) : null,
            DurableFileTransaction.Hash(bytes), Content: bytes, IsMetadata: true);
    }

    public string TransactionMetadataRoot => _root;

    private string GetPath(string instancePath)
    {
        var fullInstance = Path.GetFullPath(instancePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fullInstance.ToUpperInvariant())))
            .ToLowerInvariant();
        return Path.Combine(_root, "PackBaselines", digest + ".json");
    }
}
