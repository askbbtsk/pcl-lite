using System;
using System.IO;
using System.Text;

namespace PCL.Core.App.Lunar;

/// <summary>
/// The official launcher compares the IPC installation identifier as an exact string.
/// Asset storage may be elsewhere; it must not create a separate IPC identity.
/// </summary>
public static class LunarInstallationIdentity
{
    public static string ResolveOfficialRoot(
        string defaultRoot, string? environmentOverride, string workingDirectory)
        => string.IsNullOrWhiteSpace(environmentOverride)
            ? Path.GetFullPath(defaultRoot)
            : Path.GetFullPath(environmentOverride, workingDirectory);

    public static string ReadOrCreate(string officialRoot, string assetRoot)
    {
        string shared = Path.Combine(officialRoot, "launcher-cache", "installation-id");
        string? existing = ReadExisting(shared, rejectInvalid: true);
        if (existing is not null) return existing;

        string assetIdentity = Path.Combine(assetRoot, "launcher-cache", "installation-id");
        string selected = ReadExisting(assetIdentity, rejectInvalid: false) ?? Guid.NewGuid().ToString();
        string folder = Path.GetDirectoryName(shared)!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, ".pcl-installation-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.UTF8.GetBytes(selected));
                file.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, shared); }
            catch (IOException) when (File.Exists(shared))
            {
                // Another launcher won creation; all participants must use its exact identifier.
                return ReadExisting(shared, rejectInvalid: true)
                    ?? throw new IOException("The concurrently created Lunar installation-id disappeared.");
            }
            return selected;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string? ReadExisting(string path, bool rejectInvalid)
    {
        FileStream stream;
        try
        {
            // Do not check File.Exists separately: another creator can publish a valid
            // file between that check and a later "invalid file" check.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        {
            if (stream.Length == 36)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                string raw = reader.ReadToEnd();
                // Preserve casing: the official IPC compares the raw string.
                if (Guid.TryParseExact(raw, "D", out _)) return raw;
            }
        }
        if (rejectInvalid)
            throw new InvalidDataException("The official Lunar installation-id file is invalid; it was left unchanged.");
        return null;
    }
}
