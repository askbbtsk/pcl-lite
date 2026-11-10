using System;
using System.Diagnostics;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Core.Minecraft.Launch;

/// <summary>All game editions preserve and extend the original SystemLaunchCount value.</summary>
public static class GameLaunchStatistics
{
    private static readonly GameLaunchCounter Counter = new(
        () => States.System.LaunchCount, value => States.System.LaunchCount = value);

    /// <summary>Call only after the caller's existing successful-game signal, never when merely queuing a launch.</summary>
    public static bool RecordSuccessfulProcess(Process? process)
    {
        if (process is null) return false;
        try
        {
            if (process.HasExited) return false;
            var identity = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                + process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Counter.RecordSuccess(identity);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // Statistics failures must never turn a successful game launch into an error.
            LogWrapper.Warn(error, "LaunchStatistics", "Unable to record a successful game launch; the existing count was retained.");
            return false;
        }
    }
}
