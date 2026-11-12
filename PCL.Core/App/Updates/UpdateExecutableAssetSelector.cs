using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCL.Core.App.Updates;

/// <summary>Selects the signed executable understood by the updater, never a ZIP or a different architecture.</summary>
public static class UpdateExecutableAssetSelector
{
    public static string? Pick(IEnumerable<string> urls, bool arm64)
    {
        var candidates = urls.Select(url =>
                Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    ? (Url: url, Name: Path.GetFileName(uri.AbsolutePath))
                    : (Url: url, Name: ""))
            .Where(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var architecture = arm64 ? "arm64" : "x64";
        var matched = candidates.FirstOrDefault(x => x.Name.Contains(architecture, StringComparison.OrdinalIgnoreCase));
        if (matched.Url is not null) return matched.Url;
        return candidates.FirstOrDefault(x =>
            !x.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) &&
            !x.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
            !x.Name.Contains("x86", StringComparison.OrdinalIgnoreCase)).Url;
    }
}
