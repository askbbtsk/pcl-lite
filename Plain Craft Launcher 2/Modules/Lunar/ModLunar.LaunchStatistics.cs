using System;
using System.Diagnostics;
using System.Threading.Tasks;
using PCL.Core.Minecraft.Launch;

namespace PCL;

public static partial class ModLunar
{
    private static void ObserveSuccessfulGame(Process process, long session)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Genesis can emit a valid latest.log before creating the real game window.
                // Keep success observation separate from the network-stall watchdog, which may
                // finish as soon as it sees that log.
                for (var round = 0; round < 1200; round++)
                {
                    if (!GameSessions.IsCurrent(session) || !ReferenceEquals(_process, process)
                        || process.HasExited || _stopRequested) return;
                    if (HasGameWindow(process.Id))
                    {
                        var successful = false;
                        GameSessions.RunIfCurrent(session, () =>
                        {
                            if (!_stopRequested && ReferenceEquals(_process, process))
                                successful = true;
                        });
                        // Config writes/log subscribers may use the dispatcher: never execute them
                        // under the Lunar session gate.
                        if (successful) GameLaunchStatistics.RecordSuccessfulProcess(process);
                        return;
                    }
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { ModBase.Log(error, "[Lunar] 游戏启动计数观察已停止", ModBase.LogLevel.Developer); }
        });
    }
}
