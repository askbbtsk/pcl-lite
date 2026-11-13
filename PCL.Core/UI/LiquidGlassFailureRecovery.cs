using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PCL.Core.UI;

/// <summary>One bounded recovery attempt for optional glass; never suppress unrelated exceptions.</summary>
public sealed class LiquidGlassFailureRecovery
{
    private int _attempted;
    private WeakReference<Exception>? _recovered;

    public static bool IsGraphicsResourceFailure(Exception error)
    {
        for (var depth = 0; depth < 16 && error is not null; depth++, error = error.InnerException!)
            if (error is OutOfMemoryException ||
                error is COMException && error.HResult is unchecked((int)0x88980406) or unchecked((int)0x8007000E))
                return true;
        return false;
    }

    public bool TryRecover(Exception error, bool glassEnabled, Action disableAndRelease)
    {
        if (_recovered?.TryGetTarget(out var previous) == true && ReferenceEquals(error, previous))
            return true; // Both WPF application handlers may receive this same event.
        if (!glassEnabled || !IsGraphicsResourceFailure(error) ||
            Interlocked.CompareExchange(ref _attempted, 1, 0) != 0) return false;
        try
        {
            disableAndRelease();
            _recovered = new WeakReference<Exception>(error);
            return true;
        }
        catch (Exception) { return false; } // Release failed: keep the existing fatal path.
    }
}
