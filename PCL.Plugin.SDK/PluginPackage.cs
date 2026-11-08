using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Globalization;
using PCL.Mixin;

namespace PCL.Plugin.SDK;

public sealed record PluginManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public string Description { get; init; } = "";
    public int ApiVersion { get; init; } = PluginApiVersion.Current;
    /// <summary>Optional SemVer range for supported PCL Core versions.</summary>
    public string? PclCoreVersion { get; init; }
    public required string EntryAssembly { get; init; }
    public string EntryType { get; init; } = "Plugin";
    public string? MixinConfig { get; init; }
    public IReadOnlyList<string> MixinConfigs { get; init; } = [];
    /// <summary>Optional experimental Mixin groups. New groups are disabled until a user enables them.</summary>
    public IReadOnlyList<PluginExperimentalFeature> ExperimentalFeatures { get; init; } = [];
    public Dictionary<string, string> Dependencies { get; init; } = new(StringComparer.Ordinal);
    public string? Website { get; init; }
    /// <summary>Optional package-relative icon path (PNG, JPEG, SVG, or ICO).</summary>
    public string? Icon { get; init; }
    /// <summary>Optional localized display names keyed by BCP-47 language code.</summary>
    public Dictionary<string, string> LocalizedNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Optional localized descriptions keyed by BCP-47 language code.</summary>
    public Dictionary<string, string> LocalizedDescriptions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Optional direct GitHub release asset URL used to corroborate package hash and publish time.</summary>
    public string? ReleaseAssetUrl { get; init; }
}

/// <summary>A named, independently selectable group of experimental Mixin configuration files.</summary>
public sealed record PluginExperimentalFeature
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required IReadOnlyList<string> MixinConfigs { get; init; }
}

public sealed record PluginPackageSignature(int SchemaVersion, string Algorithm, string Certificate,
    string Signature, string CertificateThumbprint, DateTimeOffset SignedAtUtc);

public sealed record PluginPackageCheck(PluginManifest Manifest, bool IsSigned, bool SignatureValid,
    string? SignerThumbprint, string? Signer, DateTimeOffset? SignedAtUtc, string ContentHash, string? Error)
{
    public bool IsValid => Error is null && (!IsSigned || SignatureValid);
    public PluginGitHubProvenance? GitHubProvenance { get; init; }
    public string? PreviousSignerThumbprint { get; init; }
    public string? CompatibilityWarning { get; init; }
    public string? PreviousVersion { get; init; }
    public string? PackageSha256 { get; init; }
    public string? PreviousInstallSourceType { get; init; }
    public string? PreviousInstallSourceUrl { get; init; }
    public string? PreviousPackageSha256 { get; init; }
    public string? CurrentInstallSourceType { get; init; }
    public string? CurrentInstallSourceUrl { get; init; }
    public string? CurrentInstallPackageUrl { get; init; }
    public bool SignerChanged => PreviousSignerThumbprint is not null &&
        (!IsSigned || !string.Equals(PreviousSignerThumbprint, SignerThumbprint, StringComparison.OrdinalIgnoreCase));
    public bool SourceChanged => PreviousVersion is not null && CurrentInstallSourceType is not null &&
        (PreviousInstallSourceType is null ||
         !string.Equals(PreviousInstallSourceType, CurrentInstallSourceType, StringComparison.Ordinal) ||
         !string.Equals(PreviousInstallSourceUrl, CurrentInstallSourceUrl, StringComparison.Ordinal));
    public bool PackageChanged => PreviousVersion is not null &&
        PluginVersionRange.TryCompareVersions(PreviousVersion, Manifest.Version, out var versionComparison) &&
        versionComparison == 0 && PreviousPackageSha256 is not null && PackageSha256 is not null &&
        !string.Equals(PreviousPackageSha256, PackageSha256, StringComparison.OrdinalIgnoreCase);
    public bool IsDowngrade => PreviousVersion is not null &&
        PluginVersionRange.TryCompareVersions(Manifest.Version, PreviousVersion, out var comparison) && comparison < 0;
}

public sealed record PluginGitHubProvenance(string Repository, string Tag, DateTimeOffset PublishedAtUtc,
    string Sha256, bool SignatureTimeAfterPublication);

/// <summary>Package format and signing helpers shared by the launcher and plugin authoring tool.</summary>
public static class PluginPackage
{
    public const string ManifestFileName = "plugin.json";
    public const string SignatureFileName = "signature.json";
    public const string Extension = ".pclplugin";
    public const int MaximumPackageEntries = 10_000;
    public const long MaximumPackageBytes = 100L * 1024 * 1024;
    public const long MaximumExpandedBytes = 500L * 1024 * 1024;
    public const int MaximumManifestBytes = 64 * 1024;
    public const int MaximumRelativePathLength = 1024;
    public const int MaximumPathSegmentLength = 128;
    public const int MaximumPathDepth = 32;
    public const long MaximumSignatureBytes = 64L * 1024;
    public const int MaximumMixinConfigurationBytes = 256 * 1024;
    public const int MaximumMixinsPerConfiguration = 512;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 32
    };

    public static async Task<PluginPackageCheck> InspectAsync(string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
            return new(default!, false, false, null, null, null, "", "Package file does not exist.");
        var info = new FileInfo(packagePath);
        if (info.Length > MaximumPackageBytes)
            return new(default!, false, false, null, null, null, "", "Package exceeds the 100 MiB limit.");

        try { return await _InspectPackageAsync(packagePath, null, null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or FormatException or OverflowException or ArgumentException)
        {
            return new(default!, false, false, null, null, null, "", $"Invalid plugin package: {ex.Message}");
        }
    }

    /// <summary>Extracts a package to a new directory while rechecking its signature and content hash.</summary>
    /// <remarks>The destination must not exist. It is removed if extraction or verification fails.</remarks>
    public static async Task<PluginPackageCheck> ExtractVerifiedAsync(string packagePath, string destinationDirectory,
        string expectedContentHash, string expectedPackageSha256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedContentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageSha256);
        if (expectedContentHash.Length != 64 || expectedContentHash.Any(character => !Uri.IsHexDigit(character)) ||
            expectedPackageSha256.Length != 64 || expectedPackageSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Expected package hashes must contain 64 hexadecimal characters.");
        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
            throw new IOException("Plugin extraction destination already exists.");
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            var check = await _InspectPackageAsync(packagePath, destinationDirectory, expectedPackageSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (check.Error is not null || !check.IsValid)
                throw new InvalidDataException(check.Error ?? "Plugin package is invalid.");
            if (!string.Equals(expectedContentHash, check.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin package contents changed after inspection.");
            return check;
        }
        catch
        {
            try { if (Directory.Exists(destinationDirectory)) Directory.Delete(destinationDirectory, recursive: true); }
            catch { }
            throw;
        }
    }

    /// <summary>Verifies installed plugin files without loading their contents into memory.</summary>
    public static async Task<PluginPackageCheck> InspectDirectoryAsync(string pluginDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(pluginDirectory))
            return new(default!, false, false, null, null, null, "", "Plugin directory does not exist.");
        try
        {
            var root = Path.GetFullPath(pluginDirectory);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Plugin installation directory cannot be a symbolic link or junction.");
            var manifestPath = Path.Combine(root, ManifestFileName);
            if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Plugin manifest cannot be a symbolic link.");
            var manifestBytesForPaths = await _ReadBoundedFileAsync(manifestPath, MaximumManifestBytes,
                cancellationToken).ConfigureAwait(false);
            var manifestForPaths = DeserializeManifest(manifestBytesForPaths);
            var mixinPaths = _GetMixinConfigurationPaths(manifestForPaths);
            var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var mixinFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(root);
            byte[]? manifestBytes = null;
            byte[]? signatureBytes = null;
            long expanded = 0;
            var entryCount = 0;
            while (pending.TryPop(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(current))
                {
                    if (++entryCount > MaximumPackageEntries)
                        throw new InvalidDataException("Plugin installation exceeds the package entry limit.");
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Plugin installation cannot contain symbolic links or junctions.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(path);
                        continue;
                    }
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (!seenNames.Add(relative))
                        throw new InvalidDataException($"Duplicate plugin file: {relative}");
                    if (relative.Equals(SignatureFileName, StringComparison.Ordinal))
                    {
                        var info = new FileInfo(path);
                        if (info.Length > MaximumSignatureBytes)
                            throw new InvalidDataException("Plugin signature metadata is too large.");
                        signatureBytes = await _ReadBoundedFileAsync(path, MaximumSignatureBytes, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }
                    ValidateRelativePath(relative);
                    var fileInfo = new FileInfo(path);
                    expanded = checked(expanded + fileInfo.Length);
                    if (expanded > MaximumExpandedBytes)
                        throw new InvalidDataException("Plugin exceeds the expanded size limit.");
                    long? captureLimit = relative.Equals(ManifestFileName, StringComparison.Ordinal)
                        ? MaximumManifestBytes
                        : mixinPaths.Contains(relative) ? MaximumMixinConfigurationBytes : null;
                    var captured = await _HashFileAsync(path, captureLimit, cancellationToken).ConfigureAwait(false);
                    entries.Add(relative, captured.Hash);
                    if (relative.Equals(ManifestFileName, StringComparison.Ordinal)) manifestBytes = captured.Bytes;
                    else if (captured.Bytes is not null) mixinFiles[relative] = captured.Bytes;
                }
            }
            return _CreateCheck(manifestBytes, entries, mixinFiles, signatureBytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or FormatException or OverflowException or ArgumentException)
        {
            return new(default!, false, false, null, null, null, "", $"Invalid plugin package: {ex.Message}");
        }
    }

    private static async Task<PluginPackageCheck> _InspectPackageAsync(string packagePath, string? extractionDirectory,
        string? expectedPackageSha256, CancellationToken cancellationToken)
    {
        var info = new FileInfo(packagePath);
        if (info.Length > MaximumPackageBytes)
            return new(default!, false, false, null, null, null, "", "Package exceeds the 100 MiB limit.");
        await using var stream = File.OpenRead(packagePath);
        if (expectedPackageSha256 is not null)
        {
            var packageHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false));
            if (!string.Equals(expectedPackageSha256, packageHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin package archive changed after inspection.");
            stream.Position = 0;
        }
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumPackageEntries)
            return new(default!, false, false, null, null, null, "", "Package contains too many entries.");
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            ValidateRelativePath(entry.FullName);
            if (!seenNames.Add(entry.FullName.Replace('\\', '/')))
                return new(default!, false, false, null, null, null, "", $"Duplicate package entry: {entry.FullName}");
            if (entry.FullName.Equals(ManifestFileName, StringComparison.Ordinal) && entry.Length > MaximumManifestBytes)
                return new(default!, false, false, null, null, null, "", "Plugin manifest exceeds the 64 KiB limit.");
            if (entry.FullName.Equals(SignatureFileName, StringComparison.Ordinal))
            {
                if (entry.Length > MaximumSignatureBytes)
                    return new(default!, true, false, null, null, null, "", "Package signature metadata is too large.");
                continue;
            }
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes)
                return new(default!, false, false, null, null, null, "", "Expanded package exceeds the 500 MiB limit.");
        }
        var manifestEntry = archive.GetEntry(ManifestFileName);
        if (manifestEntry is null)
            return new(default!, false, false, null, null, null, "", "Package manifest is missing.");
        byte[] manifestForPathsBytes;
        await using (var manifestStream = manifestEntry.Open())
        {
            var capturedManifest = await _HashStreamAsync(manifestStream, manifestEntry.Length,
                MaximumManifestBytes, null, cancellationToken).ConfigureAwait(false);
            manifestForPathsBytes = capturedManifest.Bytes!;
        }
        var manifestForPaths = DeserializeManifest(manifestForPathsBytes);
        var mixinPaths = _GetMixinConfigurationPaths(manifestForPaths);
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var mixinFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        byte[]? manifestBytes = null;
        byte[]? signatureBytes = null;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            if (entry.FullName.Equals(SignatureFileName, StringComparison.Ordinal))
            {
                if (entry.Length > MaximumSignatureBytes)
                    return new(default!, true, false, null, null, null, "", "Package signature metadata is too large.");
                await using var signatureStream = entry.Open();
                signatureBytes = await _ReadBoundedStreamAsync(signatureStream, MaximumSignatureBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (signatureBytes.LongLength != entry.Length)
                    throw new InvalidDataException("Package signature entry length is invalid.");
                if (extractionDirectory is not null)
                {
                    var signaturePath = Path.Combine(extractionDirectory, SignatureFileName);
                    await File.WriteAllBytesAsync(signaturePath, signatureBytes, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            if (!entries.TryAdd(entry.FullName, string.Empty))
                return new(default!, false, false, null, null, null, "", $"Duplicate package entry: {entry.FullName}");
            long? captureLimit = entry.FullName.Equals(ManifestFileName, StringComparison.Ordinal)
                ? MaximumManifestBytes
                : mixinPaths.Contains(entry.FullName) ? MaximumMixinConfigurationBytes : null;
            string? destination = null;
            if (extractionDirectory is not null)
            {
                destination = Path.GetFullPath(Path.Combine(extractionDirectory,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Path.GetFullPath(extractionDirectory) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe package path: {entry.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            }
            await using var input = entry.Open();
            FileStream? output = destination is null ? null : new FileStream(destination, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                var captured = await _HashStreamAsync(input, entry.Length, captureLimit, output, cancellationToken)
                    .ConfigureAwait(false);
                entries[entry.FullName] = captured.Hash;
                if (entry.FullName.Equals(ManifestFileName, StringComparison.Ordinal)) manifestBytes = captured.Bytes;
                else if (captured.Bytes is not null) mixinFiles[entry.FullName] = captured.Bytes;
            }
            finally { if (output is not null) await output.DisposeAsync().ConfigureAwait(false); }
        }
        return _CreateCheck(manifestBytes, entries, mixinFiles, signatureBytes);
    }

    private static PluginPackageCheck _CreateCheck(byte[]? manifestBytes,
        SortedDictionary<string, string> entries, IReadOnlyDictionary<string, byte[]> mixinFiles,
        byte[]? signatureBytes)
    {
        if (manifestBytes is null)
            return new(default!, false, false, null, null, null, "", "Package manifest is missing.");
        var manifest = DeserializeManifest(manifestBytes);
        var canonicalManifest = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        entries[ManifestFileName] = Convert.ToHexString(SHA256.HashData(canonicalManifest)).ToLowerInvariant();
        var contentHash = _CreateFileContentHash(entries);
        var validationError = ValidateManifest(manifest, entries.Keys);
        if (validationError is not null)
            return new(manifest, false, false, null, null, null, contentHash, validationError);
        validationError = ValidateMixinConfigurations(manifest, mixinFiles);
        if (validationError is not null)
            return new(manifest, false, false, null, null, null, contentHash, validationError);
        if (signatureBytes is null)
            return new(manifest, false, false, null, null, null, contentHash, null);
        var signature = JsonSerializer.Deserialize<PluginPackageSignature>(signatureBytes, JsonOptions);
        if (signature is null || signature.SchemaVersion != 1 || signature.Algorithm != "RSA-SHA256-PKCS1")
            return new(manifest, true, false, null, null, null, contentHash, "Unsupported package signature format.");
        var verification = VerifyFileHashes(entries, signature);
        return new(manifest, true, verification.Valid, verification.Thumbprint, verification.Signer,
            signature.SignedAtUtc, contentHash, verification.Valid ? null : verification.Error);
    }

    private static HashSet<string> _GetMixinConfigurationPaths(PluginManifest manifest)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(manifest.MixinConfig)) paths.Add(manifest.MixinConfig);
        if (manifest.MixinConfigs is not null)
            foreach (var path in manifest.MixinConfigs) if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        if (manifest.ExperimentalFeatures is not null)
            foreach (var feature in manifest.ExperimentalFeatures)
                if (feature?.MixinConfigs is not null)
                    foreach (var path in feature.MixinConfigs) if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        return paths;
    }

    private static string _CreateFileContentHash(SortedDictionary<string, string> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
            _AppendUtf8(hash, entry.Key.Replace('\\', '/') + "\0" + entry.Value + "\n");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static (bool Valid, string? Thumbprint, string? Signer, string? Error) _VerifyFiles(
        SortedDictionary<string, string> entries, PluginPackageSignature signature)
        => VerifyFileHashes(entries, signature);

    /// <summary>Verifies a package signature from per-file SHA-256 hashes.</summary>
    public static (bool Valid, string? Thumbprint, string? Signer, string? Error) VerifyFileHashes(
        IReadOnlyDictionary<string, string> fileHashes, PluginPackageSignature signature)
    {
        ArgumentNullException.ThrowIfNull(fileHashes);
        ArgumentNullException.ThrowIfNull(signature);
        try
        {
            var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in fileHashes)
            {
                ValidateRelativePath(entry.Key);
                if (entry.Value is null || entry.Value.Length != 64 || entry.Value.Any(character => !Uri.IsHexDigit(character)))
                    return (false, null, null, $"Invalid file hash: {entry.Key}");
                entries.Add(entry.Key, entry.Value.ToLowerInvariant());
            }
            if (signature.SchemaVersion != 1 || signature.Algorithm != "RSA-SHA256-PKCS1")
                return (false, null, null, "Unsupported package signature format.");
            var certificateBytes = Convert.FromBase64String(signature.Certificate);
            using var certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
            var thumbprint = certificate.Thumbprint;
            var signedAtUtc = signature.SignedAtUtc.ToUniversalTime();
            if (signature.SignedAtUtc == default || signedAtUtc < certificate.NotBefore.ToUniversalTime() ||
                signedAtUtc > certificate.NotAfter.ToUniversalTime())
                return (false, thumbprint, certificate.Subject, "Signing time is outside the certificate validity period.");
            if (!string.Equals(thumbprint, signature.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
                return (false, thumbprint, certificate.Subject, "Certificate thumbprint does not match.");
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is null) return (false, thumbprint, certificate.Subject, "Only RSA signing certificates are supported.");
            if (rsa.KeySize < 2048)
                return (false, thumbprint, certificate.Subject, "RSA signing certificates must use at least a 2048-bit key.");
            var signatureBytes = Convert.FromBase64String(signature.Signature);
            var payloadHash = _CreatePackageSignaturePayloadHash(entries, signature.SignedAtUtc,
                signature.SchemaVersion, signature.Algorithm, signature.CertificateThumbprint, certificateBytes);
            var valid = rsa.VerifyHash(payloadHash, signatureBytes, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return valid ? (true, thumbprint, certificate.Subject, null) :
                (false, thumbprint, certificate.Subject, "Package signature verification failed.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return (false, null, null, $"Invalid package signature: {ex.Message}");
        }
    }

    private static byte[] _CreatePackageSignaturePayloadHash(SortedDictionary<string, string> entries,
        DateTimeOffset signedAtUtc, int schemaVersion, string algorithm, string certificateThumbprint,
        byte[] certificateBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _AppendUtf8(hash, "signatureSchema\0" + schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        _AppendUtf8(hash, "signatureAlgorithm\0" + algorithm + "\n");
        _AppendUtf8(hash, "certificateThumbprint\0" + certificateThumbprint + "\n");
        _AppendUtf8(hash, "certificateSha256\0" + Convert.ToHexString(SHA256.HashData(certificateBytes)).ToLowerInvariant() + "\n");
        _AppendUtf8(hash, "signedAtUtc\0" + signedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        foreach (var entry in entries)
        {
            ValidateRelativePath(entry.Key);
            _AppendUtf8(hash, entry.Key.Replace('\\', '/') + "\0" + entry.Value + "\n");
        }
        return hash.GetHashAndReset();
    }

    private static void _AppendUtf8(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static async Task<(string Hash, byte[]? Bytes)> _HashStreamAsync(Stream input, long expectedLength,
        long? captureLimit, Stream? copyTo, CancellationToken cancellationToken)
    {
        if (captureLimit is { } limit && expectedLength > limit)
            throw new InvalidDataException("Package metadata entry exceeds its size limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var captured = captureLimit is null ? null : new MemoryStream((int)expectedLength);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total = checked(total + read);
            if (total > expectedLength) throw new InvalidDataException("Package entry length is invalid.");
            hash.AppendData(buffer, 0, read);
            if (captured is not null) captured.Write(buffer, 0, read);
            if (copyTo is not null) await copyTo.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (total != expectedLength) throw new InvalidDataException("Package entry length is invalid.");
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), captured?.ToArray());
    }

    private static async Task<byte[]> _ReadBoundedStreamAsync(Stream input, long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Package metadata entry exceeds its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task<byte[]> _ReadBoundedFileAsync(string path, long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await _ReadBoundedStreamAsync(input, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Hash, byte[]? Bytes)> _HashFileAsync(string path, long? captureLimit,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        if (captureLimit is { } maximum && length > maximum)
            throw new InvalidDataException("Plugin metadata file exceeds its size limit.");
        return await _HashStreamAsync(input, length, captureLimit, null, cancellationToken).ConfigureAwait(false);
    }

    public static (bool Valid, string? Thumbprint, string? Signer, string? Error) VerifyFiles(
        IReadOnlyDictionary<string, byte[]> files, PluginPackageSignature signature)
    {
        try
        {
            if (signature.SchemaVersion != 1 || signature.Algorithm != "RSA-SHA256-PKCS1")
                return (false, null, null, "Unsupported package signature format.");
            var certificateBytes = Convert.FromBase64String(signature.Certificate);
            using var certificate = X509CertificateLoader.LoadCertificate(certificateBytes);
            var thumbprint = certificate.Thumbprint;
            var signedAtUtc = signature.SignedAtUtc.ToUniversalTime();
            if (signature.SignedAtUtc == default || signedAtUtc < certificate.NotBefore.ToUniversalTime() ||
                signedAtUtc > certificate.NotAfter.ToUniversalTime())
                return (false, thumbprint, certificate.Subject, "Signing time is outside the certificate validity period.");
            if (!string.Equals(thumbprint, signature.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
                return (false, thumbprint, certificate.Subject, "Certificate thumbprint does not match.");
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is null) return (false, thumbprint, certificate.Subject, "Only RSA signing certificates are supported.");
            if (rsa.KeySize < 2048)
                return (false, thumbprint, certificate.Subject, "RSA signing certificates must use at least a 2048-bit key.");
            var valid = rsa.VerifyData(CreatePackageSignaturePayload(files, signature.SignedAtUtc,
                    signature.SchemaVersion, signature.Algorithm, signature.CertificateThumbprint, certificateBytes),
                Convert.FromBase64String(signature.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return valid ? (true, thumbprint, certificate.Subject, null) : (false, thumbprint, certificate.Subject, "Package signature verification failed.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return (false, null, null, $"Invalid package signature: {ex.Message}");
        }
    }

    public static PluginPackageSignature SignFiles(IReadOnlyDictionary<string, byte[]> files,
        X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(files);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in files)
        {
            ValidateRelativePath(item.Key);
            var content = item.Value;
            if (item.Key.Equals(ManifestFileName, StringComparison.Ordinal))
            {
                var manifest = DeserializeManifest(content);
                content = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            }
            hashes.Add(item.Key, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        }
        return SignFileHashes(hashes, certificate);
    }

    /// <summary>Signs a package using already-calculated per-file SHA-256 hashes.</summary>
    public static PluginPackageSignature SignFileHashes(IReadOnlyDictionary<string, string> fileHashes,
        X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(fileHashes);
        ArgumentNullException.ThrowIfNull(certificate);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in fileHashes)
        {
            ValidateRelativePath(item.Key);
            if (item.Value is null || item.Value.Length != 64 || item.Value.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException($"Invalid file hash: {item.Key}", nameof(fileHashes));
            hashes.Add(item.Key, item.Value.ToLowerInvariant());
        }
        if (!certificate.HasPrivateKey) throw new ArgumentException("Certificate must contain its private key.", nameof(certificate));
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new ArgumentException("RSA certificate required.", nameof(certificate));
        if (rsa.KeySize < 2048) throw new ArgumentException("RSA signing certificates must use at least a 2048-bit key.", nameof(certificate));
        var signedAtUtc = DateTimeOffset.UtcNow;
        const int schemaVersion = 1;
        const string algorithm = "RSA-SHA256-PKCS1";
        var certificateBytes = certificate.Export(X509ContentType.Cert);
        var payloadHash = _CreatePackageSignaturePayloadHash(hashes, signedAtUtc, schemaVersion, algorithm,
            certificate.Thumbprint, certificateBytes);
        return new(schemaVersion, algorithm, Convert.ToBase64String(certificateBytes),
            Convert.ToBase64String(rsa.SignHash(payloadHash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            certificate.Thumbprint, signedAtUtc);
    }

    private static byte[] CreatePackageSignaturePayload(IReadOnlyDictionary<string, byte[]> files,
        DateTimeOffset signedAtUtc, int schemaVersion, string algorithm, string certificateThumbprint,
        byte[] certificateBytes)
    {
        using var output = new MemoryStream();
        using (var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write("signatureSchema\0");
            writer.Write(schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.Write('\n');
            writer.Write("signatureAlgorithm\0");
            writer.Write(algorithm);
            writer.Write('\n');
            writer.Write("certificateThumbprint\0");
            writer.Write(certificateThumbprint);
            writer.Write('\n');
            writer.Write("certificateSha256\0");
            writer.Write(Convert.ToHexString(SHA256.HashData(certificateBytes)).ToLowerInvariant());
            writer.Write('\n');
            writer.Flush();
        }
        output.Write(CreateSignaturePayload(files, signedAtUtc));
        return output.ToArray();
    }

    public static byte[] CreateSignaturePayload(IReadOnlyDictionary<string, byte[]> files, DateTimeOffset signedAtUtc)
    {
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        writer.Write("signedAtUtc\0");
        writer.Write(signedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writer.Write('\n');
        foreach (var item in files.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            ValidateRelativePath(item.Key);
            writer.Write(item.Key.Replace('\\', '/'));
            writer.Write('\0');
            var content = item.Value;
            if (item.Key.Equals(ManifestFileName, StringComparison.Ordinal))
            {
                var manifest = DeserializeManifest(content);
                content = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            }
            writer.Write(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
            writer.Write('\n');
        }
        writer.Flush();
        return output.ToArray();
    }

    public static string CreateFileContentHash(IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        foreach (var item in files.Where(item => !item.Key.Equals(SignatureFileName, StringComparison.Ordinal))
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            ValidateRelativePath(item.Key);
            writer.Write(item.Key.Replace('\\', '/'));
            writer.Write('\0');
            var content = item.Value;
            if (item.Key.Equals(ManifestFileName, StringComparison.Ordinal))
            {
                var manifest = DeserializeManifest(content);
                content = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            }
            writer.Write(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
            writer.Write('\n');
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant();
    }

    /// <summary>Creates the package content hash from canonicalized per-file SHA-256 hashes.</summary>
    public static string CreateFileContentHash(IReadOnlyDictionary<string, string> fileHashes)
    {
        ArgumentNullException.ThrowIfNull(fileHashes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in fileHashes.Where(entry => !entry.Key.Equals(SignatureFileName, StringComparison.Ordinal))
                     .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            ValidateRelativePath(entry.Key);
            if (entry.Value is null || entry.Value.Length != 64 || entry.Value.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException($"Invalid file hash: {entry.Key}", nameof(fileHashes));
            _AppendUtf8(hash, entry.Key.Replace('\\', '/') + "\0" + entry.Value.ToLowerInvariant() + "\n");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string? ValidateManifest(PluginManifest? manifest, IEnumerable<string> fileNames)
    {
        if (manifest is null) return "Package manifest is invalid.";
        if (manifest.SchemaVersion != 1) return "Unsupported plugin manifest version.";
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 64 ||
            manifest.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-')) ||
            !char.IsAsciiLetterOrDigit(manifest.Id[0])) return "Plugin id must contain lowercase letters, digits, dots, and hyphens.";
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120) return "Plugin name is missing or too long.";
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 64) return "Plugin version is missing or too long.";
        if (!PluginVersionRange.IsValidVersion(manifest.Version)) return "Plugin version must follow SemVer 2.0.";
        if (string.IsNullOrWhiteSpace(manifest.Author) || manifest.Author.Length > 128) return "Plugin author is missing or too long.";
        if (manifest.Description is null || manifest.Description.Length > 4096) return "Plugin description is missing or too long.";
        if (manifest.ApiVersion != PluginApiVersion.Current) return $"Plugin API version {manifest.ApiVersion} is not supported.";
        if (manifest.PclCoreVersion is { } coreVersionRange &&
            (coreVersionRange.Length > 256 || !PluginVersionRange.IsValid(coreVersionRange)))
            return "Plugin PCL Core version range is invalid.";
        if (manifest.EntryAssembly is null || manifest.EntryAssembly.Length > 512)
            return "Plugin entry assembly path is missing or too long.";
        try { ValidateRelativePath(manifest.EntryAssembly); }
        catch (ArgumentException ex) { return ex.Message; }
        if (!manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return "Entry assembly must be a DLL.";
        if (!fileNames.Contains(manifest.EntryAssembly, StringComparer.Ordinal)) return "Entry assembly is missing from package.";
        if (string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 512)
            return "Plugin entry type is missing or too long.";
        if (manifest.Website is not null && !IsSafeHttpsUri(manifest.Website))
            return "Plugin website must be an absolute HTTPS URL without credentials.";
        if (manifest.Icon is not null)
        {
            try { ValidateRelativePath(manifest.Icon); }
            catch (ArgumentException ex) { return ex.Message; }
            if (!fileNames.Contains(manifest.Icon, StringComparer.Ordinal)) return "Plugin icon is missing from package.";
            if (!manifest.Icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                !manifest.Icon.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !manifest.Icon.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                !manifest.Icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) &&
                !manifest.Icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                return "Plugin icon must be PNG, JPEG, SVG, or ICO.";
        }
        if (!ValidateLocalizedFields(manifest.LocalizedNames, 120, "name", out var localizedError) ||
            !ValidateLocalizedFields(manifest.LocalizedDescriptions, 4096, "description", out localizedError))
            return localizedError;
        if (manifest.ReleaseAssetUrl is not null && !IsGitHubReleaseAssetUri(manifest.ReleaseAssetUrl))
            return "Plugin release asset URL must be an HTTPS GitHub release asset URL.";
        if (manifest.MixinConfigs is null) return "Mixin configuration list cannot be null.";
        if (manifest.ExperimentalFeatures is null) return "Experimental feature list cannot be null.";
        if (manifest.ExperimentalFeatures.Count > 64) return "Plugin declares too many experimental Mixin features.";
        if (manifest.MixinConfigs.Count > 64) return "Plugin declares too many Mixin configurations.";
        if (manifest.Dependencies is null || manifest.Dependencies.Count > 64)
            return "Plugin dependency list is missing or exceeds 64 entries.";
        var seenDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dependencyId, versionRange) in manifest.Dependencies)
        {
            if (!IsValidPluginId(dependencyId) || !seenDependencies.Add(dependencyId))
                return $"Plugin dependency id is invalid or duplicated: {dependencyId}";
            if (string.Equals(dependencyId, manifest.Id, StringComparison.OrdinalIgnoreCase))
                return "A plugin cannot depend on itself.";
            if (!PluginVersionRange.IsValid(versionRange))
                return $"Plugin dependency version range is invalid: {dependencyId} ({versionRange})";
        }
        var mixinConfigurations = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.MixinConfig)) mixinConfigurations.Add(manifest.MixinConfig);
        if (manifest.MixinConfigs.Any(string.IsNullOrWhiteSpace)) return "Mixin configuration paths cannot be empty.";
        mixinConfigurations.AddRange(manifest.MixinConfigs);
        if (mixinConfigurations.Count > 64) return "Plugin declares too many Mixin configurations.";
        var seenMixinConfigurations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in mixinConfigurations)
        {
            if (path.Length > 512) return "Mixin configuration path is too long.";
            try { ValidateRelativePath(path); }
            catch (ArgumentException ex) { return ex.Message; }
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return "Mixin configuration paths must point to JSON files.";
            if (!fileNames.Contains(path, StringComparer.Ordinal))
                return $"Mixin configuration is missing from package: {path}";
            if (!seenMixinConfigurations.Add(path)) return $"Duplicate Mixin configuration: {path}";
        }
        var seenFeatureIds = new HashSet<string>(StringComparer.Ordinal);
        var seenFeatureConfigPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in manifest.ExperimentalFeatures)
        {
            if (feature is null || string.IsNullOrWhiteSpace(feature.Id) || feature.Id.Length > 64 ||
                feature.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-')))
                return "Experimental feature id is invalid.";
            if (!seenFeatureIds.Add(feature.Id)) return $"Duplicate experimental feature id: {feature.Id}";
            if (string.IsNullOrWhiteSpace(feature.Name) || feature.Name.Length > 128 ||
                feature.Description is null || feature.Description.Length > 2048)
                return $"Experimental feature metadata is invalid: {feature.Id}";
            if (feature.MixinConfigs is null || feature.MixinConfigs.Count is 0 or > 64)
                return $"Experimental feature must declare 1 to 64 Mixin configurations: {feature.Id}";
            foreach (var path in feature.MixinConfigs)
            {
                try { ValidateRelativePath(path); }
                catch (ArgumentException ex) { return ex.Message; }
                if (path.Length > 512 || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    return $"Experimental feature configuration path is invalid: {feature.Id}/{path}";
                if (!fileNames.Contains(path, StringComparer.Ordinal))
                    return $"Experimental feature configuration is missing from package: {feature.Id}/{path}";
                if (!seenMixinConfigurations.Add(path) || !seenFeatureConfigPaths.Add(path))
                    return $"Mixin configuration path is assigned more than once: {path}";
            }
        }
        return null;
    }

    private static bool ValidateLocalizedFields(IReadOnlyDictionary<string, string>? values, int maxLength,
        string field, out string? error)
    {
        error = null;
        if (values is null || values.Count > 32) { error = $"Plugin localized {field} values are invalid."; return false; }
        foreach (var (culture, value) in values)
        {
            if (string.IsNullOrWhiteSpace(culture) || culture.Length > 32 || string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            { error = $"Plugin localized {field} values are invalid."; return false; }
            try { _ = System.Globalization.CultureInfo.GetCultureInfo(culture); }
            catch (CultureNotFoundException) { error = $"Plugin localized {field} culture is invalid: {culture}"; return false; }
        }
        return true;
    }

    public static string? ValidateMixinConfigurations(PluginManifest manifest,
        IReadOnlyDictionary<string, byte[]> files)
    {
        if (manifest is null) return "Package manifest is invalid.";
        if (files is null) return "Package files are missing.";
        if (manifest.MixinConfigs is null) return "Mixin configuration list cannot be null.";
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.MixinConfig)) paths.Add(manifest.MixinConfig);
        if (manifest.MixinConfigs.Any(string.IsNullOrWhiteSpace)) return "Mixin configuration paths cannot be empty.";
        paths.AddRange(manifest.MixinConfigs);
        foreach (var feature in manifest.ExperimentalFeatures ?? [])
        {
            if (feature is null || feature.MixinConfigs is null)
                return "Experimental feature Mixin configuration list cannot be null.";
            paths.AddRange(feature.MixinConfigs);
        }
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!files.TryGetValue(path, out var bytes)) return $"Mixin configuration is missing from package: {path}";
            if (bytes.Length > MaximumMixinConfigurationBytes)
                return $"Mixin configuration exceeds the 256 KiB limit: {path}";
            MixinConfiguration? configuration;
            try { configuration = JsonSerializer.Deserialize<MixinConfiguration>(bytes, JsonOptions); }
            catch (JsonException ex) { return $"Mixin configuration is invalid ({path}): {ex.Message}"; }
            if (configuration is null) return $"Mixin configuration is empty: {path}";
            if (configuration.Mixins is null || configuration.Mixins.Count is 0 or > MaximumMixinsPerConfiguration)
                return $"Mixin configuration must declare 1 to {MaximumMixinsPerConfiguration} Mixin types: {path}";
            if (configuration.Mixins.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 256))
                return $"Mixin configuration contains an empty or oversized type name: {path}";
            if (configuration.Mixins.Distinct(StringComparer.Ordinal).Count() != configuration.Mixins.Count)
                return $"Mixin configuration contains duplicate type names: {path}";
            if (configuration.Package is null || configuration.Package.Length > 256 || (configuration.Plugin?.Length ?? 0) > 256)
                return $"Mixin configuration contains an oversized type name: {path}";
            if (configuration.Injectors is null || configuration.Injectors.DefaultRequire is < 0 or > 1000)
                return $"Mixin configuration has an invalid defaultRequire value: {path}";
            if (configuration.Priority is < -10_000 or > 10_000)
                return $"Mixin configuration has an invalid priority: {path}";
        }
        return null;
    }

    public static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains(':') ||
            path.Length > MaximumRelativePathLength || path.StartsWith('/') || path.StartsWith('\\') ||
            Path.IsPathRooted(path))
            throw new ArgumentException($"Unsafe package path: {path}");
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Length > MaximumPathDepth || segments.Any(part => part is "" or "." or ".." ||
                part.Length > MaximumPathSegmentLength))
            throw new ArgumentException($"Unsafe package path: {path}");
    }

    private static bool IsValidPluginId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 64 &&
        char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '-');

    public static async Task<IReadOnlyDictionary<string, byte[]>> ReadPackageFilesAsync(string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (new FileInfo(packagePath).Length > MaximumPackageBytes)
            throw new InvalidDataException("Package exceeds the 100 MiB limit.");
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > MaximumPackageEntries)
            throw new InvalidDataException("Package contains too many entries.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            ValidateRelativePath(entry.FullName);
            if (!seenNames.Add(entry.FullName.Replace('\\', '/'))) throw new InvalidDataException($"Duplicate package entry: {entry.FullName}");
            if (!files.TryAdd(entry.FullName, [])) throw new InvalidDataException($"Duplicate package entry: {entry.FullName}");
            if (entry.FullName.Equals(ManifestFileName, StringComparison.Ordinal) && entry.Length > MaximumManifestBytes)
                throw new InvalidDataException("Plugin manifest exceeds the 64 KiB limit.");
            total = checked(total + entry.Length);
            if (total > MaximumExpandedBytes) throw new InvalidDataException("Expanded package is too large.");
            await using var source = entry.Open();
            var contents = GC.AllocateUninitializedArray<byte>(checked((int)entry.Length));
            var offset = 0;
            while (offset < contents.Length)
            {
                var read = await source.ReadAsync(contents.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }
            if (offset != contents.Length || await source.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidDataException($"Package entry length is invalid: {entry.FullName}");
            files[entry.FullName] = contents;
        }
        return files;
    }

    public static JsonSerializerOptions SerializerOptions => new(JsonOptions);

    /// <summary>Parses a bounded, depth-limited plugin manifest.</summary>
    public static PluginManifest DeserializeManifest(ReadOnlySpan<byte> manifestBytes)
    {
        if (manifestBytes.Length > MaximumManifestBytes)
            throw new InvalidDataException("Plugin manifest exceeds the 64 KiB limit.");
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(manifestBytes, JsonOptions)
                   ?? throw new InvalidDataException("Plugin manifest is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Plugin manifest is invalid.", ex);
        }
    }

    private static bool IsSafeHttpsUri(string value) => value.Length <= 2048 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsGitHubReleaseAssetUri(string value)
    {
        if (!IsSafeHttpsUri(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 6 && segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase) &&
               segments[3].Equals("download", StringComparison.OrdinalIgnoreCase) &&
               Uri.UnescapeDataString(segments[5]).EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Utilities for plugin authors to create signing certificates and packages.</summary>
public static class PluginAuthoring
{
    public static byte[] CreateSigningCertificate(string subjectName, string password)
    {
        if (string.IsNullOrWhiteSpace(subjectName)) throw new ArgumentException("A certificate subject is required.", nameof(subjectName));
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A PFX password is required.", nameof(password));
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(new X500DistinguishedName(subjectName), rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        return certificate.Export(X509ContentType.Pfx, password);
    }

    public static async Task CreatePackageAsync(string sourceDirectory, string outputPath,
        X509Certificate2 signingCertificate, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);
        var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateFilesWithoutReparsePoints(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/');
            PluginPackage.ValidateRelativePath(relative);
            if (relative.Equals(PluginPackage.SignatureFileName, StringComparison.Ordinal)) continue;
            if (!seenPaths.Add(relative))
                throw new InvalidDataException($"Duplicate plugin source path: {relative}");
            var info = new FileInfo(path);
            if (relative.Equals(PluginPackage.ManifestFileName, StringComparison.Ordinal) &&
                info.Length > PluginPackage.MaximumManifestBytes)
                throw new InvalidDataException("Plugin manifest exceeds the 64 KiB limit.");
            if (paths.Count >= PluginPackage.MaximumPackageEntries - 1)
                throw new InvalidDataException("Plugin source exceeds the package entry or expanded size limit.");
            paths.Add(relative, path);
        }
        if (!paths.TryGetValue(PluginPackage.ManifestFileName, out var manifestPath))
            throw new InvalidDataException("plugin.json is missing.");
        var manifestBytes = await _ReadBoundedFileAsync(manifestPath, PluginPackage.MaximumManifestBytes,
            cancellationToken).ConfigureAwait(false);
        var manifest = PluginPackage.DeserializeManifest(manifestBytes);
        var validationError = PluginPackage.ValidateManifest(manifest, paths.Keys);
        if (validationError is not null) throw new InvalidDataException(validationError);
        var canonicalManifest = JsonSerializer.SerializeToUtf8Bytes(manifest, PluginPackage.SerializerOptions);
        var mixinPaths = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(manifest.MixinConfig)) mixinPaths.Add(manifest.MixinConfig);
        foreach (var path in manifest.MixinConfigs ?? []) if (!string.IsNullOrWhiteSpace(path)) mixinPaths.Add(path);
        foreach (var feature in manifest.ExperimentalFeatures ?? [])
            foreach (var path in feature.MixinConfigs ?? []) if (!string.IsNullOrWhiteSpace(path)) mixinPaths.Add(path);

        var outputFullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        var temporaryPath = outputFullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var mixinFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                long expandedBytes = 0;
                foreach (var (name, path) in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    if (name.Equals(PluginPackage.ManifestFileName, StringComparison.Ordinal))
                    {
                        expandedBytes = checked(expandedBytes + canonicalManifest.LongLength);
                        if (expandedBytes > PluginPackage.MaximumExpandedBytes)
                            throw new InvalidDataException("Plugin source exceeds the expanded size limit.");
                        await target.WriteAsync(canonicalManifest, cancellationToken).ConfigureAwait(false);
                        hashes.Add(name, Convert.ToHexString(SHA256.HashData(canonicalManifest)).ToLowerInvariant());
                        continue;
                    }

                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException($"Package source cannot contain symbolic links: {path}");
                    await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using var captured = mixinPaths.Contains(name) ? new MemoryStream() : null;
                    var buffer = new byte[81920];
                    long copied = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        copied = checked(copied + read);
                        expandedBytes = checked(expandedBytes + read);
                        if (expandedBytes > PluginPackage.MaximumExpandedBytes)
                            throw new InvalidDataException("Plugin source exceeds the expanded size limit.");
                        if (captured is not null && captured.Length + read > PluginPackage.MaximumMixinConfigurationBytes)
                            throw new InvalidDataException($"Mixin configuration exceeds the 256 KiB limit: {name}");
                        contentHash.AppendData(buffer, 0, read);
                        if (captured is not null) captured.Write(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    if (copied != source.Length)
                        throw new InvalidDataException($"Plugin source changed while packaging: {name}");
                    hashes.Add(name, Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant());
                    if (captured is not null) mixinFiles.Add(name, captured.ToArray());
                }

                validationError = PluginPackage.ValidateMixinConfigurations(manifest, mixinFiles);
                if (validationError is not null) throw new InvalidDataException(validationError);
                var signature = PluginPackage.SignFileHashes(hashes, signingCertificate);
                var signatureEntry = archive.CreateEntry(PluginPackage.SignatureFileName, CompressionLevel.Optimal);
                await using var signatureTarget = signatureEntry.Open();
                await JsonSerializer.SerializeAsync(signatureTarget, signature, PluginPackage.SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (new FileInfo(temporaryPath).Length > PluginPackage.MaximumPackageBytes)
                throw new InvalidDataException("Created plugin package exceeds the 100 MiB compressed size limit.");
            File.Move(temporaryPath, outputFullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<byte[]> _ReadBoundedFileAsync(string path, long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maximumBytes) throw new InvalidDataException("File exceeds its allowed size limit.");
        using var output = new MemoryStream((int)input.Length);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("File exceeds its allowed size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Package source cannot contain symbolic links: {entry}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }
}
