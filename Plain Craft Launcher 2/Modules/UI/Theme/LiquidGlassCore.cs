using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PCL.Core.App;

namespace PCL;

/// <summary>
/// Native optical material. WGC provides desktop pixels to an independent sibling
/// source; the existing shader refracts that source without sampling foreground UI.
/// </summary>
public static class LiquidGlassCore
{
    private static FormMain? _window;
    private static bool _applied;
    private static bool _refreshing;
    private static Visibility _gradientVisibility, _imageVisibility;
    private static double _shadowOpacity, _radiusX, _radiusY;
    private static string? _lastDiagnostics;
    private static Geometry? _originalSidebarClip;
    private static TahoeDesktopCapture? _desktopCapture;
    private static bool _wallpaperSuppressed;
    private static readonly Dictionary<MyIconButton, MyIconButton.Themes> OriginalIconThemes = new();
    private static readonly Dictionary<string, object?> OriginalThemeResources = new();

    public static TahoeAppearance Appearance { get; } = new();
    public static bool IsEnabled => PCL.Core.UI.LiquidGlassPerformance.IsEnabled(
        Config.Preference.TahoeGlass.IsEnabled, Config.Preference.TahoeGlass.PerformanceConsent);
    public static bool IsDesktopBackdropActive => IsEnabled && _desktopCapture?.IsActive == true;
    public static bool IsDesktopBackdropRequested => PCL.Core.UI.LiquidGlassPerformance.ShouldCaptureDesktop(
        Config.Preference.TahoeGlass.IsEnabled, Config.Preference.TahoeGlass.PerformanceConsent,
        Config.Preference.TahoeGlass.UseDesktopBackdrop, Config.Preference.TahoeGlass.DesktopCaptureConsent);
    public static FrameworkElement? BackdropSource => IsEnabled &&
        (!IsDesktopBackdropRequested || _desktopCapture?.HasFrame == true) ? _window?.TahoeBackdrop : null;
    public static event EventHandler? Changed;
    public static PCL.Core.UI.LiquidGlassProfile Profile =>
        PCL.Core.UI.LiquidGlassPerformance.GetProfile(Config.Preference.TahoeGlass.PerformancePreset);

    public static void Refresh()
    {
        var window = ModMain.frmMain;
        if (window?.TahoeBackdrop is null || _refreshing) return;
        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.BeginInvoke(Refresh);
            return;
        }
        _refreshing = true;
        try
        {
            if (_window != window)
            {
                _window = window;
                _desktopCapture = new TahoeDesktopCapture(window, window.PanForm, window.TahoeDesktopImage,
                    window.TransformRotate, window.TransformPos, new TahoeCapturePlatform(LogCapture), LogCapture);
                _desktopCapture.StateChanged += OnCaptureStateChanged;
                window.Closed += OnClosed;
                window.PanMainLeft.SizeChanged += OnSidebarSizeChanged;
                window.PanTitleSelectViewport.SizeChanged += OnTitleSizeChanged;
                window.PanTitle.SizeChanged += OnTitleSizeChanged;
                SystemParameters.StaticPropertyChanged += OnSystemSettingsChanged;
            }

            // Suppress the old effect while enabled and restore its configured value on exit.
            System.Windows.Application.Current.Resources["BlurRadius"] = IsEnabled || !Config.Preference.Blur.IsEnabled
                ? 0d : (double)Config.Preference.Blur.Radius;
            if (IsEnabled)
            {
                if (!_applied) CaptureOriginal(window);
                Apply(window);
                _applied = true;
            }
            else if (_applied)
            {
                Restore(window);
                _applied = false;
            }
            Appearance.IsEnabled = IsEnabled;
            if (IsEnabled)
            {
                foreach (var surface in GlassSurfaces(window))
                {
                    surface.BlurRadius = Profile.BlurRadius;
                    surface.Dispersion = Profile.Dispersion;
                    surface.HighlightStrength = Profile.Highlight;
                }
            }
            Appearance.IsDesktopRequested = IsDesktopBackdropRequested;
            string captureState = (_desktopCapture?.State ?? TahoeCaptureState.Stopped).ToString();
            if (_desktopCapture?.State == TahoeCaptureState.Live && !window.TahoeWindowGlass.IsRefractionActive)
                captureState = "Compatibility";
            Appearance.DesktopStatus = PCL.Core.App.Localization.Lang.Text("Setup.Ui.Tahoe.State." + captureState);
            Changed?.Invoke(null, EventArgs.Empty);
            if (IsEnabled)
            {
                foreach (var surface in GlassSurfaces(window))
                {
                    surface.BlurRadius = Profile.BlurRadius;
                    surface.Dispersion = Profile.Dispersion;
                    surface.HighlightStrength = Profile.Highlight;
                }
            }
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
            {
                if (_window != window) return;
                var state = $"Enabled={IsEnabled}; Toolbar={window.TahoeToolbar.RenderStatus}; " +
                            $"Sidebar={window.TahoeSidebar.RenderStatus}; Desktop={_desktopCapture?.Status}; Tier={RenderCapability.Tier >> 16}";
                if (state == _lastDiagnostics) return;
                _lastDiagnostics = state;
                ModBase.Log("[Tahoe] " + state, ModBase.LogLevel.Developer);
            });
        }
        finally { _refreshing = false; }
    }

    private static void LogCapture(string message, Exception? error)
    {
        if (error == null) ModBase.Log(message, ModBase.LogLevel.Developer);
        else ModBase.Log(error, message);
    }

    private static IEnumerable<TahoeGlassSurface> GlassSurfaces(DependencyObject parent)
    {
        if (parent is TahoeGlassSurface surface) yield return surface;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in GlassSurfaces(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static void OnCaptureStateChanged(object? sender, EventArgs args) => Refresh();

    private static void CaptureOriginal(FormMain window)
    {
        _gradientVisibility = window.ImgTitleGradient.Visibility;
        _imageVisibility = window.ImgTitle.Visibility;
        _shadowOpacity = window.RectLeftShadow.Opacity;
        _radiusX = window.RectForm.RadiusX;
        _radiusY = window.RectForm.RadiusY;
        _originalSidebarClip = window.PanMainLeft.Clip;
        OriginalIconThemes.Clear();
        foreach (var icon in TitleIcons(window)) OriginalIconThemes[icon] = icon.Theme;
    }

    private static void Apply(FormMain window)
    {
        bool dark = ThemeManager.IsDarkMode;
        var foreground = new SolidColorBrush(SystemParameters.HighContrast ? SystemColors.WindowTextColor
            : dark ? Color.FromRgb(244, 246, 251) : Color.FromRgb(29, 35, 44));
        foreground.Freeze();
        _desktopCapture!.Apply(IsDesktopBackdropRequested);
        window.TahoeBackdrop.Visibility = Visibility.Visible;
        window.TahoeWallpaperHost.Visibility = IsDesktopBackdropRequested ? Visibility.Collapsed : Visibility.Visible;
        window.TahoeDesktopImage.Visibility = IsDesktopBackdropRequested ? Visibility.Visible : Visibility.Collapsed;
        if (_wallpaperSuppressed != IsDesktopBackdropRequested)
        {
            _wallpaperSuppressed = IsDesktopBackdropRequested;
            if (_wallpaperSuppressed && window.VideoBack.Source != null) window.VideoBack.Pause();
            else if (!_wallpaperSuppressed) ModVideoBack.VideoPlay();
        }
        if (IsDesktopBackdropRequested)
            window.PanForm.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush
                : new SolidColorBrush(dark ? Color.FromRgb(28, 29, 32) : Color.FromRgb(243, 243, 245));
        else
            ThemeManager.RefreshBackground();
        SetBrush(window, "TahoeText", dark ? "#F2F3F5" : "#252830");
        SetBrush(window, "TahoeSecondaryText", dark ? "#B9BEC8" : "#5D626D");
        SetBrush(window, "TahoeSidebarSection", dark ? "#16FFFFFF" : "#52FFFFFF");
        SetBrush(window, "TahoeFieldSurface", dark ? "#343840" : "#FFFFFF");
        SetBrush(window, "TahoeFieldBorder", dark ? "#60FFFFFF" : "#30000000");
        SetBrush(window, "TahoeAccent", dark ? "#0A84FF" : "#007AFF");
        SetBrush(window, "TahoeActionText", dark ? "#CDEBFF" : "#003B7A");
        SetBrush(window, "ColorBrush1", dark ? "#F2F3F5" : "#202B36");
        SetBrush(window, "ColorBrush2", dark ? "#60B9FF" : "#0065CE");
        SetBrush(window, "ColorBrush3", dark ? "#80C8FF" : "#007AFF");
        SetBrush(window, "ColorBrush4", dark ? "#9CD4FF" : "#368FE8");
        SetBrush(window, "ColorBrush6", dark ? "#394F65" : "#D6E8F6");
        SetBrush(window, "ColorBrush7", dark ? "#2B4257" : "#E2EFF7");
        SetBrush(window, "ColorBrush8", dark ? "#243B4F" : "#EBF3F8");
        SetBrush(window, "ColorBrushGray1", dark ? "#E1E8EF" : "#30404F");
        SetBrush(window, "ColorBrushGray2", dark ? "#CBD5DF" : "#435563");
        SetBrush(window, "ColorBrushGray3", dark ? "#B7C6D3" : "#506675");
        if (SystemParameters.HighContrast)
        {
            foreach (string key in new[] { "TahoeText", "TahoeSecondaryText", "TahoeActionText",
                         "ColorBrush1", "ColorBrushGray1", "ColorBrushGray2", "ColorBrushGray3" })
                SetResource(window, key, SystemColors.WindowTextBrush);
            foreach (string key in new[] { "TahoeAccent", "ColorBrush2", "ColorBrush3", "ColorBrush4" })
                SetResource(window, key, SystemColors.HotTrackBrush);
            foreach (string key in new[] { "TahoeSidebarSection", "TahoeFieldSurface",
                         "ColorBrush6", "ColorBrush7", "ColorBrush8" })
                SetResource(window, key, SystemColors.WindowBrush);
            SetResource(window, "TahoeFieldBorder", SystemColors.WindowTextBrush);
        }
        if (string.IsNullOrWhiteSpace(Config.Preference.Font))
            SetResource(window, "LaunchFontFamily", new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"));
        else if (OriginalThemeResources.TryGetValue("LaunchFontFamily", out object? originalFont))
        {
            if (originalFont is null) window.Resources.Remove("LaunchFontFamily");
            else window.Resources["LaunchFontFamily"] = originalFont;
        }
        window.PanTitle.BackgroundBrush = Brushes.Transparent;
        window.RectLeftBackground.BackgroundBrush = Brushes.Transparent;
        window.ImgTitleGradient.Visibility = Visibility.Hidden;
        window.ImgTitle.Visibility = Visibility.Hidden;
        window.RectLeftShadow.Opacity = 0;
        window.RectForm.RadiusX = window.RectForm.RadiusY = 22;
        UpdateSidebarClip(window);

        ApplySurface(window.TahoeToolbar, dark, .10);
        ApplySurface(window.TahoeWindowControls, dark, .10);
        ApplySurface(window.TahoeSidebar, dark, .20);
        if (IsDesktopBackdropRequested) ApplySurface(window.TahoeWindowGlass, dark, .10);
        else { window.TahoeWindowGlass.Visibility = Visibility.Collapsed; window.TahoeWindowGlass.BackdropSource = null; }
        UpdateTitleGlass(window);
        window.ShapeTitleLogo.Stroke = foreground;
        window.ShapeHMCLTitleLogo.Stroke = foreground;
        window.ShapeHMCLTitleLogo.Fill = foreground;
        window.LabTitleLogo.Foreground = foreground;
        window.CELogo.Foreground = foreground;
        window.LabTitleInner.Foreground = foreground;
        foreach (var icon in TitleIcons(window))
        {
            icon.Theme = MyIconButton.Themes.Custom;
            icon.Foreground = foreground;
        }
    }

    private static void ApplySurface(TahoeGlassSurface surface, bool dark, double opacity)
    {
        surface.BackdropSource = BackdropSource;
        surface.TintColor = SystemParameters.HighContrast ? SystemColors.WindowColor
            : dark ? Color.FromRgb(23, 29, 39) : Colors.White;
        surface.TintOpacity = SystemParameters.HighContrast ? 1 : opacity;
        surface.ForceCompatibility = SystemParameters.HighContrast;
        surface.BlurRadius = Profile.BlurRadius;
        surface.Dispersion = Profile.Dispersion;
        surface.HighlightStrength = Profile.Highlight;
        surface.RefractionStrength = .05;
        surface.Visibility = Visibility.Visible;
        surface.RefreshSampling();
    }

    private static void Restore(FormMain window)
    {
        _desktopCapture?.Apply(false);
        window.TahoeBackdrop.Visibility = Visibility.Visible;
        window.TahoeWallpaperHost.Visibility = Visibility.Visible;
        window.TahoeDesktopImage.Visibility = Visibility.Collapsed;
        if (_wallpaperSuppressed)
        {
            _wallpaperSuppressed = false;
            ModVideoBack.VideoPlay();
        }
        ThemeManager.RefreshBackground();
        window.TahoeToolbar.Visibility = window.TahoeSidebar.Visibility = Visibility.Collapsed;
        window.TahoeWindowControls.Visibility = Visibility.Collapsed;
        window.TahoeToolbar.BackdropSource = window.TahoeSidebar.BackdropSource = null;
        window.TahoeWindowControls.BackdropSource = null;
        window.TahoeWindowGlass.Visibility = Visibility.Collapsed;
        window.TahoeWindowGlass.BackdropSource = null;
        window.PanTitle.SetResourceReference(AnimatedBackgroundGrid.BackgroundBrushProperty, "ColorBrush3");
        window.RectLeftBackground.SetResourceReference(AnimatedBackgroundGrid.BackgroundBrushProperty, "ColorBrushTransparentBackground");
        window.ImgTitleGradient.Visibility = _gradientVisibility;
        window.ImgTitle.Visibility = _imageVisibility;
        window.RectLeftShadow.Opacity = _shadowOpacity;
        window.RectForm.RadiusX = _radiusX;
        window.RectForm.RadiusY = _radiusY;
        window.PanMainLeft.Clip = _originalSidebarClip;
        window.ShapeTitleLogo.Stroke = Brushes.White;
        window.ShapeHMCLTitleLogo.Stroke = window.ShapeHMCLTitleLogo.Fill = Brushes.White;
        window.LabTitleLogo.Foreground = window.CELogo.Foreground = window.LabTitleInner.Foreground = Brushes.White;
        foreach (var pair in OriginalIconThemes)
        {
            pair.Key.Theme = pair.Value;
            pair.Key.RefreshAnim();
        }
        OriginalIconThemes.Clear();
        foreach (var pair in OriginalThemeResources)
        {
            if (pair.Value is null) window.Resources.Remove(pair.Key);
            else window.Resources[pair.Key] = pair.Value;
        }
        OriginalThemeResources.Clear();
    }

    private static IEnumerable<MyIconButton> TitleIcons(FormMain window) =>
        [window.BtnTitleClose, window.BtnTitleMin, window.BtnTitleHelp, window.BtnTitleInner, window.BtnTitleNavOverflow];

    private static void SetBrush(FormMain window, string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        SetResource(window, key, brush);
    }

    private static void SetResource(FormMain window, string key, object value)
    {
        if (!OriginalThemeResources.ContainsKey(key))
            OriginalThemeResources[key] = window.Resources.Contains(key) ? window.Resources[key] : null;
        window.Resources[key] = value;
    }

    private static void OnSidebarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsEnabled && _window is { } window) UpdateSidebarClip(window);
    }

    private static void OnTitleSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsEnabled && _window is { } window) UpdateTitleGlass(window);
    }

    private static void UpdateTitleGlass(FormMain window)
    {
        var viewport = window.PanTitleSelectViewport;
        if (!viewport.IsLoaded || viewport.ActualWidth <= 0) return;
        Point origin = viewport.TranslatePoint(new Point(), window.PanTitle);
        double left = Math.Max(8, origin.X - 8);
        double width = Math.Max(0, Math.Min(viewport.ActualWidth + 16, window.PanTitle.ActualWidth - left - 84));
        window.TahoeToolbar.HorizontalAlignment = HorizontalAlignment.Left;
        window.TahoeToolbar.Width = width;
        window.TahoeToolbar.Margin = new Thickness(left, 7, 0, 7);
        window.TahoeToolbar.CornerRadius = 17;
    }

    private static void UpdateSidebarClip(FormMain window)
    {
        var size = window.PanMainLeft.RenderSize;
        window.PanMainLeft.Clip = new RectangleGeometry(
            new Rect(8, 3, Math.Max(0, size.Width - 14), Math.Max(0, size.Height - 11)), 22, 22);
    }

    private static void OnSystemSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) Refresh();
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemSettingsChanged;
        if (_window is not null)
        {
            _window.Closed -= OnClosed;
            _window.PanMainLeft.SizeChanged -= OnSidebarSizeChanged;
            _window.PanTitleSelectViewport.SizeChanged -= OnTitleSizeChanged;
            _window.PanTitle.SizeChanged -= OnTitleSizeChanged;
        }
        OriginalIconThemes.Clear();
        OriginalThemeResources.Clear();
        if (_desktopCapture != null)
        {
            _desktopCapture.StateChanged -= OnCaptureStateChanged;
            _desktopCapture.Dispose();
        }
        _desktopCapture = null;
        _window = null;
        _wallpaperSuppressed = false;
        _applied = false;
    }
}

/// <summary>Compatibility entry for the original PR's XAML and control hooks.</summary>
public static class TahoeThemeManager
{
    public static TahoeAppearance Appearance => LiquidGlassCore.Appearance;
    public static bool IsEnabled => LiquidGlassCore.IsEnabled;
    public static bool IsDesktopBackdropRequested => LiquidGlassCore.IsDesktopBackdropRequested;
    public static FrameworkElement? BackdropSource => LiquidGlassCore.BackdropSource;
    public static event EventHandler? Changed
    {
        add => LiquidGlassCore.Changed += value;
        remove => LiquidGlassCore.Changed -= value;
    }
    public static void Refresh() => LiquidGlassCore.Refresh();
}

/// <summary>Observable opt-in state for XAML triggers; styles revert automatically when disabled.</summary>
public sealed class TahoeAppearance : DependencyObject
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.Register(
        nameof(IsEnabled), typeof(bool), typeof(TahoeAppearance), new PropertyMetadata(false));

    public bool IsEnabled { get => (bool)GetValue(IsEnabledProperty); set => SetValue(IsEnabledProperty, value); }

    public static readonly DependencyProperty IsDesktopRequestedProperty = DependencyProperty.Register(
        nameof(IsDesktopRequested), typeof(bool), typeof(TahoeAppearance), new PropertyMetadata(false));
    public bool IsDesktopRequested { get => (bool)GetValue(IsDesktopRequestedProperty); set => SetValue(IsDesktopRequestedProperty, value); }

    public static readonly DependencyProperty DesktopStatusProperty = DependencyProperty.Register(
        nameof(DesktopStatus), typeof(string), typeof(TahoeAppearance), new PropertyMetadata(""));
    public string DesktopStatus { get => (string)GetValue(DesktopStatusProperty); set => SetValue(DesktopStatusProperty, value); }
}
