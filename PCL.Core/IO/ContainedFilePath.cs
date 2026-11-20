using System;
using System.IO;

namespace PCL.Core.IO;

/// <summary>Resolves a file below a caller-selected directory without following redirected entries.</summary>
public static class ContainedFilePath
{
    public static string Resolve(string directory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Expected a relative file path.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        RejectReparsePoint(root);
        var path = root;
        foreach (var part in relativePath.Split('/', '\\'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Invalid file path component.");
            path = Path.Combine(path, part);
            RejectReparsePoint(path);
        }

        var fullPath = Path.GetFullPath(path);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The resolved file path escapes its destination directory.");
        return fullPath;
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A file path points to a redirected entry.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
