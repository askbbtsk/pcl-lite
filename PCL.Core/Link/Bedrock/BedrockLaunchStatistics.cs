using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using PCL.Core.Logging;
using PCL.Core.Minecraft.Launch;

namespace PCL.Core.Link.Bedrock;

/// <summary>Shell activation success alone is not proof that a new Bedrock game started.</summary>
public static class BedrockLaunchStatistics
{
    private static readonly string[] ProcessNames = ["Minecraft.Windows", "Minecraft.Win10.DX11"];

    public static IReadOnlySet<int>? CaptureRunningProcesses()
    {
        try
        {
            var ids = new HashSet<int>();
            foreach (var name in ProcessNames)
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    foreach (var process in processes) ids.Add(process.Id);
                }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            return ids;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return null; }
    }

    /// <summary>Call after successful OS activation. Existing games and failed activations are not counted.</summary>
    public static void ObserveActivatedGame(IReadOnlySet<int>? previousProcesses)
    {
        if (previousProcesses is null) return; // Unknown pre-launch state cannot prove a new game.
        _ = Task.Run(() => ObserveAsync(previousProcesses));
    }

    /// <summary>Use the actual game PID returned by direct GDK startup, never a shell/explorer PID.</summary>
    public static void ObserveStartedGame(Process? process, IReadOnlySet<int>? previousProcesses)
    {
        if (process is null)
        {
            ObserveActivatedGame(previousProcesses);
            return;
        }
        try
        {
            if (process.HasExited) return;
            if (BedrockLaunchObservation.IsGameProcessName(process.ProcessName))
            {
                var processId = process.Id;
                _ = Task.Run(() => ObserveAsync(previousProcesses, processId));
                return;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            LogWrapper.Debug(error, "LaunchStatistics", "Unable to identify the directly started Bedrock process.");
        }
        ObserveActivatedGame(previousProcesses);
    }

    private static async Task ObserveAsync(IReadOnlySet<int>? previousProcesses, int? activatedProcessId = null)
    {
        try
        {
            for (var attempt = 0; attempt < 180; attempt++)
            {
                foreach (var name in ProcessNames)
                {
                    var processes = Process.GetProcessesByName(name);
                    try
                    {
                        foreach (var process in processes)
                        {
                            try
                            {
                                if (BedrockLaunchObservation.CanCount(previousProcesses, process.Id,
                                        process.ProcessName, process.HasExited, activatedProcessId))
                                {
                                    if (GameLaunchStatistics.RecordSuccessfulProcess(process)) return;
                                    // Concurrent activation requests can observe the same new game.
                                    // A deduplicated observation must not consume a later distinct launch.
                                }
                            }
                            catch (Exception error) when (error is InvalidOperationException
                                or System.ComponentModel.Win32Exception or NotSupportedException) { }
                        }
                    }
                    finally { foreach (var process in processes) process.Dispose(); }
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { LogWrapper.Debug(error, "LaunchStatistics", "Bedrock launch observation ended without a verified new game."); }
    }
}
