using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Enhancements;

/// <summary>
/// Reads actual files using the host's resolved library list. It deliberately does not implement a second
/// version inheritance, rule evaluator, Java selector, downloader, or repair engine.
/// </summary>
public static class InstanceEnhancementAnalyzer
{
    public const string ParserVersion = "1";

    /// <summary>Run blocking file hashing outside the WPF dispatcher.</summary>
    public static Task<InstanceResolvedReport> AnalyzeAsync(EnhancementAnalysisInput input,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Analyze(input, cancellationToken), cancellationToken);

    public static InstanceResolvedReport Analyze(EnhancementAnalysisInput input, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.InstanceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.EffectiveVersionJson);
        token.ThrowIfCancellationRequested();
        using var version = JsonDocument.Parse(input.EffectiveVersionJson);
        if (version.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Effective version JSON must be an object.");
        var warnings = new List<string>();
        var before = CaptureSources(input, token);
        var entries = new List<LibraryHealthEntry>();
        // Order is intentionally preserved, including duplicate paths with different declarations.
        foreach (var library in input.Libraries)
        {
            token.ThrowIfCancellationRequested();
            entries.Add(CheckLibrary(input, library, token));
        }
        var after = CaptureSources(input, token);
        var stable = before.SequenceEqual(after, StringComparer.Ordinal);
        if (!stable) warnings.Add("Source inputs changed during analysis; repeat the check before using this report.");
        return new InstanceResolvedReport
        {
            Fingerprint = Fingerprint(input, before),
            Components = IdentifyComponents(input, warnings),
            Libraries = entries,
            Warnings = warnings,
            InputsStable = stable,
        };
    }

    /// <summary>Includes ordered effective input, resolver facts, source hashes, platform and optional Java identity.</summary>
    public static string CalculateFingerprint(EnhancementAnalysisInput input, CancellationToken token = default)
        => Fingerprint(input, CaptureSources(input, token));

    private static string Fingerprint(EnhancementAnalysisInput input, IReadOnlyList<string> sources)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ParserVersion, input.AdapterVersion, input.EffectiveVersionJson, input.MinecraftVersion,
            input.SourceMetadataSha256, input.Platform, input.Architecture, input.Java,
            InstanceDirectory = EnhancementPaths.Normalize(input.InstanceDirectory),
            input.Libraries, input.DeclaredComponents, input.AllowedLibraryRoots, Sources = sources,
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static List<string> CaptureSources(EnhancementAnalysisInput input, CancellationToken token)
    {
        var result = new List<string>();
        foreach (var path in input.SourceFiles)
        {
            token.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(path);
            EnhancementPaths.RejectLinks(full);
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Resolution source is too large: " + full);
            result.Add(full + "\n" + ReadHashes(stream, token).Sha256);
        }
        return result;
    }

    private static LibraryHealthEntry CheckLibrary(EnhancementAnalysisInput input, EnhancementLibrary library,
        CancellationToken token)
    {
        var entry = new LibraryHealthEntry { Path = library.Path, Name = library.Name };
        try
        {
            var full = Path.GetFullPath(library.Path);
            if (!EnhancementPaths.IsWithin(input.InstanceDirectory, full)
                && !input.AllowedLibraryRoots.Any(root => EnhancementPaths.IsWithin(root, full)))
                return entry with { Status = LibraryHealthStatus.UnsafePath, Error = "Library path is outside allowed roots." };
            EnhancementPaths.RejectLinks(full);
            if (Directory.Exists(full)) return entry with { Status = LibraryHealthStatus.Unavailable, Error = "A directory occupies the expected file path." };
            if (!File.Exists(full)) return entry with { Status = LibraryHealthStatus.Missing };
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            var modified = File.GetLastWriteTimeUtc(full);
            var hashes = ReadHashes(stream, token);
            if (stream.Length != length || File.GetLastWriteTimeUtc(full) != modified)
                return entry with { Status = LibraryHealthStatus.Changed, Error = "File changed during verification." };
            entry = entry with { ActualLength = length, ActualSha256 = hashes.Sha256 };
            var hasSha1 = IsDigest(library.Sha1, 40);
            var hasSha256 = IsDigest(library.Sha256, 64);
            if ((!string.IsNullOrWhiteSpace(library.Sha1) && !hasSha1)
                || (!string.IsNullOrWhiteSpace(library.Sha256) && !hasSha256))
                return entry with { Status = LibraryHealthStatus.Unverified, Error = "The declaration contains an invalid digest." };
            if ((library.Size is > 0 && library.Size.Value != length)
                || (hasSha1 && !string.Equals(library.Sha1, hashes.Sha1, StringComparison.OrdinalIgnoreCase))
                || (hasSha256 && !string.Equals(library.Sha256, hashes.Sha256, StringComparison.OrdinalIgnoreCase)))
                return entry with { Status = LibraryHealthStatus.Damaged };
            return entry with { Status = hasSha1 || hasSha256 ? LibraryHealthStatus.Valid : LibraryHealthStatus.Unverified };
        }
        catch (Exception ex) when (EnhancementPaths.IsExpectedFailure(ex))
        {
            return entry with
            {
                Status = ex is InvalidDataException ? LibraryHealthStatus.UnsafePath : LibraryHealthStatus.Unavailable,
                Error = ex.Message,
            };
        }
    }

    private static bool IsDigest(string? value, int length) => value is not null && value.Length == length
        && value.All(static character => char.IsAsciiHexDigit(character));

    private static (string Sha256, string Sha1) ReadHashes(Stream stream, CancellationToken token)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            sha256.AppendData(buffer, 0, count);
            sha1.AppendData(buffer, 0, count);
        }
        return (Convert.ToHexString(sha256.GetHashAndReset()), Convert.ToHexString(sha1.GetHashAndReset()));
    }

    private static List<ComponentDescriptor> IdentifyComponents(EnhancementAnalysisInput input, List<string> warnings)
    {
        var result = new List<ComponentDescriptor>();
        if (!string.IsNullOrWhiteSpace(input.MinecraftVersion))
            result.Add(new() { Id = "net.minecraft", Version = input.MinecraftVersion, Source = "effective-version" });
        foreach (var library in input.Libraries)
        {
            var parts = library.Name?.Split(':');
            if (parts is null || parts.Length < 3) continue;
            var id = (parts[0], parts[1]) switch
            {
                ("net.fabricmc", "fabric-loader") => "net.fabricmc.fabric-loader",
                ("net.fabricmc", "intermediary") => "net.fabricmc.intermediary",
                ("net.minecraftforge", "forge") => "net.minecraftforge.forge",
                ("net.neoforged", "neoforge") => "net.neoforged.neoforge",
                _ => null,
            };
            if (id is null || result.Any(component => component.Id == id && component.Version == parts[2])) continue;
            result.Add(new() { Id = id, Version = parts[2], Source = "effective-library", DependencyOnly = id.EndsWith(".intermediary", StringComparison.Ordinal) });
        }
        foreach (var declared in input.DeclaredComponents)
        {
            var index = result.FindIndex(component => component.Id == declared.Id && component.Version == declared.Version);
            if (index >= 0)
            {
                result[index] = declared;
                if (declared.Disabled) warnings.Add("Disabled declaration remains present in the effective host configuration: " + declared.Id);
            }
            else
            {
                result.Add(declared);
                if (!declared.Disabled) warnings.Add("Unverified component declaration: " + declared.Id + " " + declared.Version);
            }
        }
        return result;
    }
}
