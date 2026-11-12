using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme.Packages;

namespace PCL;

/// <summary>Outside PanForm's theme scope, so a package cannot hide its recovery controls.</summary>
internal sealed class CustomThemePreviewBar : IDisposable
{
    private readonly Grid _parent;
    private readonly Border _bar;
    private readonly TextBlock _caption;
    private readonly DispatcherTimer _timer;
    private readonly ThemePreviewSession _session = new();
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _name;
    private CancellationTokenRegistration _registration;
    private bool _waitingForFrame;

    public CustomThemePreviewBar(FormMain window, string name)
    {
        _name = name;
        _parent = (Grid)VisualTreeHelper.GetParent(window.PanBack);
        var content = new StackPanel();
        _caption = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White,
            FontSize = 14, Margin = new Thickness(0, 0, 0, 8) };
        var actions = new WrapPanel();
        var keep = new MyButton { Text = Lang.Text("Theme.Pack.Keep"), Height = 38, MinWidth = 110,
            Margin = new Thickness(0, 0, 10, 0) };
        var revert = new MyButton { Text = Lang.Text("Theme.Pack.Revert"), Height = 38, MinWidth = 110 };
        keep.Click += (_, _) => Finish(true);
        revert.Click += (_, _) => Finish(false);
        actions.Children.Add(keep);
        actions.Children.Add(revert);
        content.Children.Add(_caption);
        content.Children.Add(actions);
        _bar = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 39, 47)),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16), Margin = new Thickness(28), MaxWidth = 650,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Child = content };
        // Pin resources to the built-in application layer, independent of theme/local glass resources.
        foreach (string key in new[] { "ColorBrush1", "ColorBrush2", "ColorBrush3", "ColorBrush4",
                     "ColorBrushHalfWhite", "ColorBrushSemiTransparent", "ColorBrushGray4" })
            if (System.Windows.Application.Current.TryFindResource(key) is { } value) _bar.Resources[key] = value;
        _bar.Resources["ColorBrush1"] = Brushes.White;
        ThemeControlRegistry.SetProtected(_bar, true);
        Panel.SetZIndex(_bar, int.MaxValue);
        _timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += Tick;
    }

    public async Task<bool> ShowAsync(CancellationToken token)
    {
        _parent.Children.Add(_bar);
        _waitingForFrame = true;
        CompositionTarget.Rendering += FirstFrame;
        Tick(null, EventArgs.Empty);
        _registration = token.Register(() => _bar.Dispatcher.BeginInvoke(() => Finish(false)));
        return await _completion.Task;
    }

    private void FirstFrame(object? sender, EventArgs args)
    {
        if (!_bar.IsVisible || _bar.ActualWidth <= 0) return;
        CompositionTarget.Rendering -= FirstFrame;
        _waitingForFrame = false;
        _session.MarkPresented();
        _timer.Start();
    }

    private void Tick(object? sender, EventArgs args)
    {
        int remaining = _session.RemainingSeconds;
        _caption.Text = Lang.Text("Theme.Pack.PreviewCountdown", _name, remaining);
        if (remaining == 0) Finish(false);
    }
    public void Finish(bool keep)
    {
        if (_session.TryFinish(keep)) _completion.TrySetResult(_session.Result == true);
    }
    public void Dispose()
    {
        _timer.Stop();
        if (_waitingForFrame) CompositionTarget.Rendering -= FirstFrame;
        _timer.Tick -= Tick;
        _registration.Dispose();
        _parent.Children.Remove(_bar);
        Finish(false);
    }
}
