using System;
using System.IO;
using System.Linq;

namespace PCL.Core.UI.Theme.Packages;

public static class ThemeFilePaths
{
    public static string Normalize(string relativePath)
    {
        string value = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || value.StartsWith('/')
            || value.Contains(':') || value.Contains('%') || value.Contains('?') || value.Contains('#')
            || value.Split('/').Any(part => part.Length == 0 || part is "." or ".."
                || part.EndsWith('.') || part.EndsWith(' ') || part.Any(char.IsControl)
                || part.IndexOfAny(['<', '>', '|', '"', '*', '\0']) >= 0
                || System.Text.RegularExpressions.Regex.IsMatch(part,
                    @"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new InvalidDataException($"Invalid theme-relative path: {relativePath}");
        return value;
    }

    public static string Resolve(string root, string relativePath)
    {
        string absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(absoluteRoot, Normalize(relativePath)));
        if (!target.StartsWith(absoluteRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Theme paths must stay inside the theme directory.");
        for (string? path = target; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Theme links are not supported: {path}");
            if (path.Equals(absoluteRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
        return target;
    }

    public static string Dependency(string parent, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Uri.TryCreate(reference, UriKind.Absolute, out _))
            throw new InvalidDataException("Only relative local theme resources are supported.");
        // Do not accept URI decoding tricks, root paths, traversal or assembly pack URIs.
        string normalized = Normalize(reference);
        string? directory = Path.GetDirectoryName(parent.Replace('/', Path.DirectorySeparatorChar));
        return Normalize(string.IsNullOrEmpty(directory) ? normalized : directory.Replace('\\', '/') + "/" + normalized);
    }
}
