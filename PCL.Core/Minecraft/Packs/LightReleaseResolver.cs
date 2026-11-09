using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Packs;

public sealed record LightReleaseAsset(string Tag, string Name, string Url, string? Sha256, string? ChecksumsUrl);

public static class LightReleaseResolver
{
    public const string Repository = "bedkillerspacex-boop/Pcl-Light";
    public const string BundledFileName = "PCL Light.exe";

    public static string[] DownloadCandidates(string officialUrl, IEnumerable<string> proxies)
        => new[] { officialUrl }.Concat(proxies.Select(proxy => proxy.TrimEnd('/') + "/" + officialUrl))
            .Distinct(StringComparer.Ordinal).ToArray();

    public static LightReleaseAsset Resolve(string releaseJson, string architecture)
    {
        if (architecture is not ("x64" or "ARM64")) throw new InvalidDataException("Unsupported Light architecture.");
        var release = JsonNode.Parse(releaseJson) ?? throw new InvalidDataException("Empty Light release.");
        if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true)
            throw new InvalidDataException("PCL Light requires a stable release.");
        var tag = release["tag_name"]?.GetValue<string>() ?? throw new InvalidDataException("Release tag is missing.");
        var assets = release["assets"]?.AsArray() ?? throw new InvalidDataException("Release assets are missing.");
        var matches = assets.Where(asset => asset?["name"]?.GetValue<string>() is { } name
            && name.StartsWith("PCL-Light_", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith("_" + architecture + ".exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("No unique Light executable matches the selected architecture.");
        var match = matches[0]!;
        string OfficialUrl(JsonNode asset)
        {
            var url = asset["browser_download_url"]?.GetValue<string>() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
                || !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected Light release download origin.");
            return url;
        }
        var checksumAsset = assets.FirstOrDefault(asset => asset?["name"]?.GetValue<string>() == "SHA256SUMS.txt");
        var digest = match["digest"]?.GetValue<string>();
        var hash = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
        if (hash is not null && !IsHash(hash)) throw new InvalidDataException("Invalid release SHA-256 digest.");
        return new LightReleaseAsset(tag, match["name"]!.GetValue<string>(), OfficialUrl(match), hash,
            checksumAsset is null ? null : OfficialUrl(checksumAsset));
    }

    public static string ReadChecksum(string text, string fileName)
    {
        var matches = text.Split('\n').Select(line => Regex.Match(line.Trim(), @"\A([0-9a-fA-F]{64})\s+\*?(.+)\z"))
            .Where(match => match.Success && match.Groups[2].Value == fileName).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("A unique Light checksum was not found.");
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
