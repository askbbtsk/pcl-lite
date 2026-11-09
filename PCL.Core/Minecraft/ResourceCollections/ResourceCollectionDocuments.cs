using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.ResourceCollections;

public enum ResourceAvailability { Available, Missing, Linked, Unavailable, Unsupported }

/// <summary>A location index, never a replacement mod directory or virtual classpath.</summary>
public sealed record ResourceCollectionEntry
{
    public string CanonicalPath { get; init; } = "";
    public List<string> OriginalPaths { get; init; } = [];
    public ResourceAvailability Availability { get; init; }
    public string? PhysicalIdentity { get; init; }
    public bool ShortNameUnresolved { get; init; }
    public string? Error { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ResourceCollectionDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public List<ResourceCollectionEntry> Entries { get; init; } = [];
    public List<string> Notices { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public enum ResourceCollectionReadStatus { Missing, Valid, Invalid, UnsupportedSchema, Unavailable }
public sealed record ResourceCollectionReadResult(ResourceCollectionReadStatus Status,
    ResourceCollectionDocument? Document = null, string? ContentSha256 = null, string? Error = null);

public enum ResourceCopyStatus { Copied, AlreadyPresent, Failed, Cancelled }
public enum ResourceCopyFailure
{
    None, Missing, Unsupported, Linked, OccupiedOrUnreadable, NameConflict, SourceChanged,
    VerificationFailed, InvalidArchive, InvalidPath, Cancelled,
}

public sealed record ResourceCopyItem(string SourcePath, string? DestinationPath, ResourceCopyStatus Status,
    ResourceCopyFailure Failure = ResourceCopyFailure.None, string? Error = null, string? Sha256 = null);

public sealed record ResourceCopyReport(IReadOnlyList<ResourceCopyItem> Items, bool Cancelled)
{
    public int CopiedCount => System.Linq.Enumerable.Count(Items, item => item.Status == ResourceCopyStatus.Copied);
    public int AlreadyPresentCount => System.Linq.Enumerable.Count(Items, item => item.Status == ResourceCopyStatus.AlreadyPresent);
    public int FailedCount => System.Linq.Enumerable.Count(Items, item => item.Status == ResourceCopyStatus.Failed);
}
