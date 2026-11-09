using System;

namespace PCL.Core.UI;

/// <summary>A monotonic minimum delay beginning only when the confirmation is actually shown.</summary>
public sealed class ConfirmationDelayPolicy
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _delay;
    private long? _shownAt;
    private bool _cancelled;

    public ConfirmationDelayPolicy(TimeSpan delay, TimeProvider? clock = null)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(delay));
        _delay = delay;
        _clock = clock ?? TimeProvider.System;
    }

    public void MarkShown()
    {
        if (!_cancelled && _shownAt is null) _shownAt = _clock.GetTimestamp();
    }

    public bool CanConfirm => !_cancelled && _shownAt is { } started && _clock.GetElapsedTime(started) >= _delay;

    public int RemainingSeconds => _cancelled ? 0 : _shownAt is not { } started
        ? (int)Math.Ceiling(_delay.TotalSeconds)
        : (int)Math.Ceiling(Math.Max(0, (_delay - _clock.GetElapsedTime(started)).TotalSeconds));

    public void Cancel() => _cancelled = true;
}
