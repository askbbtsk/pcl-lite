using System;
using System.Windows;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.UI;

namespace PCL;

public static partial class LiquidGlassCore
{
    private static readonly LiquidGlassFailureRecovery FailureRecovery = new();
    private static bool _disabledAfterFailure;

    internal static bool TryRecoverGraphicsFailure(Exception error)
    {
        if (!LiquidGlassFailureRecovery.IsGraphicsResourceFailure(error)) return false;
        var window = ModMain.frmMain;
        if (window is null || !window.Dispatcher.CheckAccess() || window.Dispatcher.HasShutdownStarted)
            return false;
        return FailureRecovery.TryRecover(error, _applied || IsEnabled, () =>
        {
            // Turn off the feature first; disposing native capture must not restart
            // it via StateChanged while we release the failed material graph.
            _disabledAfterFailure = true;
            if (_desktopCapture is not null)
            {
                _desktopCapture.StateChanged -= OnCaptureStateChanged;
                _desktopCapture.Dispose();
                _desktopCapture = null;
            }
            foreach (var surface in GlassSurfaces(window))
            {
                surface.BackdropSource = null;
                surface.Visibility = Visibility.Collapsed;
            }
            // Persist only the glass enable switch. Every tuning value and all
            // account/theme settings remain available after the user restarts.
            Config.Preference.TahoeGlass.IsEnabled = false;
            Refresh();
            window.Dispatcher.BeginInvoke(() => LogWrapper.Warn("LiquidGlass",
                "液态玻璃因内存不足或渲染线程故障已自动关闭，桌面采样已停止。参数已保留；若窗口仍无响应，请重启启动器。"));
        });
    }
}
