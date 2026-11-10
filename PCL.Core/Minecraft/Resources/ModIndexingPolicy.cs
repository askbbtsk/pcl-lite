using System;
using System.Collections.Generic;
using System.IO;

namespace PCL.Core.Minecraft.Resources;

/// <summary>Only explicit requests and completed downloads may start expensive mod indexing.</summary>
public sealed class ModIndexingPolicy
{
    private readonly object _gate = new();
    private readonly HashSet<string> _downloadRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _promptedInstances = new(StringComparer.OrdinalIgnoreCase);

    public static int GetWorkerCount(int processors, long availableMemoryBytes)
    {
        // Each recursive worker can retain a large nested archive. Bound CPU and memory together.
        var memorySlots = availableMemoryBytes <= 0 ? 2 : availableMemoryBytes / (512L * 1024 * 1024);
        return (int)Math.Min(Math.Clamp(processors, 1, 8), Math.Clamp(memorySlots, 1, 8));
    }

    public void DownloadCompleted(string directory)
    {
        lock (_gate) _downloadRequests.Add(Key(directory));
    }

    public bool ConsumeDownloadRequest(string directory)
    {
        lock (_gate) return _downloadRequests.Remove(Key(directory));
    }

    public bool ShouldPrompt(string instancePath, bool hasPending, bool promptEnabled)
    {
        if (!hasPending || !promptEnabled) return false;
        lock (_gate) return _promptedInstances.Add(Key(instancePath));
    }

    public void AllowPromptAgain(string instancePath)
    {
        lock (_gate) _promptedInstances.Remove(Key(instancePath));
    }

    private static string Key(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
