using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace PCL.Core.Minecraft.Enhancements;

/// <summary>Optional sidecar storage; construction and reads never create a directory.</summary>
public sealed class InstanceEnhancementStore
{
    private const int MaxDocumentBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _instance;
    public string DirectoryPath { get; }
    public string DeclarationPath => Path.Combine(DirectoryPath, "instance.json");
    public string ResolvedPath => Path.Combine(DirectoryPath, "resolved.json");

    public InstanceEnhancementStore(string instanceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);
        _instance = EnhancementPaths.Normalize(instanceDirectory);
        DirectoryPath = Path.Combine(_instance, "PCL", "Enhancements");
    }

    public EnhancementReadResult<InstanceEnhancementDocument> ReadDeclaration()
        => Read<InstanceEnhancementDocument>(DeclarationPath, value => value.Components is not null
            && value.Components.TrueForAll(component => component is not null && !string.IsNullOrWhiteSpace(component.Id)));

    private EnhancementReadResult<InstanceResolvedReport> ReadResolvedDocument()
        => Read<InstanceResolvedReport>(ResolvedPath, value => !string.IsNullOrWhiteSpace(value.Fingerprint)
            && value.Components is not null && value.Libraries is not null && value.Warnings is not null);

    public EnhancementReadResult<InstanceResolvedReport> ReadResolved()
    {
        try
        {
            using var readGate = AcquireReadGate();
            return ReadResolvedDocument();
        }
        catch (Exception error) when (EnhancementPaths.IsExpectedFailure(error))
        { return new(EnhancementReadStatus.Unavailable, null, Error: error.Message); }
    }

    /// <summary>Explicitly deletes only this supported derived report after rechecking the preview hash.</summary>
    public bool ClearResolved(string expectedContentSha256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedContentSha256);
        cancellationToken.ThrowIfCancellationRequested();
        EnhancementPaths.RejectLinks(DirectoryPath);
        if (!Directory.Exists(DirectoryPath)) return false;
        var gatePath = Path.Combine(DirectoryPath, ".write.lock");
        EnhancementPaths.RejectLinks(gatePath);
        using var gate = new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        EnhancementPaths.RejectLinks(ResolvedPath);
        var current = ReadResolvedDocument();
        if (current.Status == EnhancementReadStatus.Missing) return false;
        if (current.Status != EnhancementReadStatus.Valid)
            throw new IOException("Invalid, unreadable or newer report was preserved; only supported derived caches can be cleared.");
        if (!string.Equals(current.ContentSha256, expectedContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Resolved report changed after the preview; reload before clearing it.");
        cancellationToken.ThrowIfCancellationRequested();
        EnhancementPaths.RejectLinks(ResolvedPath);
        if (!string.Equals(ReadBytesHash(ResolvedPath), expectedContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Resolved report changed before cache cleanup.");
        File.Delete(ResolvedPath);
        return true;
    }

    private FileStream? AcquireReadGate()
    {
        EnhancementPaths.RejectLinks(DirectoryPath);
        var gatePath = Path.Combine(DirectoryPath, ".write.lock");
        EnhancementPaths.RejectLinks(gatePath);
        try { return new FileStream(gatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Explicit edit only. Pass the hash returned by ReadDeclaration for an existing document.</summary>
    public void SaveDeclaration(InstanceEnhancementDocument document, string? expectedContentSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1 || document.Components is null || document.Components.Exists(
                component => component is null || string.IsNullOrWhiteSpace(component.Id)))
            throw new InvalidDataException("Invalid enhancement declaration.");
        Write(DeclarationPath, document, expectedContentSha256, replaceDerived: false, cancellationToken);
    }

    /// <summary>Cache writes may fail without invalidating the caller's freshly computed report.</summary>
    public bool TrySaveResolved(InstanceResolvedReport report, out string? error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        if (!report.InputsStable) { error = "Inputs changed during analysis; the report was not cached."; return false; }
        if (report.SchemaVersion != 1) { error = "Unsupported report schema."; return false; }
        try
        {
            Write(ResolvedPath, report, expectedContentSha256: null, replaceDerived: true, cancellationToken);
            error = null;
            return true;
        }
        catch (Exception ex) when (EnhancementPaths.IsExpectedFailure(ex) || ex is JsonException)
        {
            error = ex.Message;
            return false;
        }
    }

    private EnhancementReadResult<T> Read<T>(string path, Func<T, bool> validate) where T : class
    {
        try
        {
            EnhancementPaths.RejectLinks(path);
            if (!File.Exists(path)) return new(EnhancementReadStatus.Missing, null);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxDocumentBytes) return new(EnhancementReadStatus.Invalid, null, Error: "Document is too large.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var payload = bytes.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(payload));
            using var parsed = JsonDocument.Parse(payload);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var number))
                return new(EnhancementReadStatus.Invalid, null, hash, "Missing schemaVersion.");
            if (number != 1) return new(EnhancementReadStatus.UnsupportedSchema, null, hash, "Unsupported enhancement schema.");
            var value = JsonSerializer.Deserialize<T>(payload, Options);
            return value is not null && validate(value)
                ? new(EnhancementReadStatus.Valid, value, hash)
                : new(EnhancementReadStatus.Invalid, null, hash, "Invalid enhancement document.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { return new(EnhancementReadStatus.Invalid, null, Error: ex.Message); }
        catch (Exception ex) when (EnhancementPaths.IsExpectedFailure(ex))
        { return new(EnhancementReadStatus.Unavailable, null, Error: ex.Message); }
    }

    private void Write<T>(string path, T document, string? expectedContentSha256, bool replaceDerived,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnhancementPaths.RejectLinks(path);
        if (!Directory.Exists(_instance)) throw new DirectoryNotFoundException(_instance);
        Directory.CreateDirectory(DirectoryPath);
        EnhancementPaths.RejectLinks(DirectoryPath);
        // Shared across cooperating processes. Leave the empty lock file in place to avoid inode races.
        var gatePath = Path.Combine(DirectoryPath, ".write.lock");
        EnhancementPaths.RejectLinks(gatePath);
        using var gate = new FileStream(gatePath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        EnhancementPaths.RejectLinks(path);
        var before = ReadBytesHash(path);
        if (!replaceDerived && !string.Equals(before, expectedContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Enhancement declaration changed; reload before applying the edit.");
        if (replaceDerived && ReadResolvedDocument().Status == EnhancementReadStatus.UnsupportedSchema)
            throw new IOException("A newer resolved report was preserved; use the in-memory analysis.");
        if (!replaceDerived && before is not null && ReadDeclaration().Status != EnhancementReadStatus.Valid)
            throw new IOException("An invalid or unsupported declaration must be preserved.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        if (bytes.Length > MaxDocumentBytes) throw new InvalidDataException("Document is too large.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { target.Write(bytes); target.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested();
            EnhancementPaths.RejectLinks(path);
            if (!string.Equals(ReadBytesHash(path), before, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Enhancement document changed while preparing the write.");
            File.Move(temporary, path, overwrite: before is not null);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string? ReadBytesHash(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxDocumentBytes) throw new InvalidDataException("Existing document is too large.");
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
