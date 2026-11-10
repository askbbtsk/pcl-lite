using System;
using System.Collections.Generic;

namespace PCL.Core.Minecraft.Launch;

/// <summary>Serializes increments of the existing cumulative count and deduplicates success notifications.</summary>
public sealed class GameLaunchCounter(Func<int> read, Action<int> write)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _recorded = new(StringComparer.Ordinal);

    public bool RecordSuccess(string launchIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchIdentity);
        lock (_gate)
        {
            if (_recorded.Contains(launchIdentity)) return false;
            var previous = read();
            // The persisted format is an Int32. Never wrap a user's existing total to a negative value.
            if (previous < int.MaxValue) write(previous + 1);
            _recorded.Add(launchIdentity);
            return true;
        }
    }
}
