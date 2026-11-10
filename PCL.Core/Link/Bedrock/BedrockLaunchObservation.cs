using System;
using System.Collections.Generic;

namespace PCL.Core.Link.Bedrock;

/// <summary>Evidence required after an accepted activation; UWP windows may belong to ApplicationFrameHost.</summary>
internal static class BedrockLaunchObservation
{
    internal static bool IsGameProcessName(string processName) =>
        string.Equals(processName, "Minecraft.Windows", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processName, "Minecraft.Win10.DX11", StringComparison.OrdinalIgnoreCase);

    internal static bool CanCount(IReadOnlySet<int>? previousProcesses, int processId,
        string processName, bool hasExited, int? activatedProcessId = null)
    {
        if (processId <= 0 || hasExited || !IsGameProcessName(processName)) return false;
        if (activatedProcessId.HasValue && activatedProcessId.Value != processId) return false;
        // A known direct-spawn PID is evidence even when taking the pre-launch snapshot failed.
        // Shell activation without either a snapshot or an actual game PID supplies no evidence.
        if (previousProcesses is null) return activatedProcessId == processId;
        return !previousProcesses.Contains(processId);
    }
}
