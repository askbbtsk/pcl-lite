using System;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Core.Minecraft.PersonalOptions;

/// <summary>Coordinates cooperating launcher operations by the actual options directory, including path aliases.</summary>
public sealed class PersonalOptionsFileCoordinator : IDisposable
{
    private readonly FileStream _lease;

    private PersonalOptionsFileCoordinator(FileStream lease) => _lease = lease;

    public static PersonalOptionsFileCoordinator? TryAcquire(string optionsPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(optionsPath))!;
        EnsurePlainPath(directory);
        var state = Path.Combine(directory, "PCL", "Enhancements");
        EnsurePlainPath(state);
        Directory.CreateDirectory(state);
        EnsurePlainPath(state);
        var leasePath = Path.Combine(state, "options-session.lock");
        EnsurePlainPath(leasePath);
        PersonalOptionsFileCoordinator? coordinator = null;
        try
        {
            // The handle is retained until game exit; another cooperating launcher must skip writes.
            var stream = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            coordinator = new PersonalOptionsFileCoordinator(stream);
            if (coordinator.HasRunningOwner()) { coordinator.Dispose(); return null; }
            stream.SetLength(0);
            stream.Flush(flushToDisk: true);
            return coordinator;
        }
        catch (IOException) { coordinator?.Dispose(); return null; }
        catch { coordinator?.Dispose(); throw; }
    }

    public void Dispose() => _lease.Dispose();

    internal void MarkGameStarted(int processId, long startTimeUtcTicks)
    {
        if (processId <= 0 || startTimeUtcTicks <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        var bytes = Encoding.ASCII.GetBytes(processId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + startTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _lease.Position = 0;
        _lease.SetLength(0);
        _lease.Write(bytes);
        _lease.Flush(flushToDisk: true);
    }

    internal void ClearStoppedOwner()
    {
        _lease.SetLength(0);
        _lease.Flush(flushToDisk: true);
    }

    private bool HasRunningOwner()
    {
        if (_lease.Length == 0) return false;
        if (_lease.Length > 128) return true;
        var bytes = new byte[(int)_lease.Length];
        _lease.Position = 0;
        _lease.ReadExactly(bytes);
        var parts = Encoding.ASCII.GetString(bytes).Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || pid <= 0
            || !long.TryParse(parts[1], out var startTicks) || startTicks <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    internal static IDisposable? TryAcquireProfile(string path)
    {
        EnsurePlainPath(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        EnsurePlainPath(directory);
        var lockPath = path + ".pcl-options.lock";
        EnsurePlainPath(lockPath);
        try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    internal static void EnsurePlainPath(string path)
    {
        var full = Path.GetFullPath(path);
        var ancestors = new System.Collections.Generic.Stack<string>();
        for (var current = full; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
        while (ancestors.Count > 0)
        {
            var current = ancestors.Pop();
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Personal options cannot follow links: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal sealed record Snapshot(bool Exists, byte[] Bytes, string Hash)
    {
        internal string Text
        {
            get
            {
                var bom = Bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
                var skip = bom ? Encoding.UTF8.Preamble.Length : 0;
                var text = new UTF8Encoding(false, true).GetString(Bytes, skip, Bytes.Length - skip);
                return bom ? "\uFEFF" + text : text;
            }
        }
    }

    internal static Snapshot Read(string path)
    {
        EnsurePlainPath(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Personal options file is too large.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return new Snapshot(true, bytes, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (FileNotFoundException) { return new Snapshot(false, [], "missing"); }
        catch (DirectoryNotFoundException) { return new Snapshot(false, [], "missing"); }
    }

    internal static byte[] Encode(string text)
    {
        var bom = text.StartsWith('\uFEFF');
        var content = new UTF8Encoding(false, true).GetBytes(bom ? text[1..] : text);
        if (!bom) return content;
        var result = new byte[Encoding.UTF8.Preamble.Length + content.Length];
        Encoding.UTF8.Preamble.CopyTo(result);
        content.CopyTo(result, Encoding.UTF8.Preamble.Length);
        return result;
    }

    internal static bool TryWrite(string path, byte[] bytes, Snapshot expected)
    {
        EnsurePlainPath(path);
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        EnsurePlainPath(parent);
        var temp = Path.Combine(parent, $".pcl-options-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            var current = Read(path);
            if (current.Exists != expected.Exists || current.Hash != expected.Hash) return false;
            EnsurePlainPath(path);
            // Atomic publication; no-overwrite publication when a previously missing file appears concurrently.
            File.Move(temp, path, overwrite: expected.Exists);
            return true;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
