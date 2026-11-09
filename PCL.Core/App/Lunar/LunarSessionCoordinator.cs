using System;

namespace PCL.Core.App.Lunar;

/// <summary>Serializes session ownership and callbacks, including an old Process.Exited racing a new launch.</summary>
public sealed class LunarSessionCoordinator
{
    private readonly object _gate = new();
    private long _generation;
    private bool _active;

    public long Begin(Action initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        lock (_gate)
        {
            var generation = checked(++_generation);
            _active = true;
            initialize();
            return generation;
        }
    }

    public bool RunIfCurrent(long generation, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (!_active || generation != _generation) return false;
            callback();
            return true;
        }
    }

    public bool Complete(long generation, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (!_active || generation != _generation) return false;
            _active = false;
            callback();
            return true;
        }
    }

    /// <summary>Checks the generation even after completion, for queued final notifications.</summary>
    public bool IsLatest(long generation)
    {
        lock (_gate) return generation == _generation;
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate) return _active && generation == _generation;
    }

    /// <summary>Normal launch entry refuses to replace a session whose completion is still being published.</summary>
    public long TryBegin(Action initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        lock (_gate)
        {
            if (_active) return 0;
            var generation = checked(++_generation);
            _active = true;
            initialize();
            return generation;
        }
    }
}
