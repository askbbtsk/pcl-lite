using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Enhancements;

/// <summary>Optional component information. Every combination of the three flags is meaningful.</summary>
public sealed record ComponentDescriptor
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Source { get; init; }
    public bool Important { get; init; }
    public bool DependencyOnly { get; init; }
    public bool Disabled { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>User declarations are never reconstructed from the disposable resolution report.</summary>
public sealed record InstanceEnhancementDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid? InstanceId { get; init; }
    public List<ComponentDescriptor> Components { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public enum EnhancementReadStatus { Missing, Valid, Invalid, UnsupportedSchema, Unavailable }

public sealed record EnhancementReadResult<T>(EnhancementReadStatus Status, T? Value,
    string? ContentSha256 = null, string? Error = null);

/// <summary>Runtime identity affects effective resolution, but never selects Java itself.</summary>
public sealed record ResolutionJavaIdentity(int MajorVersion, string Architecture, string RuntimePath,
    string? FullVersion = null);

/// <summary>Selected Java facts only; machine-specific runtime paths are represented by a digest.</summary>
public sealed record ResolutionJavaEvidence(int MajorVersion, string Architecture, string? FullVersion,
    string RuntimeIdentitySha256);

/// <summary>Ordered metadata input evidence. External sources expose only a filename and path digest.</summary>
public sealed record ResolutionSourceEvidence(string? RelativePath, string FileName, bool IsExternal,
    string PathIdentitySha256, string ContentSha256);

/// <summary>Already resolved by the existing host library resolver, including native/platform rules.</summary>
public sealed record EnhancementLibrary(string Path, string? Name = null, string? Sha1 = null,
    long? Size = null, string? Sha256 = null, bool IsLocal = false);

public sealed record EnhancementAnalysisInput
{
    public required string InstanceDirectory { get; init; }
    public required string EffectiveVersionJson { get; init; }
    public string? MinecraftVersion { get; init; }
    public IReadOnlyList<ComponentDescriptor> DeclaredComponents { get; init; } = [];
    public IReadOnlyList<EnhancementLibrary> Libraries { get; init; } = [];
    public IReadOnlyList<string> AllowedLibraryRoots { get; init; } = [];
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
    public string Platform { get; init; } = "windows";
    public string Architecture { get; init; } = "x64";
    public string AdapterVersion { get; init; } = "1";
    public string? SourceMetadataSha256 { get; init; }
    public ResolutionJavaIdentity? Java { get; init; }
}

public enum LibraryHealthStatus { Valid, Missing, Damaged, Unverified, Unavailable, Changed, UnsafePath }

public sealed record LibraryHealthEntry
{
    public required string Path { get; init; }
    public string? Name { get; init; }
    public LibraryHealthStatus Status { get; init; }
    public long? ActualLength { get; init; }
    public string? ActualSha256 { get; init; }
    public string? DeclaredSha1 { get; init; }
    public string? DeclaredSha256 { get; init; }
    public long? DeclaredSize { get; init; }
    public bool IsLocal { get; init; }
    public string? Error { get; init; }
}

/// <summary>Derived evidence only. It never authorizes replacement of a game file.</summary>
public sealed record InstanceResolvedReport
{
    public int SchemaVersion { get; init; } = 1;
    public string ParserVersion { get; init; } = InstanceEnhancementAnalyzer.ParserVersion;
    public required string Fingerprint { get; init; }
    public string? EffectiveVersionSha256 { get; init; }
    public string? MinecraftVersion { get; init; }
    public string? Platform { get; init; }
    public string? Architecture { get; init; }
    public string? AdapterVersion { get; init; }
    public string? SourceMetadataSha256 { get; init; }
    public ResolutionJavaEvidence? Java { get; init; }
    public List<ResolutionSourceEvidence> SourceEvidence { get; init; } = [];
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<ComponentDescriptor> Components { get; init; } = [];
    public List<LibraryHealthEntry> Libraries { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool InputsStable { get; init; } = true;
}
