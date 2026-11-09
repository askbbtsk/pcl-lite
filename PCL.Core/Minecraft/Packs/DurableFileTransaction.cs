using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace PCL.Core.Minecraft.Packs;

/// <summary>A declared game or metadata change. A null new hash represents deletion.</summary>
public sealed record TransactionFileChange(string RootId, string RelativePath, string? ExpectedOldSha256,
    string? NewSha256, string? PayloadPath = null, byte[]? Content = null, bool IsMetadata = false);

/// <summary>Write-ahead, content-checked recovery for files across explicitly supplied roots.</summary>
public static class DurableFileTransaction
{
    private sealed record Entry(string RootId, string RelativePath, string? Before, string? After,
        string? Backup, string? Staged, bool IsMetadata);
    private sealed record Journal(int Schema, Guid Id, Dictionary<string, string> Roots, List<Entry> Entries,
        string? StorageDirectory = null);
    private sealed record Commit(Guid Id, string JournalSha256);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Apply(string storageDirectory, IReadOnlyDictionary<string, string> allowedRoots,
        IReadOnlyList<TransactionFileChange> changes, CancellationToken token = default,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Any(change => change is null)) throw new InvalidDataException("Null transaction change.");
        var roots = NormalizeRoots(allowedRoots);
        if (changes.Count > 100000) throw new InvalidDataException("Too many transaction changes.");
        var storage = Path.GetFullPath(storageDirectory);
        CheckLinks(storage);
        Directory.CreateDirectory(storage);
        using var gate = Lock(storage);
        RecoverCore(storage, roots);
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var directory = Path.Combine(storage, "tx-" + id.ToString("N"));
        Directory.CreateDirectory(directory);
        var entries = new List<Entry>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var journalDurable = false;
        var committed = false;
        try
        {
            foreach (var change in changes.OrderBy(change => change.IsMetadata))
            {
                token.ThrowIfCancellationRequested();
                ValidateHash(change.ExpectedOldSha256);
                ValidateHash(change.NewSha256);
                var target = Resolve(roots, change.RootId, change.RelativePath);
                if (target.Equals(storage, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(Path.TrimEndingDirectorySeparator(storage) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A transaction cannot modify its own recovery storage.");
                if (!destinations.Add(target)) throw new InvalidDataException("Duplicate transaction destination.");
                if (Directory.Exists(target)) throw new IOException("A directory occupies a file destination.");
                if (!Equal(HashOrMissing(target), change.ExpectedOldSha256))
                    throw new IOException("Destination changed after preview: " + change.RelativePath);
                var number = entries.Count.ToString("D8");
                string? backup = null;
                if (change.ExpectedOldSha256 is not null)
                {
                    backup = "old-" + number + ".bin";
                    CopyDurable(target, Path.Combine(directory, backup), token);
                    if (!Equal(HashOrMissing(Path.Combine(directory, backup)), change.ExpectedOldSha256))
                        throw new IOException("Source changed during backup: " + change.RelativePath);
                }
                string? staged = null;
                if (change.NewSha256 is not null)
                {
                    if ((change.PayloadPath is null) == (change.Content is null))
                        throw new InvalidDataException("A replacement needs exactly one payload source.");
                    staged = "new-" + number + ".bin";
                    if (change.PayloadPath is not null)
                    {
                        CheckLinks(change.PayloadPath);
                        CopyDurable(change.PayloadPath, Path.Combine(directory, staged), token);
                    }
                    else WriteDurable(Path.Combine(directory, staged), change.Content!);
                    if (!Equal(HashOrMissing(Path.Combine(directory, staged)), change.NewSha256))
                        throw new InvalidDataException("Staged payload checksum mismatch: " + change.RelativePath);
                }
                else if (change.PayloadPath is not null || change.Content is not null)
                    throw new InvalidDataException("Deletion cannot contain replacement content.");
                entries.Add(new(change.RootId, change.RelativePath, change.ExpectedOldSha256,
                    change.NewSha256, backup, staged, change.IsMetadata));
            }
            var journal = new Journal(2, id, roots, entries, storage);
            var journalBytes = JsonSerializer.SerializeToUtf8Bytes(journal, Options);
            if (journalBytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Transaction journal is too large.");
            WriteDurable(Path.Combine(directory, "transaction.json"), journalBytes);
            journalDurable = true;
            var complete = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var target = Resolve(roots, entry.RootId, entry.RelativePath);
                if (!Equal(HashOrMissing(target), entry.Before))
                    throw new IOException("Destination changed before commit: " + entry.RelativePath);
                if (entry.After is null) File.Delete(target);
                else
                {
                    PublishReplacement(Path.Combine(directory, entry.Staged!), target, entry.Before, entry.After, token);
                }
                progress?.Invoke(++complete, entries.Count);
            }
            token.ThrowIfCancellationRequested();
            foreach (var entry in entries)
                if (!Equal(HashOrMissing(Resolve(roots, entry.RootId, entry.RelativePath)), entry.After))
                    throw new IOException("Destination changed while committing: " + entry.RelativePath);
            WriteDurable(Path.Combine(directory, "commit.json"), JsonSerializer.SerializeToUtf8Bytes(
                new Commit(id, Hash(journalBytes)), Options));
            committed = true;
        }
        catch (Exception original)
        {
            if (journalDurable && !committed)
            {
                try { RecoverOne(directory, roots); }
                catch (Exception recovery)
                {
                    throw new AggregateException("Transaction recovery needs attention; original files were retained at " + directory,
                        original, recovery);
                }
            }
            else if (!journalDurable) TryCleanup(directory);
            throw;
        }
        finally { if (committed) TryCleanup(directory); }
    }

    public static int Recover(string storageDirectory, IReadOnlyDictionary<string, string> allowedRoots)
    {
        var roots = NormalizeRoots(allowedRoots);
        var storage = Path.GetFullPath(storageDirectory);
        CheckLinks(storage);
        if (!Directory.Exists(storage)) return 0;
        using var gate = Lock(storage);
        return RecoverCore(storage, roots);
    }

    private static int RecoverCore(string storage, Dictionary<string, string> roots)
    {
        var count = 0;
        foreach (var directory in Directory.EnumerateDirectories(storage, "tx-*", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory)[3..], "N", out _)) continue;
            RecoverOne(directory, roots);
            count++;
        }
        return count;
    }

    private static void RecoverOne(string directory, Dictionary<string, string> roots)
    {
        CheckLinks(directory);
        var journalPath = Path.Combine(directory, "transaction.json");
        var bytes = ReadDocument(journalPath);
        var journal = DeserializeDocument<Journal>(bytes)
            ?? throw new InvalidDataException("Missing transaction journal.");
        if (journal.Schema is not (1 or 2) || journal.Id == Guid.Empty || "tx-" + journal.Id.ToString("N") != Path.GetFileName(directory)
            || journal.Roots is null || journal.Entries is null || journal.Entries.Count > 100000
            || journal.Roots.Count != roots.Count || journal.Roots.Any(root => !roots.TryGetValue(root.Key, out var expected)
                || !string.Equals(expected, root.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Transaction journal does not match the supplied roots.");
        var storage = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(directory)!);
        if (journal.Schema == 2 && !string.Equals(journal.StorageDirectory, storage, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Transaction journal was moved from its recovery storage.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            if (entry is null) throw new InvalidDataException("Invalid transaction entry.");
            ValidateHash(entry.Before); ValidateHash(entry.After);
            var target = Resolve(roots, entry.RootId, entry.RelativePath);
            if (target.Equals(storage, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(storage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovery cannot modify its own transaction storage.");
            if (!seen.Add(target))
                throw new InvalidDataException("Duplicate journal destination.");
            var number = (seen.Count - 1).ToString("D8");
            if (entry.Backup != (entry.Before is null ? null : "old-" + number + ".bin")
                || entry.Staged != (entry.After is null ? null : "new-" + number + ".bin"))
                throw new InvalidDataException("Invalid transaction recovery asset mapping.");
        }
        var markerPath = Path.Combine(directory, "commit.json");
        CheckLinks(markerPath);
        if (Directory.Exists(markerPath)) throw new InvalidDataException("A directory occupies the transaction commit marker.");
        if (File.Exists(markerPath))
        {
            var marker = DeserializeDocument<Commit>(ReadDocument(markerPath));
            if (marker is null || marker.Id != journal.Id || !Equal(marker.JournalSha256, Hash(bytes)))
                throw new InvalidDataException("Invalid transaction commit marker; data was preserved.");
            TryCleanup(directory);
            return;
        }
        foreach (var entry in journal.Entries)
            if (entry.Before is not null && !Equal(HashOrMissing(BackupPath(directory, entry.Backup)), entry.Before))
                throw new InvalidDataException("Missing or damaged original backup.");
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            var target = Resolve(roots, entry.RootId, entry.RelativePath);
            var current = HashOrMissing(target);
            if (Equal(current, entry.Before)) continue;
            if (!Equal(current, entry.After)) throw new IOException("External change was preserved: " + entry.RelativePath);
            if (entry.Before is null) File.Delete(target);
            else
            {
                PublishReplacement(BackupPath(directory, entry.Backup), target, current, entry.Before, CancellationToken.None);
            }
        }
        TryCleanup(directory);
    }

    private static Dictionary<string, string> NormalizeRoots(IReadOnlyDictionary<string, string> supplied)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        if (supplied.Count == 0 || supplied.Count > 16) throw new InvalidDataException("Invalid transaction roots.");
        return supplied.ToDictionary(pair => pair.Key, pair =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.Value));
            CheckLinks(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            return root;
        }, StringComparer.Ordinal);
    }

    private static string Resolve(Dictionary<string, string> roots, string id, string relative)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Missing journal root.");
        if (!roots.TryGetValue(id, out var root)) throw new InvalidDataException("Unapproved journal root.");
        PackUpdatePlanner.ValidateRelativePath(relative);
        var target = PackUpdatePlanner.ResolveWithin(root, relative);
        CheckLinks(target);
        return target;
    }

    private static FileStream Lock(string storage)
    {
        var path = Path.Combine(storage, ".journal.lock");
        CheckLinks(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static string BackupPath(string directory, string? name)
    {
        if (name is null || name.Length != 16 || !name.StartsWith("old-", StringComparison.Ordinal)
            || !name.EndsWith(".bin", StringComparison.Ordinal) || !name.AsSpan(4, 8).ToString().All(char.IsAsciiDigit))
            throw new InvalidDataException("Invalid transaction backup name.");
        var path = Path.Combine(directory, name);
        CheckLinks(path);
        return path;
    }

    private static byte[] ReadDocument(string path)
    {
        CheckLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Transaction journal is too large.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }

    private static T? DeserializeDocument<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicates(document.RootElement);
        return document.RootElement.Deserialize<T>(Options);
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate transaction JSON property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }

    private static void PublishReplacement(string source, string target, string? expectedCurrent, string expectedNew,
        CancellationToken token)
    {
        var parent = Path.GetDirectoryName(target)!;
        CheckLinks(parent);
        Directory.CreateDirectory(parent);
        // Stage on the target volume. Metadata and game content may live on different drives.
        var temporary = Path.Combine(parent, ".pcl-transaction-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CopyDurable(source, temporary, token);
            if (!Equal(HashOrMissing(temporary), expectedNew)) throw new InvalidDataException("Replacement payload changed.");
            token.ThrowIfCancellationRequested();
            if (!Equal(HashOrMissing(target), expectedCurrent)) throw new IOException("Destination changed before replacement.");
            CheckLinks(target);
            File.Move(temporary, target, overwrite: expectedCurrent is not null);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void CopyDurable(string source, string target, CancellationToken token)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
        output.Flush(flushToDisk: true);
    }

    private static void WriteDurable(string target, byte[] bytes)
    {
        var temp = target + ".tmp";
        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { output.Write(bytes); output.Flush(flushToDisk: true); }
        File.Move(temp, target, overwrite: false);
    }

    private static string? HashOrMissing(string path)
    {
        CheckLinks(path);
        if (Directory.Exists(path)) throw new IOException("A directory occupies a transaction file path.");
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Equal(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static void ValidateHash(string? hash)
    {
        if (hash is not null && (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("Invalid transaction SHA-256.");
    }

    private static void CheckLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Transactions cannot traverse links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void TryCleanup(string directory)
    {
        try
        {
            CheckLinks(directory);
            // Inspect first; do not recursively delete a later user-added subtree or a similarly named file.
            var paths = Directory.EnumerateFileSystemEntries(directory).ToArray();
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                if (Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                    || !(name is "transaction.json" or "transaction.json.tmp" or "commit.json" or "commit.json.tmp"
                        || IsPayloadName(name, "old-") || IsPayloadName(name, "new-"))) return;
            }
            // Retire the complete directory atomically before deleting recovery evidence. A crash during
            // cleanup can then leave only an inert retired directory, never a tx-* with a missing journal.
            var retired = Path.Combine(Path.GetDirectoryName(directory)!, ".completed-" + Path.GetFileName(directory));
            if (Directory.Exists(retired) || File.Exists(retired)) return;
            Directory.Move(directory, retired);
            foreach (var oldPath in paths)
            {
                var path = Path.Combine(retired, Path.GetFileName(oldPath));
                if (Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                File.Delete(path);
            }
            // No recursion: a concurrent external addition is preserved and prevents directory removal.
            Directory.Delete(retired, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsPayloadName(string name, string prefix)
        => name.Length == 16 && name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(".bin", StringComparison.Ordinal) && name.AsSpan(4, 8).ToString().All(char.IsAsciiDigit);
}
