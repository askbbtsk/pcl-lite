using System;
using System.Threading;

namespace PCL.Core.App.Lunar;

/// <summary>One-time explicit acknowledgement; cancellation never grants consent.</summary>
public sealed class LunarCredentialConsent
{
    public const int ConfirmationSeconds = 3;
    private int _promptOpen;

    public bool EnsureAccepted(Func<bool> isAcknowledged, Func<int, bool> confirm, Action remember)
    {
        // Prevent reentrant navigation while a nested modal dispatcher is running.
        if (Interlocked.CompareExchange(ref _promptOpen, 1, 0) != 0) return false;
        try
        {
            if (isAcknowledged()) return true;
            if (!confirm(ConfirmationSeconds)) return false;
            remember();
            return true;
        }
        finally { Volatile.Write(ref _promptOpen, 0); }
    }
}
