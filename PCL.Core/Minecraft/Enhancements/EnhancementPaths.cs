using System;
using System.IO;

namespace PCL.Core.Minecraft.Enhancements;

internal static class EnhancementPaths
{
    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool IsWithin(string root, string path)
    {
        var boundary = Normalize(root);
        var candidate = Normalize(path);
        return string.Equals(boundary, candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Examine existing ancestors as well: a perfectly ordinary leaf can live below a junction.
    internal static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Enhancement operations cannot traverse a link: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    internal static bool IsExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
