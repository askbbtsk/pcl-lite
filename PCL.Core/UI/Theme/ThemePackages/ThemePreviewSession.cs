using System;

namespace PCL.Core.UI.Theme.Packages;

/// <summary>A monotonic preview deadline starts only after the recovery bar is presented.</summary>
public sealed class ThemePreviewSession(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long? _started;
    public bool? Result { get; private set; }
    public bool IsPresented => _started is not null;
    public void MarkPresented() => _started ??= _time.GetTimestamp();
    public int RemainingSeconds => _started is not long start ? 15
        : Math.Max(0, (int)Math.Ceiling((ThemeApi.PreviewDuration - _time.GetElapsedTime(start)).TotalSeconds));

    public bool TryFinish(bool keep)
    {
        if (Result is not null) return false;
        // A queued click arriving after the timeout must not accept an expired preview.
        Result = keep && IsPresented && RemainingSeconds > 0;
        return true;
    }
}
