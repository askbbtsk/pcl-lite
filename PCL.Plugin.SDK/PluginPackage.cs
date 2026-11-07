using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
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
    public required string EntryAssembly { get; init; }
    public string EntryType { get; init; } = "Plugin";
    public string? MixinConfig { get; init; }
    public IReadOnlyList<string> MixinConfigs { get; init; } = [];
    public string? Website { get; init; }
    /// <summary>Optional direct GitHub release asset URL used to corroborate package hash and publish time.</summary>
    public string? ReleaseAssetUrl { get; init; }
}

public sealed record PluginPackageSignature(int SchemaVersion, string Algorithm, string Certificate,
    string Signature, string CertificateThumbprint, DateTimeOffset SignedAtUtc);

public sealed record PluginPackageCheck(PluginManifest Manifest, bool IsSigned, bool SignatureValid,
    string? SignerThumbprint, string? Signer, DateTimeOffset? SignedAtUtc, string ContentHash, string? Error)
{
    public bool IsValid => Error is null && (!IsSigned || SignatureValid);
    public PluginGitHubProvenance? GitHubProvenance { get; init; }
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

        try
        {
            await using var stream = File.OpenRead(packagePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaximumPackageEntries)
                return new(default!, false, false, null, null, null, "", "Package contains too many entries.");
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) continue;
                ValidateRelativePath(entry.FullName);
                if (entry.FullName.Equals(ManifestFileName, StringComparison.Ordinal) &&
                    entry.Length > MaximumManifestBytes)
                    return new(default!, false, false, null, null, null, "", "Plugin manifest exceeds the 64 KiB limit.");
                if (!seenNames.Add(entry.FullName.Replace('\\', '/')))
                    return new(default!, false, false, null, null, null, "", $"Duplicate package entry: {entry.FullName}");
                if (entry.FullName.Equals(SignatureFileName, StringComparison.Ordinal)) continue;
                if (!files.TryAdd(entry.FullName, []))
                    return new(default!, false, false, null, null, null, "", $"Duplicate package entry: {entry.FullName}");
                expanded = checked(expanded + entry.Length);
                if (expanded > MaximumExpandedBytes)
                    return new(default!, false, false, null, null, null, "", "Expanded package exceeds the 500 MiB limit.");
                await using var input = entry.Open();
                var contents = GC.AllocateUninitializedArray<byte>(checked((int)entry.Length));
                var offset = 0;
                while (offset < contents.Length)
                {
                    var read = await input.ReadAsync(contents.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    offset += read;
                }
                if (offset != contents.Length || await input.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                    return new(default!, false, false, null, null, null, "", $"Package entry length is invalid: {entry.FullName}");
                files[entry.FullName] = contents;
            }

            if (!files.TryGetValue(ManifestFileName, out var manifestBytes))
                return new(default!, false, false, null, null, null, "", "Package manifest is missing.");
            var manifest = DeserializeManifest(manifestBytes);
            var validationError = ValidateManifest(manifest, files.Keys);
            if (validationError is not null)
                return new(manifest!, false, false, null, null, null, CreateFileContentHash(files), validationError);
            validationError = ValidateMixinConfigurations(manifest!, files);
            if (validationError is not null)
                return new(manifest!, false, false, null, null, null, CreateFileContentHash(files), validationError);

            var signatureEntry = archive.GetEntry(SignatureFileName);
            if (signatureEntry is null)
                return new(manifest!, false, false, null, null, null, CreateFileContentHash(files), null);
            if (signatureEntry.Length > MaximumSignatureBytes)
                return new(manifest!, true, false, null, null, null, CreateFileContentHash(files), "Package signature metadata is too large.");

            await using var signatureStream = signatureEntry.Open();
            var signature = await JsonSerializer.DeserializeAsync<PluginPackageSignature>(signatureStream,
                JsonOptions, cancellationToken).ConfigureAwait(false);
            if (signature is null || signature.SchemaVersion != 1 || signature.Algorithm != "RSA-SHA256-PKCS1")
                return new(manifest!, true, false, null, null, null, CreateFileContentHash(files), "Unsupported package signature format.");
            var verification = VerifyFiles(files, signature);
            return new(manifest!, true, verification.Valid, verification.Thumbprint, verification.Signer,
                signature.SignedAtUtc, CreateFileContentHash(files), verification.Valid ? null : verification.Error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or FormatException or OverflowException or ArgumentException)
        {
            return new(default!, false, false, null, null, null, "", $"Invalid plugin package: {ex.Message}");
        }
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
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey) throw new ArgumentException("Certificate must contain its private key.", nameof(certificate));
        using var rsa = certificate.GetRSAPrivateKey() ?? throw new ArgumentException("RSA certificate required.", nameof(certificate));
        if (rsa.KeySize < 2048) throw new ArgumentException("RSA signing certificates must use at least a 2048-bit key.", nameof(certificate));
        var signedAtUtc = DateTimeOffset.UtcNow;
        const int schemaVersion = 1;
        const string algorithm = "RSA-SHA256-PKCS1";
        var certificateBytes = certificate.Export(X509ContentType.Cert);
        var payload = CreatePackageSignaturePayload(files, signedAtUtc, schemaVersion, algorithm,
            certificate.Thumbprint, certificateBytes);
        return new(schemaVersion, algorithm, Convert.ToBase64String(certificateBytes),
            Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
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

    public static string? ValidateManifest(PluginManifest? manifest, IEnumerable<string> fileNames)
    {
        if (manifest is null) return "Package manifest is invalid.";
        if (manifest.SchemaVersion != 1) return "Unsupported plugin manifest version.";
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 64 ||
            manifest.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-')) ||
            !char.IsAsciiLetterOrDigit(manifest.Id[0])) return "Plugin id must contain lowercase letters, digits, dots, and hyphens.";
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120) return "Plugin name is missing or too long.";
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 64) return "Plugin version is missing or too long.";
        if (string.IsNullOrWhiteSpace(manifest.Author) || manifest.Author.Length > 128) return "Plugin author is missing or too long.";
        if (manifest.Description is null || manifest.Description.Length > 4096) return "Plugin description is missing or too long.";
        if (manifest.ApiVersion != PluginApiVersion.Current) return $"Plugin API version {manifest.ApiVersion} is not supported.";
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
        if (manifest.ReleaseAssetUrl is not null && !IsGitHubReleaseAssetUri(manifest.ReleaseAssetUrl))
            return "Plugin release asset URL must be an HTTPS GitHub release asset URL.";
        if (manifest.MixinConfigs is null) return "Mixin configuration list cannot be null.";
        if (manifest.MixinConfigs.Count > 64) return "Plugin declares too many Mixin configurations.";
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
        return null;
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
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        long expandedBytes = 0;
        foreach (var path in EnumerateFilesWithoutReparsePoints(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/');
            PluginPackage.ValidateRelativePath(relative);
            if (relative.Equals(PluginPackage.SignatureFileName, StringComparison.Ordinal)) continue;
            var fileLength = new FileInfo(path).Length;
            if (relative.Equals(PluginPackage.ManifestFileName, StringComparison.Ordinal) &&
                fileLength > PluginPackage.MaximumManifestBytes)
                throw new InvalidDataException("Plugin manifest exceeds the 64 KiB limit.");
            expandedBytes = checked(expandedBytes + fileLength);
            if (files.Count >= PluginPackage.MaximumPackageEntries - 1 || expandedBytes > PluginPackage.MaximumExpandedBytes)
                throw new InvalidDataException("Plugin source exceeds the package entry or expanded size limit.");
            files.Add(relative, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        if (!files.TryGetValue(PluginPackage.ManifestFileName, out var manifestBytes))
            throw new InvalidDataException("plugin.json is missing.");
        var manifest = PluginPackage.DeserializeManifest(manifestBytes);
        var validationError = PluginPackage.ValidateManifest(manifest, files.Keys);
        if (validationError is not null) throw new InvalidDataException(validationError);
        validationError = PluginPackage.ValidateMixinConfigurations(manifest, files);
        if (validationError is not null) throw new InvalidDataException(validationError);
        files[PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(manifest, PluginPackage.SerializerOptions);
        var signature = PluginPackage.SignFiles(files, signingCertificate);

        var outputFullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        var temporaryPath = outputFullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var (name, contents) in files)
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await target.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                }
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
