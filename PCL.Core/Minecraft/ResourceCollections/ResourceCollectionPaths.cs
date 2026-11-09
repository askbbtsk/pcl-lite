using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCL.Core.Minecraft.ResourceCollections;

internal static partial class ResourceCollectionPaths
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

    internal static unsafe string ExpandLongName(string full, out bool unresolved)
    {
        unresolved = false;
        if (!OperatingSystem.IsWindows()) { unresolved = full.Contains('~'); return full; }
        var buffer = new char[32768];
        fixed (char* input = full)
        fixed (char* output = buffer)
        {
            var length = GetLongPathName(input, output, (uint)buffer.Length);
            if (length == 0 || length >= buffer.Length) { unresolved = full.Contains('~'); return full; }
            return Normalize(new string(output, 0, (int)length));
        }
    }

    internal static unsafe string? PhysicalIdentity(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        FileInformation info;
        return GetFileInformationByHandle(handle.DangerousGetHandle(), &info) != 0
            ? $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}" : null;
    }

    internal static bool ExpectedFailure(Exception ex) => ex is IOException or InvalidDataException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true)]
    private static unsafe partial uint GetLongPathName(char* shortPath, char* longPath, uint bufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int GetFileInformationByHandle(nint handle, FileInformation* info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationTimeLow, CreationTimeHigh, AccessTimeLow, AccessTimeHigh,
            WriteTimeLow, WriteTimeHigh, VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks,
            FileIndexHigh, FileIndexLow;
    }
}
