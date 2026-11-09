using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PCL.Core.Minecraft.ResourceCollections;

/// <summary>Reads file locations only. Indexing never creates, moves, deletes or opens archive contents.</summary>
public static class ResourceCollectionIndexer
{
    public static ResourceCollectionDocument Capture(string name, IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > 10000) throw new ArgumentException("A collection can contain at most 10000 selected paths.", nameof(paths));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("Invalid collection name.", nameof(name));
        var entries = new List<ResourceCollectionEntry>();
        var notices = new List<string>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Inspect(path);
            var samePhysical = entry.PhysicalIdentity is null ? -1 : entries.FindIndex(existing =>
                string.Equals(existing.PhysicalIdentity, entry.PhysicalIdentity, StringComparison.Ordinal));
            var sameNormalized = entries.FindIndex(existing => string.Equals(existing.CanonicalPath,
                entry.CanonicalPath, StringComparison.OrdinalIgnoreCase));
            var index = samePhysical >= 0 ? samePhysical : sameNormalized;
            if (index >= 0)
            {
                var existing = entries[index];
                if (existing.PhysicalIdentity is not null && entry.PhysicalIdentity is not null
                    && existing.PhysicalIdentity != entry.PhysicalIdentity)
                {
                    notices.Add("Case-sensitive paths identify different files; both were retained: " + path);
                    entries.Add(entry);
                    continue;
                }
                // Unresolved short names are not evidence of a different file or a reason to discard it.
                if (samePhysical < 0 && (existing.ShortNameUnresolved || entry.ShortNameUnresolved)
                    && !string.Equals(existing.CanonicalPath, entry.CanonicalPath, StringComparison.Ordinal))
                { entries.Add(entry); continue; }
                var aliases = existing.OriginalPaths.Concat(entry.OriginalPaths).Distinct(StringComparer.Ordinal).ToList();
                entries[index] = existing with { OriginalPaths = aliases };
                if (aliases.Count > existing.OriginalPaths.Count)
                    notices.Add("The same file was selected using more than one path spelling: " + path);
            }
            else entries.Add(entry);
        }
        return new() { Name = name, Entries = entries, Notices = notices };
    }

    public static ResourceCollectionDocument Refresh(ResourceCollectionDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1) throw new InvalidDataException("Unsupported collection schema.");
        var refreshed = Capture(document.Name, document.Entries.SelectMany(entry => entry.OriginalPaths).ToArray(), cancellationToken);
        var entries = refreshed.Entries.Select(entry => entry with
        {
            ExtensionData = document.Entries.FirstOrDefault(original =>
                original.OriginalPaths.Any(alias => entry.OriginalPaths.Contains(alias, StringComparer.Ordinal)))?.ExtensionData,
        }).ToList();
        return refreshed with { Id = document.Id, Entries = entries, ExtensionData = document.ExtensionData };
    }

    private static ResourceCollectionEntry Inspect(string path)
    {
        var entry = new ResourceCollectionEntry { CanonicalPath = path, OriginalPaths = [path] };
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.");
            var full = ResourceCollectionPaths.Normalize(path);
            entry = entry with { CanonicalPath = full };
            if (ResourceCollectionPaths.HasLinkAncestor(full)) return entry with { Availability = ResourceAvailability.Linked };
            if (Directory.Exists(full)) return entry with { Availability = ResourceAvailability.Unsupported, Error = "Select individual mod files, not a directory." };
            if (!File.Exists(full)) return entry with { Availability = ResourceAvailability.Missing, ShortNameUnresolved = full.Contains('~') };
            var expanded = ResourceCollectionPaths.ExpandLongName(full, out var unresolved);
            entry = entry with { CanonicalPath = expanded, ShortNameUnresolved = unresolved };
            if (!ResourceCollectionPaths.IsSupported(expanded)) return entry with { Availability = ResourceAvailability.Unsupported };
            return entry with { Availability = ResourceAvailability.Available,
                PhysicalIdentity = ResourceCollectionPaths.PhysicalIdentity(expanded) };
        }
        catch (Exception ex) when (ResourceCollectionPaths.ExpectedFailure(ex))
        { return entry with { Availability = ResourceAvailability.Unavailable, Error = ex.Message }; }
    }
}
