using System;
using System.Windows.Interop;
using PCL.Core.UI;

namespace PCL;

public static partial class LiquidGlassCore
{
    private static HwndSource? _interactionSource;
    private static bool _windowMoving;

    private static void AttachInteraction(FormMain window)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source == _interactionSource) return;
        if (_interactionSource is { IsDisposed: false }) _interactionSource.RemoveHook(InteractionMessage);
        _interactionSource = source;
        source?.AddHook(InteractionMessage);
    }

    private static IntPtr InteractionMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        bool moving = LiquidGlassInteraction.Moving(_windowMoving, message);
        if (moving != _windowMoving)
        {
            _windowMoving = moving;
            Refresh();
        }
        return IntPtr.Zero; // Preserve normal window input and sizing.
    }

    private static void DetachInteraction()
    {
        if (_interactionSource is { IsDisposed: false }) _interactionSource.RemoveHook(InteractionMessage);
        _interactionSource = null;
        _windowMoving = false;
    }
}
