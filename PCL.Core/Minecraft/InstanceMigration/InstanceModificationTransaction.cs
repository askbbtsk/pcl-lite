using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Minecraft.InstanceMigration;

public sealed record ModificationPayload(string RelativePath, string PayloadPath, string? ExpectedOldSha256, bool Metadata = false);
public sealed record ModificationModReplacement(string SourcePath, string SourceSha256, string PayloadPath, string TargetFileName);
public sealed record InstanceModificationPlan(IReadOnlyList<TransactionFileChange> Changes, IReadOnlyList<string> RetiredFiles);

/// <summary>Preserves original mods as disabled files and journals them with the installed core changes.</summary>
public static class InstanceModificationTransaction
{
    public static string StoragePath(string instanceDirectory)
        => Path.Combine(Path.GetFullPath(instanceDirectory), "PCL", "Enhancements", "ModificationTransactions");

    public static int Recover(string instanceDirectory, string gameDirectory)
        => DurableFileTransaction.Recover(StoragePath(instanceDirectory), Roots(instanceDirectory, gameDirectory));

    public static string? HashOrMissing(string path)
    {
        RejectLinks(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static InstanceModificationPlan Prepare(string instanceDirectory, string gameDirectory,
        IReadOnlyList<ModificationPayload> instancePayloads, IReadOnlyList<ModificationModReplacement> replacements,
        IReadOnlyList<ModificationPayload>? additionalMods = null)
    {
        var instance = Path.GetFullPath(instanceDirectory); var game = Path.GetFullPath(gameDirectory);
        RejectLinks(instance); RejectLinks(game);
        var changes = new List<TransactionFileChange>(); var retired = new List<string>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(TransactionFileChange change)
        {
            var root = change.RootId == "instance" ? instance : game;
            var path = PackUpdatePlanner.ResolveWithin(root, change.RelativePath); RejectLinks(path);
            if (!destinations.Add(path)) throw new InvalidDataException("Duplicate modification target: " + change.RelativePath);
            changes.Add(change);
        }
        foreach (var payload in instancePayloads)
        {
            var target = PackUpdatePlanner.ResolveWithin(instance, payload.RelativePath);
            if (!string.Equals(HashOrMissing(target), payload.ExpectedOldSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Instance file changed during installation: " + payload.RelativePath);
            Add(new("instance", payload.RelativePath, payload.ExpectedOldSha256,
                HashOrMissing(payload.PayloadPath) ?? throw new FileNotFoundException("Installed payload is missing."), payload.PayloadPath, IsMetadata: payload.Metadata));
        }
        foreach (var replacement in replacements)
        {
            var source = Path.GetFullPath(replacement.SourcePath);
            var mods = Path.GetFullPath(Path.Combine(game, "mods"));
            if (!string.Equals(Path.GetDirectoryName(source), mods, StringComparison.OrdinalIgnoreCase)
                || !InstanceMigrationPlanner.ValidFileName(replacement.TargetFileName))
                throw new InvalidDataException("A modification may only replace selected direct mod files.");
            if (!string.Equals(InstanceMigrationPlanner.DisabledSuffix(source),
                    InstanceMigrationPlanner.DisabledSuffix(replacement.TargetFileName), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A replacement must preserve the original disabled suffix.");
            var oldHash = HashOrMissing(source);
            if (!string.Equals(oldHash, replacement.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Selected mod changed after preview: " + source);
            var newHash = HashOrMissing(replacement.PayloadPath) ?? throw new FileNotFoundException("Target mod payload is missing.");
            var targetRelative = Path.Combine("mods", replacement.TargetFileName);
            var target = PackUpdatePlanner.ResolveWithin(game, targetRelative); var targetHash = HashOrMissing(target);
            if (newHash == oldHash && string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && targetHash is not null && targetHash != newHash)
                throw new IOException("A different target mod already exists: " + replacement.TargetFileName);
            // Persist original bytes before replacing/removing their old name. Never re-enable a disabled file.
            var originalName = Path.GetFileName(source);
            var preservedName = InstanceMigrationPlanner.DisabledSuffix(source).Length == 0 ? originalName + ".disabled"
                : originalName + ".pcl-retired-" + oldHash![..12] + ".disabled";
            var preservedRelative = Path.Combine("mods", preservedName);
            var preserved = PackUpdatePlanner.ResolveWithin(game, preservedRelative);
            if (string.Equals(preserved, target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The disabled original collides with the replacement name.");
            var preservedHash = HashOrMissing(preserved);
            if (preservedHash is not null && preservedHash != oldHash)
                throw new IOException("A different disabled original already exists: " + preservedName);
            if (preservedHash is null) Add(new("game", preservedRelative, null, oldHash, source));
            retired.Add(preservedName);
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                Add(new("game", Path.Combine("mods", originalName), oldHash, null));
            if (targetHash != newHash) Add(new("game", targetRelative, targetHash, newHash, replacement.PayloadPath));
        }
        foreach (var additional in additionalMods ?? [])
        {
            PackUpdatePlanner.ValidateRelativePath(additional.RelativePath);
            if (!additional.RelativePath.Replace('\\', '/').StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
                || additional.RelativePath.Replace('\\', '/').Split('/').Length != 2)
                throw new InvalidDataException("Additional mod must be a direct mods entry.");
            var target = PackUpdatePlanner.ResolveWithin(game, additional.RelativePath); var existing = HashOrMissing(target);
            var fresh = HashOrMissing(additional.PayloadPath) ?? throw new FileNotFoundException("Dependency payload is missing.");
            if (existing == fresh) continue;
            if (existing is not null) throw new IOException("An existing dependency has different content: " + additional.RelativePath);
            Add(new("game", additional.RelativePath, null, fresh, additional.PayloadPath));
        }
        return new(changes, retired);
    }

    public static void Apply(string instanceDirectory, string gameDirectory, InstanceModificationPlan plan,
        CancellationToken token = default)
        => DurableFileTransaction.Apply(StoragePath(instanceDirectory), Roots(instanceDirectory, gameDirectory), plan.Changes, token);

    private static IReadOnlyDictionary<string, string> Roots(string instanceDirectory, string gameDirectory)
        => new Dictionary<string, string> { ["instance"] = instanceDirectory, ["game"] = gameDirectory };

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Modification cannot follow links."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
