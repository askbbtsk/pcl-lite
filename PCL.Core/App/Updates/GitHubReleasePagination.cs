using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PCL.Core.App.Updates;

public static class GitHubReleasePagination
{
    public static string? Next(IEnumerable<string> links)
    {
        foreach (var header in links)
        foreach (Match link in Regex.Matches(header, @"<([^>]+)>\s*;\s*rel=""([^""]+)"""))
        {
            if (!Array.Exists(link.Groups[2].Value.Split(' '), rel => rel == "next")) continue;
            if (!Uri.TryCreate(link.Groups[1].Value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.github.com" ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !uri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected GitHub Releases pagination URL");
            return uri.AbsoluteUri;
        }
        return null;
    }
}
