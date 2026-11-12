using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCL.Plugin.SDK;

namespace PCL.Core.UI.Theme.Packages;

/// <summary>The file/visual contract version, independent of launcher release versions.</summary>
public static class ThemeApi
{
    public const int Version = 1;
    public const string ResourcePrefix = "Pcl.Theme.";
    public static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(15);
    public const long MaximumFileBytes = 8 * 1024 * 1024;
    public const long MaximumPackageBytes = 128 * 1024 * 1024;
    public const int MaximumFiles = 256;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public sealed record ThemeManifest
{
    public int SchemaVersion { get; init; } = 1;
    public int ApiVersion { get; init; } = ThemeApi.Version;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Author { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";
    public string? LauncherVersion { get; init; }
    public string Entry { get; init; } = "Theme.xaml";
    public string? Light { get; init; }
    public string? Dark { get; init; }
    public bool SupportsGlass { get; init; }
    public string? Glass { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1 || ApiVersion != ThemeApi.Version)
            throw new InvalidDataException($"Unsupported theme schema/API: {SchemaVersion}/{ApiVersion}.");
        if (Id is null || !Regex.IsMatch(Id, @"^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)
            || Id.EndsWith('.') || Id is "con" or "prn" or "aux" or "nul"
            || Regex.IsMatch(Id, @"^(com|lpt)[1-9](\.|$)", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Theme ID must be a portable lowercase directory name.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 160 || Author is null || Author.Length > 160
            || Description is null || Description.Length > 4096 || string.IsNullOrWhiteSpace(Entry)
            || !PluginVersionRange.IsValidVersion(Version))
            throw new InvalidDataException("Invalid theme name, author, description or version.");
        if (LauncherVersion is not null && !PluginVersionRange.IsValid(LauncherVersion))
            throw new InvalidDataException("Invalid launcherVersion range.");
        if (!SupportsGlass && Glass is not null)
            throw new InvalidDataException("A glass variant requires supportsGlass=true.");
        foreach (string? path in new[] { Entry, Light, Dark, Glass })
            if (path is not null && !path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Theme entries must be local XAML files.");
    }

    public bool IsCompatible(string launcherVersion) =>
        LauncherVersion is null || PluginVersionRange.Satisfies(launcherVersion, LauncherVersion);
}

/// <summary>An immutable snapshot of precisely the files validated for installation/loading.</summary>
public sealed record ThemePackage(ThemeManifest Manifest, IReadOnlyDictionary<string, byte[]> Files,
    string ContentHash, string SourcePath)
{
    public string ForceConsentKey(string launcherVersion) =>
        $"{launcherVersion}\n{Manifest.Id}\n{ContentHash}";
}

public sealed record ThemeCatalogItem(string Directory, ThemeManifest? Manifest, string? Error);

public sealed class ThemePackageException(string message, string? file = null, int line = 0, int column = 0,
    Exception? inner = null) : Exception(
        file is null ? message : $"{file}({line},{column}): {message}", inner);
