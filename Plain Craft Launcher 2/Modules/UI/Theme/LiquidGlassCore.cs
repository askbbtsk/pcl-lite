using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PCL.Core.App;

namespace PCL;

/// <summary>
/// Native optical material. A screenshot-compatible sampler provides pixels to an independent sibling
/// source; the existing shader refracts that source without sampling foreground UI.
/// </summary>
public static partial class LiquidGlassCore
{
    private static FormMain? _window;
    private static bool _applied;
    private static bool _refreshing;
    private static Visibility _gradientVisibility, _imageVisibility;
    private static double _shadowOpacity, _radiusX, _radiusY;
    private static Visibility _windowShadowVisibility;
    private static string? _lastDiagnostics;
    private static Geometry? _originalSidebarClip;
    private static TahoeDesktopCapture? _desktopCapture;
    private static LiquidGlassWindowClip? _windowClip;
    private static readonly Dictionary<MyIconButton, MyIconButton.Themes> OriginalIconThemes = new();
    private static readonly Dictionary<string, object?> OriginalThemeResources = new();

    public static TahoeAppearance Appearance { get; } = new();
    private static PCL.Core.UI.LiquidGlassThemePalette _palette =
        PCL.Core.UI.LiquidGlassMaterial.ComputePalette(210d, 80d, 0d, false);
    public static PCL.Core.UI.LiquidGlassThemePalette CurrentPalette => _palette;
    public static bool IsEnabled => PCL.Core.UI.LiquidGlassPerformance.IsEnabled(
        Config.Preference.TahoeGlass.IsEnabled, Config.Preference.TahoeGlass.PerformanceConsent)
        && CustomThemeRuntime.AllowsGlass && !_disabledAfterFailure;
    public static bool IsDesktopBackdropActive => IsEnabled && _desktopCapture?.IsActive == true;
    public static bool IsDesktopBackdropRequested => PCL.Core.UI.LiquidGlassPerformance.ShouldCaptureDesktop(
        Config.Preference.TahoeGlass.IsEnabled, Config.Preference.TahoeGlass.PerformanceConsent,
        Config.Preference.TahoeGlass.UseDesktopBackdrop, Config.Preference.TahoeGlass.DesktopCaptureConsent)
        && CustomThemeRuntime.AllowsGlass && !_disabledAfterFailure;
    public static FrameworkElement? BackdropSource => IsEnabled &&
        (!IsDesktopBackdropRequested || _desktopCapture?.HasFrame == true)
            ? IsDesktopBackdropRequested ? _window?.TahoeDesktopImage : _window?.TahoeBackdrop : null;
    public static event EventHandler? Changed;
    public static PCL.Core.UI.LiquidGlassProfile Profile =>
        PCL.Core.UI.LiquidGlassPerformance.ResolveProfile(Config.Preference.TahoeGlass.PerformancePreset,
            Config.Preference.TahoeGlass.MaximumFrameRate, Config.Preference.TahoeGlass.BlurRadius,
            Config.Preference.TahoeGlass.Dispersion, Config.Preference.TahoeGlass.Highlight, Config.Preference.TahoeGlass.Motion);
    public static int SamplingFrameRate => PCL.Core.UI.LiquidGlassPerformance.SamplingFrameRate(
        Config.Preference.TahoeGlass.SamplingFrameRate, Config.Preference.TahoeGlass.MaximumFrameRate);
    public static double DiffuseStrength => PCL.Core.UI.LiquidGlassPerformance.Adjustment(
        Config.Preference.TahoeGlass.Diffuse, ThemeManager.IsDarkMode ? 14 : 17, 50) / 100d;
    public static double EdgeSharpness => PCL.Core.UI.LiquidGlassPerformance.Adjustment(
        Config.Preference.TahoeGlass.EdgeSharpness, 90, 100) / 100d;
    public static double TintScale => PCL.Core.UI.LiquidGlassPerformance.Adjustment(
        Config.Preference.TahoeGlass.TintScale, 100, 100) / 100d;
    public static int RefractionPercent => PCL.Core.UI.LiquidGlassMaterial.ResolveRefractionPercent(
        Config.Preference.TahoeGlass.RefractionPercent, Config.Preference.TahoeGlass.RefractionStrength);
    public static double RefractionStrength => PCL.Core.UI.LiquidGlassMaterial.RefractionFromPercent(RefractionPercent);

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
                AttachInteraction(window);
                _windowClip = new LiquidGlassWindowClip(window, window.PanForm, window.RectForm,
                    error => LogCapture("[LiquidGlass] Native window clipping failed", error));
                ITahoeCapturePlatform capturePlatform =
                    new LiquidGlassScreenshotCapturePlatform(() => SamplingFrameRate);
                LogCapture("[LiquidGlass] CaptureBackend=Magnifier; GlobalCaptureExclusion=False", null);
                _desktopCapture = new TahoeDesktopCapture(window, window.PanForm, window.TahoeDesktopImage,
                    window.TransformRotate, window.TransformPos, capturePlatform, LogCapture);
                _desktopCapture.StateChanged += OnCaptureStateChanged;
                window.Closed += OnClosed;
                window.PanMainLeft.SizeChanged += OnSidebarSizeChanged;
                window.PanTitleSelectViewport.SizeChanged += OnTitleSizeChanged;
                window.PanTitle.SizeChanged += OnTitleSizeChanged;
                window.PanTitleMain.IsVisibleChanged += OnTitleNavigationChanged;
                window.PanTitleMain.IsHitTestVisibleChanged += OnTitleNavigationChanged;
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
            Appearance.IsDesktopRequested = IsDesktopBackdropRequested;
            string captureState = _windowMoving && IsDesktopBackdropRequested ? "Moving"
                : (_desktopCapture?.State ?? TahoeCaptureState.Stopped).ToString();
            if (_desktopCapture?.State == TahoeCaptureState.Live && !window.TahoeWindowGlass.IsRefractionActive)
                captureState = "Compatibility";
            Appearance.DesktopStatus = PCL.Core.App.Localization.Lang.Text("Setup.Ui.Tahoe.State." + captureState);
            Changed?.Invoke(null, EventArgs.Empty);
            if (IsEnabled)
            {
                bool darkNow = ThemeManager.IsDarkMode;
                var (hueNow, satNow, lightNow) = PCL.Core.UI.LiquidGlassMaterial.ResolveCurrentThemeHsl();
                _palette = PCL.Core.UI.LiquidGlassMaterial.ComputePalette(
                    hueNow, satNow, lightNow, darkNow, SystemParameters.HighContrast);
                foreach (var surface in GlassSurfaces(window))
                {
                    surface.BlurRadius = Profile.BlurRadius;
                    surface.FrostRadius = PCL.Core.UI.LiquidGlassMaterial.FrostRadius(Config.Preference.TahoeGlass.FrostRadius);
                    surface.RefractionStrength = RefractionStrength;
                    surface.Dispersion = Profile.Dispersion;
                    surface.HighlightStrength = Profile.Highlight;
                    surface.IsDarkMode = darkNow;
                    surface.DiffuseStrength = DiffuseStrength;
                    surface.EdgeSharpness = EdgeSharpness;
                    surface.Brightness = SystemParameters.HighContrast ? 0 : Math.Clamp(Config.Preference.TahoeGlass.Brightness, -50, 50);
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

    public static void ApplyOpacity(int value)
    {
        var window = ModMain.frmMain;
        if (window?.LauncherBackgroundLayer is null) return;
        PCL.Core.UI.LiquidGlassComposition.Apply(IsEnabled, value, window,
            window.LauncherBackgroundLayer, window.TahoeWallpaperHost, window.TahoeDesktopImage);
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
        _windowShadowVisibility = window.PanWindowShadow.Visibility;
        _radiusX = window.RectForm.RadiusX;
        _radiusY = window.RectForm.RadiusY;
        _originalSidebarClip = window.PanMainLeft.Clip;
        OriginalIconThemes.Clear();
        foreach (var icon in TitleIcons(window)) OriginalIconThemes[icon] = icon.Theme;
    }

    private static void Apply(FormMain window)
    {
        bool dark = ThemeManager.IsDarkMode;
        bool highContrast = SystemParameters.HighContrast;
        var (hue, saturation, lightAdjust) = PCL.Core.UI.LiquidGlassMaterial.ResolveCurrentThemeHsl();
        var palette = PCL.Core.UI.LiquidGlassMaterial.ComputePalette(hue, saturation, lightAdjust, dark, highContrast);
        _palette = palette;
        var foreground = new SolidColorBrush(highContrast ? SystemColors.WindowTextColor : palette.Text);
        foreground.Freeze();
        _desktopCapture!.SetInteractiveMove(_windowMoving);
        _desktopCapture.Apply(IsDesktopBackdropRequested);
        window.TahoeBackdrop.Visibility = Visibility.Visible;
        window.TahoeWallpaperHost.Visibility = Visibility.Visible;
        window.TahoeDesktopImage.Visibility = IsDesktopBackdropRequested ? Visibility.Visible : Visibility.Collapsed;
        ThemeManager.RefreshBackground();
        ApplyOpacity(Config.Preference.Theme.WindowOpacity);
        SetBrush(window, "TahoeText", palette.Text);
        SetBrush(window, "TahoeSecondaryText", palette.SecondaryText);
        SetBrush(window, "TahoeSidebarSection", palette.SidebarSection);
        SetBrush(window, "TahoeFieldSurface", palette.FieldSurface);
        SetBrush(window, "TahoeFieldBorder", palette.FieldBorder);
        SetBrush(window, "TahoeAccent", palette.Accent);
        SetBrush(window, "TahoeActionText", palette.ActionText);
        SetBrush(window, "ColorBrush1", palette.Brush1);
        SetBrush(window, "ColorBrush2", palette.Brush2);
        SetBrush(window, "ColorBrush3", palette.Brush3);
        SetBrush(window, "ColorBrush4", palette.Brush4);
        SetBrush(window, "ColorBrush6", palette.Brush6);
        SetBrush(window, "ColorBrush7", palette.Brush7);
        SetBrush(window, "ColorBrush8", palette.Brush8);
        SetBrush(window, "ColorBrushGray1", palette.Gray1);
        SetBrush(window, "ColorBrushGray2", palette.Gray2);
        SetBrush(window, "ColorBrushGray3", palette.Gray3);
        if (highContrast)
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
        window.PanTitle.SuppressThemeBackground = true;
        window.PanTitle.BackgroundBrush = Brushes.Transparent;
        window.RectLeftBackground.BackgroundBrush = Brushes.Transparent;
        window.ImgTitleGradient.Visibility = Visibility.Hidden;
        window.ImgTitle.Visibility = Visibility.Hidden;
        window.RectLeftShadow.Opacity = 0;
        window.PanWindowShadow.Visibility = Visibility.Collapsed;
        window.RectForm.RadiusX = window.RectForm.RadiusY = 22;
        _windowClip?.SetEnabled(true);
        UpdateSidebarClip(window);

        ApplySurface(window.TahoeToolbar, palette.ToolbarTint, dark, dark ? .14 : .18, dark ? .26 : .28);
        ApplySurface(window.TahoeWindowControls, palette.ToolbarTint, dark, dark ? .14 : .18, dark ? .26 : .28);
        ApplySurface(window.TahoeSidebar, palette.SidebarTint, dark, dark ? .20 : .22, dark ? .32 : .36);
        if (IsDesktopBackdropRequested) ApplySurface(window.TahoeWindowGlass, palette.WindowTint, dark, .10, dark ? .26 : .30);
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

    private static void ApplySurface(
        TahoeGlassSurface surface, Color tintColor, bool dark, double wallpaperOpacity, double desktopOpacity)
    {
        surface.BackdropSource = BackdropSource;
        surface.TintColor = SystemParameters.HighContrast ? SystemColors.WindowColor : tintColor;
        surface.TintOpacity = SystemParameters.HighContrast ? 1
            : PCL.Core.UI.LiquidGlassMaterial.SurfaceWashOpacity(dark, IsDesktopBackdropRequested, wallpaperOpacity, desktopOpacity) * TintScale;
        surface.ForceCompatibility = SystemParameters.HighContrast;
        surface.IsDarkMode = dark;
        surface.DiffuseStrength = DiffuseStrength;
        surface.EdgeSharpness = EdgeSharpness;
        surface.Brightness = SystemParameters.HighContrast ? 0 : Math.Clamp(Config.Preference.TahoeGlass.Brightness, -50, 50);
        surface.BlurRadius = Profile.BlurRadius;
        surface.FrostRadius = PCL.Core.UI.LiquidGlassMaterial.FrostRadius(Config.Preference.TahoeGlass.FrostRadius);
        surface.Dispersion = Profile.Dispersion;
        surface.HighlightStrength = Profile.Highlight;
        surface.RefractionStrength = RefractionStrength;
        surface.Visibility = Visibility.Visible;
        surface.RefreshSampling();
    }

    private static void Restore(FormMain window)
    {
        _windowClip?.SetEnabled(false);
        _desktopCapture?.Apply(false);
        window.TahoeBackdrop.Visibility = Visibility.Visible;
        window.TahoeWallpaperHost.Visibility = Visibility.Visible;
        window.TahoeDesktopImage.Visibility = Visibility.Collapsed;
        ApplyOpacity(Config.Preference.Theme.WindowOpacity);
        ThemeManager.RefreshBackground();
        window.TahoeToolbar.Visibility = window.TahoeSidebar.Visibility = Visibility.Collapsed;
        window.TahoeWindowControls.Visibility = Visibility.Collapsed;
        window.TahoeToolbar.BackdropSource = window.TahoeSidebar.BackdropSource = null;
        window.TahoeWindowControls.BackdropSource = null;
        window.TahoeWindowGlass.Visibility = Visibility.Collapsed;
        window.TahoeWindowGlass.BackdropSource = null;
        window.PanTitle.SuppressThemeBackground = false;
        window.PanTitle.SetResourceReference(AnimatedBackgroundGrid.BackgroundBrushProperty, "ColorBrush3");
        window.RectLeftBackground.SetResourceReference(AnimatedBackgroundGrid.BackgroundBrushProperty, "ColorBrushTransparentBackground");
        window.ImgTitleGradient.Visibility = _gradientVisibility;
        window.ImgTitle.Visibility = _imageVisibility;
        window.RectLeftShadow.Opacity = _shadowOpacity;
        window.PanWindowShadow.Visibility = _windowShadowVisibility;
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

    private static void SetBrush(FormMain window, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        SetResource(window, key, brush);
    }

    private static void SetResource(FormMain window, string key, object value)
    {
        if (!SystemParameters.HighContrast && CustomThemeRuntime.Current?.FindOverride(key) is { } themeValue)
            value = themeValue;
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

    private static void OnTitleNavigationChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsEnabled && _window is { } window) UpdateTitleGlass(window);
    }

    private static void UpdateTitleGlass(FormMain window)
    {
        // The main navigation fades out before its layout is collapsed. Its independent
        // glass surface must leave immediately as well, rather than remain over the child title.
        bool showNavigation = window.PanTitleMain.IsVisible && window.PanTitleMain.IsHitTestVisible;
        window.TahoeToolbar.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
        if (!showNavigation) return;
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
        _windowClip?.Dispose();
        _windowClip = null;
        DetachInteraction();
        SystemParameters.StaticPropertyChanged -= OnSystemSettingsChanged;
        if (_window is not null)
        {
            _window.Closed -= OnClosed;
            _window.PanMainLeft.SizeChanged -= OnSidebarSizeChanged;
            _window.PanTitleSelectViewport.SizeChanged -= OnTitleSizeChanged;
            _window.PanTitle.SizeChanged -= OnTitleSizeChanged;
            _window.PanTitleMain.IsVisibleChanged -= OnTitleNavigationChanged;
            _window.PanTitleMain.IsHitTestVisibleChanged -= OnTitleNavigationChanged;
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
