using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

/// <summary>Runs work off the dispatcher while a cancellable native PCL dialog owns the foreground.</summary>
internal static class NativeWorkDialog
{
    internal static bool Run(string title, string caption, Func<CancellationToken, IProgress<int>, Task<bool>> execute,
        Func<bool> contextIsCurrent)
        => RunWithStatus(title, caption,
            (token, status) => execute(token, new StatusProgress<int>(value => status.Report(caption + Environment.NewLine + value))),
            contextIsCurrent);

    internal sealed class StatusProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    internal static bool RunWithStatus(string title, string caption,
        Func<CancellationToken, IProgress<string>, Task<bool>> execute, Func<bool> contextIsCurrent)
    {
        var window = ModMain.frmMain;
        if (window?.PanMsg is null || !window.Dispatcher.CheckAccess() || !window.IsLoaded) return false;
        var cancellation = new CancellationTokenSource();
        var converter = new ModMain.MyMsgBoxConverter
        {
            Type = ModMain.MyMsgBoxType.Text, Title = title, Text = caption,
            Button1 = Lang.Text("Common.Action.Cancel"), ForceWait = true, ModalLifetimeOwnedByCaller = true,
        };
        var succeeded = false;
        MyMsgText? Control() => window.PanMsg.Children.OfType<MyMsgText>().FirstOrDefault(control => control.UsesConverter(converter));
        void Finish()
        {
            if (converter.IsExited) return;
            var control = Control();
            if (control is not null) control.Btn2_Click(null!, null!);
            else { converter.IsExited = true; converter.WaitFrame.Continue = false; ModMain.WaitingMyMsgBox.Remove(converter); }
        }
        // A large index can report thousands of files per second. Posting each report
        // to the dispatcher at Normal priority starves mouse input (including Cancel).
        // Keep only the latest message; the UI timer consumes it at a bounded rate.
        var progress = new LatestStatusProgress();
        converter.Button1Action = () =>
        {
            if (cancellation.IsCancellationRequested) return;
            cancellation.Cancel();
            if (Control() is { } control)
            {
                control.Btn1.IsEnabled = false;
                control.SetWorkMessage(Lang.Text("Common.Status.Cancelling"));
            }
        };
        var work = Task.Run(() => execute(cancellation.Token, progress));
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        EventHandler tick = (_, _) =>
        {
            if (!contextIsCurrent() || window.Dispatcher.HasShutdownStarted)
            { cancellation.Cancel(); Finish(); return; }
            if (!cancellation.IsCancellationRequested && progress.TakeLatest() is { } message)
                Control()?.SetWorkMessage(message);
            if (!work.IsCompleted) return;
            try { succeeded = work.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ModBase.Log(error, "后台模组解析失败", ModBase.LogLevel.Developer); }
            Finish();
        };
        timer.Tick += tick;
        EventHandler close = (_, _) => { cancellation.Cancel(); converter.IsExited = true; converter.WaitFrame.Continue = false; };
        window.Closed += close;
        var modal = false;
        try
        {
            ModMain.WaitingMyMsgBox.Add(converter);
            ComponentDispatcher.PushModal(); modal = true;
            ModMain.MyMsgBoxTick(); timer.Start();
            if (!converter.IsExited) Dispatcher.PushFrame(converter.WaitFrame);
            return succeeded && contextIsCurrent();
        }
        finally
        {
            timer.Stop(); timer.Tick -= tick; window.Closed -= close;
            cancellation.Cancel();
            _ = work.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
            ModMain.WaitingMyMsgBox.Remove(converter);
            if (modal) ComponentDispatcher.PopModal();
        }
    }
}
