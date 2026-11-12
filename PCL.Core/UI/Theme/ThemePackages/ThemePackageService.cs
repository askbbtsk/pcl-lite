using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.UI.Theme.Packages;

/// <summary>Local, bounded installation. Inspection never writes to the source or installed theme.</summary>
public sealed class ThemePackageService(string themesDirectory, string temporaryDirectory)
{
    public string ThemesDirectory { get; } = Path.GetFullPath(themesDirectory);
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public IReadOnlyList<ThemeCatalogItem> Scan()
    {
        Directory.CreateDirectory(ThemesDirectory);
        return Directory.EnumerateDirectories(ThemesDirectory).Order(StringComparer.OrdinalIgnoreCase)
            .Select(directory =>
            {
                try
                {
                    var manifest = ReadManifest(ReadBounded(ThemeFilePaths.Resolve(directory, "theme.json"), 65536));
                    if (!Path.GetFileName(directory).Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Theme directory does not match its ID.");
                    return new ThemeCatalogItem(directory, manifest, null);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { return new ThemeCatalogItem(directory, null, error.Message); }
            }).ToArray();
    }

    public Task<ThemePackage> InspectAsync(string source, CancellationToken token = default) =>
        Task.Run(() => Inspect(source, token), token);

    public static bool IsThemeXaml(string file)
    {
        if (!file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) return false;
        return ThemeXamlPolicy.ReadDocument(ReadBounded(file, ThemeApi.MaximumFileBytes), Path.GetFileName(file)).Root?.Name
            == ThemeXamlPolicy.Presentation + "ResourceDictionary";
    }

    private ThemePackage Inspect(string source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Directory.Exists(source))
            return ReadDirectory(source, source, token);
        if (source.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsThemeXaml(source)) throw new InvalidDataException("The XAML is not a theme resource dictionary.");
            string filename = Path.GetFileName(source);
            string id = "local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filename)))[..16].ToLowerInvariant();
            var manifest = new ThemeManifest { Id = id, Name = Path.GetFileNameWithoutExtension(source), Entry = filename };
            string root = Path.GetDirectoryName(Path.GetFullPath(source))!;
            // A single XAML import deliberately ignores unrelated neighbouring manifests/files.
            return ReadSnapshot(root, manifest, source, token);
        }
        if (!source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected a theme directory, XAML resource dictionary or ZIP.");
        if (new FileInfo(source).Length > ThemeApi.MaximumPackageBytes)
            throw new InvalidDataException("Theme ZIP exceeds 128 MiB.");
        string stage = CreateStage();
        try
        {
            using var zip = ZipFile.OpenRead(source);
            if (zip.Entries.Count > ThemeApi.MaximumFiles)
                throw new InvalidDataException("Too many theme archive entries.");
            long total = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                // Reject UNIX symlinks and Windows reparse entries before extraction.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                    || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Theme archives cannot contain links.");
                bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                string relative = ThemeFilePaths.Normalize(entry.FullName.TrimEnd('/', '\\'));
                if (!paths.Add(relative)) throw new InvalidDataException($"Duplicate theme archive entry: {relative}");
                if (entry.Length > ThemeApi.MaximumFileBytes || (total += entry.Length) > ThemeApi.MaximumPackageBytes)
                    throw new InvalidDataException("Theme archive size limit exceeded.");
                string destination = ThemeFilePaths.Resolve(stage, relative);
                if (directory) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyBounded(input, output, entry.Length, token);
            }
            return ReadDirectory(stage, source, token);
        }
        finally { DeleteStage(stage); }
    }

    private static ThemePackage ReadDirectory(string root, string source, CancellationToken token)
    {
        string path = ThemeFilePaths.Resolve(root, "theme.json");
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Theme manifest exceeds 64 KiB.");
        return ReadSnapshot(root, ReadManifest(ReadBounded(path, 65536)), source, token);
    }

    private static ThemeManifest ReadManifest(byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<ThemeManifest>(bytes, ThemeApi.JsonOptions)
            ?? throw new InvalidDataException("Missing theme manifest.");
        manifest.Validate();
        return manifest;
    }

    private static ThemePackage ReadSnapshot(string root, ThemeManifest manifest, string source, CancellationToken token)
    {
        manifest.Validate();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(new[] { manifest.Entry, manifest.Light, manifest.Dark, manifest.Glass }
            .Where(path => path is not null).Select(path => ThemeFilePaths.Normalize(path!)));
        long total = 0;
        int totalNodes = 0;
        while (pending.TryDequeue(out string? relative))
        {
            token.ThrowIfCancellationRequested();
            if (files.ContainsKey(relative)) continue;
            if (files.Count >= ThemeApi.MaximumFiles) throw new InvalidDataException("Too many theme dependencies.");
            string path = ThemeFilePaths.Resolve(root, relative);
            long size = new FileInfo(path).Length;
            if (size > ThemeApi.MaximumFileBytes || (total += size) > ThemeApi.MaximumPackageBytes)
                throw new InvalidDataException("Theme dependency size limit exceeded.");
            byte[] bytes = ReadBounded(path, ThemeApi.MaximumFileBytes);
            if (bytes.LongLength != size) throw new IOException($"Theme file changed while reading: {relative}");
            files.Add(relative, bytes);
            if (relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                if ((totalNodes += ThemeXamlPolicy.ReadDocument(bytes, relative).Descendants().Count()) > 24000)
                    throw new InvalidDataException("Theme package exceeds 24000 total XAML nodes.");
                foreach (string dependency in ThemeXamlPolicy.Validate(bytes, relative))
                    pending.Enqueue(dependency);
            }
            else if (!new[] { ".png", ".jpg", ".jpeg", ".webp", ".ico", ".ttf", ".otf" }
                         .Contains(Path.GetExtension(relative).ToLowerInvariant()))
                throw new InvalidDataException($"Unsupported theme resource: {relative}");
        }
        ThemeXamlPolicy.ValidateDependencyGraph(files);
        files["theme.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, ThemeApi.JsonOptions);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Key + "\0"));
            hash.AppendData(SHA256.HashData(file.Value));
        }
        return new ThemePackage(manifest, files, Convert.ToHexString(hash.GetHashAndReset()), source);
    }

    public async Task InstallAsync(ThemePackage package, bool replace, CancellationToken token = default)
    {
        package.Manifest.Validate();
        await _installGate.WaitAsync(token).ConfigureAwait(false);
        string? stage = null, backup = null;
        try
        {
            Directory.CreateDirectory(ThemesDirectory);
            // Stage next to the destination so the final directory rename stays on one volume.
            string parent = Path.GetDirectoryName(ThemesDirectory)!;
            stage = Path.Combine(parent, ".theme-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            foreach (var file in package.Files)
            {
                token.ThrowIfCancellationRequested();
                string path = ThemeFilePaths.Resolve(stage, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, file.Value, token).ConfigureAwait(false);
            }
            var staged = ReadDirectory(stage, package.SourcePath, token);
            if (staged.ContentHash != package.ContentHash)
                throw new InvalidDataException("Theme snapshot changed before installation.");
            string destination = ThemeFilePaths.Resolve(ThemesDirectory, package.Manifest.Id);
            if (Directory.Exists(destination))
            {
                if (!replace) throw new IOException("Theme is already installed; replacement requires confirmation.");
                backup = Path.Combine(parent, ".theme-previous-" + Guid.NewGuid().ToString("N"));
                Directory.Move(destination, backup);
            }
            try { token.ThrowIfCancellationRequested(); Directory.Move(stage, destination); stage = null; }
            catch
            {
                if (backup is not null) { Directory.Move(backup, destination); backup = null; }
                throw;
            }
        }
        finally
        {
            if (stage is not null) DeleteStage(stage);
            if (backup is not null) DeleteStage(backup);
            _installGate.Release();
        }
    }

    private string CreateStage()
    {
        Directory.CreateDirectory(temporaryDirectory);
        string path = Path.Combine(temporaryDirectory, "theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteStage(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CopyBounded(Stream input, Stream output, long expected, CancellationToken token)
    {
        var buffer = new byte[81920];
        long written = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if ((written += count) > expected) throw new InvalidDataException("Archive entry exceeds declared length.");
            output.Write(buffer, 0, count);
        }
        if (written != expected) throw new InvalidDataException("Incomplete archive entry.");
    }

    private static byte[] ReadBounded(string path, long limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException($"Theme file exceeds its size limit: {path}");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException($"Theme file changed while reading: {path}");
        return bytes;
    }
}
