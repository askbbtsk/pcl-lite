using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCL.Core.Minecraft.ResourceCollections;

internal static class ResourceCollectionPaths
{
    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool HasLinkAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(current, parent, StringComparison.OrdinalIgnoreCase)) return false;
            current = parent;
        }
    }

    internal static void RejectLinks(string path)
    {
        if (HasLinkAncestor(path)) throw new InvalidDataException("A linked file or ancestor is not a copy source/destination.");
    }

    internal static bool IsSupported(string path)
    {
        var name = path;
        if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) name = name[..^9];
        else if (name.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return Path.GetExtension(name).ToLowerInvariant() is ".jar" or ".zip" or ".litemod";
    }

    internal static string ExpandLongName(string full, out bool unresolved)
    {
        unresolved = false;
        if (!OperatingSystem.IsWindows()) { unresolved = full.Contains('~'); return full; }
        var buffer = new StringBuilder(32768);
        var length = GetLongPathName(full, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) { unresolved = full.Contains('~'); return full; }
        return Normalize(buffer.ToString());
    }

    internal static string? PhysicalIdentity(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle, out var info)
            ? $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}" : null;
    }

    internal static bool ExpectedFailure(Exception ex) => ex is IOException or InvalidDataException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationTimeLow, CreationTimeHigh, AccessTimeLow, AccessTimeHigh,
            WriteTimeLow, WriteTimeHigh, VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks,
            FileIndexHigh, FileIndexLow;
    }
}
