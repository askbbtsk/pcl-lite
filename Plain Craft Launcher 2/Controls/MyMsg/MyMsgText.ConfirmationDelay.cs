using System;
using System.Windows;
using System.Windows.Threading;
using PCL.Core.UI;

namespace PCL;

public partial class MyMsgText
{
    private ConfirmationDelayPolicy? _confirmationDelay;
    private DispatcherTimer? _confirmationTimer;

    private void InitializeConfirmationDelay()
    {
        if (myConverter.MinimumConfirmDelaySeconds <= 0) return;
        _confirmationDelay = new ConfirmationDelayPolicy(TimeSpan.FromSeconds(myConverter.MinimumConfirmDelaySeconds));
        Btn1.IsEnabled = false;
        Btn1.Text = myConverter.Button1 + " (" + myConverter.MinimumConfirmDelaySeconds + ")";
        IsVisibleChanged += ConfirmationVisibilityChanged;
        Unloaded += ConfirmationUnloaded;
    }

    private void StartConfirmationDelay()
    {
        if (_confirmationDelay is null || !IsLoaded || !IsVisible || myConverter.IsExited) return;
        _confirmationDelay.MarkShown();
        _confirmationTimer ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(100) };
        _confirmationTimer.Tick -= ConfirmationTick;
        _confirmationTimer.Tick += ConfirmationTick;
        _confirmationTimer.Start();
        UpdateConfirmationDelay();
        if (Btn2.IsVisible) Btn2.Focus();
    }

    private void ConfirmationVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) StartConfirmationDelay();
    }

    private void ConfirmationTick(object? sender, EventArgs e) => UpdateConfirmationDelay();

    private void UpdateConfirmationDelay()
    {
        if (_confirmationDelay is null) return;
        var ready = _confirmationDelay.CanConfirm && !myConverter.IsExited;
        Btn1.IsEnabled = ready;
        Btn1.Text = ready ? myConverter.Button1 : myConverter.Button1 + " (" + _confirmationDelay.RemainingSeconds + ")";
        if (ready) _confirmationTimer?.Stop();
    }

    private bool CanAcceptPrimaryConfirmation()
        => myConverter.MinimumConfirmDelaySeconds <= 0 || _confirmationDelay?.CanConfirm == true;

    private void StopConfirmationDelay()
    {
        _confirmationDelay?.Cancel();
        if (_confirmationTimer is not null)
        {
            _confirmationTimer.Stop();
            _confirmationTimer.Tick -= ConfirmationTick;
        }
    }

    private void ConfirmationUnloaded(object sender, RoutedEventArgs e)
    {
        StopConfirmationDelay();
        IsVisibleChanged -= ConfirmationVisibilityChanged;
        Unloaded -= ConfirmationUnloaded;
        if (!myConverter.IsExited)
        { myConverter.IsExited = true; myConverter.Result = 2; myConverter.WaitFrame.Continue = false; }
    }
}
