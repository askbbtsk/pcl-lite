using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>Native PCL warning with a primary-action delay. Missing/shutting-down UI always means cancellation.</summary>
internal static class TimedConfirmation
{
    internal static int Show(string caption, string title, string button1, string? button2 = null, int seconds = 5)
    {
        if (seconds < 1 || seconds > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return 2;
        try
        {
            return app.Dispatcher.CheckAccess() ? ShowOnUi(caption, title, button1, button2, seconds)
                : app.Dispatcher.Invoke(() => ShowOnUi(caption, title, button1, button2, seconds));
        }
        catch (System.Threading.Tasks.TaskCanceledException) { return 2; }
        catch (InvalidOperationException) when (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
        { return 2; }
    }

    private static int ShowOnUi(string caption, string title, string button1, string? button2, int seconds)
    {
        var window = ModMain.frmMain;
        if (window?.PanMsg is null || !window.IsLoaded || window.Dispatcher.HasShutdownStarted) return 2;
        var converter = new ModMain.MyMsgBoxConverter
        {
            Type = ModMain.MyMsgBoxType.Text, Title = title, Text = caption, Button1 = button1,
            Button2 = string.IsNullOrEmpty(button2) ? Lang.Text("Common.Action.Cancel") : button2,
            IsWarn = true, HighLight = true, ForceWait = true, Result = 2,
            MinimumConfirmDelaySeconds = seconds, ModalLifetimeOwnedByCaller = true,
        };
        void Cancel(object? sender, EventArgs e)
        {
            converter.IsExited = true; converter.Result = 2; converter.WaitFrame.Continue = false;
            ModMain.WaitingMyMsgBox.Remove(converter);
        }
        window.Closed += Cancel;
        var modal = false;
        try
        {
            ModMain.WaitingMyMsgBox.Add(converter);
            window.DragStop();
            ComponentDispatcher.PushModal(); modal = true;
            ModMain.MyMsgBoxTick();
            if (!converter.IsExited) Dispatcher.PushFrame(converter.WaitFrame);
            return converter.IsExited && converter.Result is int result && result == 1 ? 1 : 2;
        }
        finally
        {
            window.Closed -= Cancel;
            ModMain.WaitingMyMsgBox.Remove(converter);
            if (modal) ComponentDispatcher.PopModal();
        }
    }
}
