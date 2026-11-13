using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;

namespace PCL;

public partial class MyButton
{
    private bool _tahoeApplied;
    private bool _tahoeSubscribed;
    private bool _tahoeKeyboardPressed;
    private Key _tahoeKeyboardKey;
    private bool _tahoeContrastForeground;
    private CornerRadius _tahoeOriginalCornerRadius;
    private Thickness _tahoeOriginalBorderThickness;
    private Thickness _tahoeOriginalContentMargin;
    private double _tahoeOriginalOpacity;
    private FontWeight _tahoeOriginalFontWeight;
    private TahoeGlassSurface? _tahoeSurface;
    private TahoeButtonMotion? _tahoeMotion;
    private Border? _tahoeWash;
    private Border? _tahoeBody;
    private Border? _tahoeHighlight;
    private Border? _tahoeFocus;

    private void InitializeTahoeButton()
    {
        Loaded += TahoeButtonLoaded;
        Unloaded += TahoeButtonUnloaded;
        SizeChanged += (_, _) => RefreshTahoeButtonGeometry();
        MouseMove += (_, e) => _tahoeMotion?.Move(e.GetPosition(this));
        PreviewKeyDown += TahoeButtonKeyDown;
        PreviewKeyUp += TahoeButtonKeyUp;
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (!IsKeyboardFocusWithin)
            {
                _tahoeKeyboardPressed = false;
                _tahoeMotion?.Release();
            }
            RefreshTahoeButtonAppearance();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                isMouseDown = false;
                _tahoeKeyboardPressed = false;
                _tahoeMotion?.Reset();
            }
        };
    }

    private void TahoeButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (!_tahoeSubscribed)
        {
            TahoeThemeManager.Changed += TahoeButtonThemeChanged;
            _tahoeSubscribed = true;
        }
        ApplyTahoeButtonTheme();
    }

    private void TahoeButtonUnloaded(object sender, RoutedEventArgs e)
    {
        if (_tahoeSubscribed)
        {
            TahoeThemeManager.Changed -= TahoeButtonThemeChanged;
            _tahoeSubscribed = false;
        }
        _tahoeKeyboardPressed = false;
        isMouseDown = false;
        _tahoeMotion?.Dispose();
        _tahoeMotion = null;
        if (_tahoeSurface != null) _tahoeSurface.BackdropSource = null;
    }

    private void TahoeButtonThemeChanged(object? sender, EventArgs e) => ApplyTahoeButtonTheme();

    private void ApplyTahoeButtonTheme()
    {
        var enabled = TahoeThemeManager.IsEnabled && !ThemeControlRegistry.HasTemplate(this);
        if (!enabled && !_tahoeApplied) return;
        if (enabled && !_tahoeApplied)
        {
            _tahoeOriginalCornerRadius = PanFore.CornerRadius;
            _tahoeOriginalBorderThickness = PanFore.BorderThickness;
            _tahoeOriginalContentMargin = PanButtonContent.Margin;
            _tahoeOriginalOpacity = PanButtonVisual.Opacity;
            _tahoeOriginalFontWeight = LabText.FontWeight;
        }
        ModAnimation.AniStop("MyButton Scale " + Uuid);
        ModAnimation.AniStop("MyButton Background " + Uuid);
        _tahoeMotion?.Reset();
        _tahoeApplied = enabled;
        isMouseDown = false;
        _tahoeKeyboardPressed = false;
        if (PanFore.RenderTransform is ScaleTransform scale)
            scale.ScaleX = scale.ScaleY = 1;

        if (enabled)
        {
            EnsureTahoeButtonLayers();
            _tahoeSurface!.Visibility = Visibility.Visible;
            _tahoeSurface.BackdropSource = TahoeThemeManager.BackdropSource;
            PanFore.Background = Brushes.Transparent;
            PanFore.BorderThickness = new Thickness(0);
            // Preserve the original border's contribution to the content measurement.
            PanButtonContent.Margin = new Thickness(
                _tahoeOriginalContentMargin.Left + _tahoeOriginalBorderThickness.Left,
                _tahoeOriginalContentMargin.Top + _tahoeOriginalBorderThickness.Top,
                _tahoeOriginalContentMargin.Right + _tahoeOriginalBorderThickness.Right,
                _tahoeOriginalContentMargin.Bottom + _tahoeOriginalBorderThickness.Bottom);
            PanBack.Background = Brushes.Transparent;
            RefreshTahoeButtonGeometry();
            RefreshTahoeButtonAppearance();
        }
        else
        {
            _tahoeMotion?.Dispose();
            _tahoeMotion = null;
            _tahoeSurface!.BackdropSource = null;
            _tahoeSurface.Visibility = Visibility.Collapsed;
            _tahoeBody!.Visibility = Visibility.Collapsed;
            _tahoeWash!.Visibility = _tahoeHighlight!.Visibility = Visibility.Collapsed;
            _tahoeFocus!.Visibility = Visibility.Collapsed;
            PanFore.CornerRadius = _tahoeOriginalCornerRadius;
            PanFore.BorderThickness = _tahoeOriginalBorderThickness;
            PanButtonContent.Margin = _tahoeOriginalContentMargin;
            PanButtonVisual.Opacity = _tahoeOriginalOpacity;
            LabText.FontWeight = _tahoeOriginalFontWeight;
            RestoreTahoeForeground();
            PanBack.SetResourceReference(BackgroundProperty, "ColorBrushSemiTransparent");
            PanFore.SetResourceReference(BackgroundProperty, IsMouseOver
                ? (_ColorType == ColorState.Red ? "ColorBrushRedBack" : "ColorBrush7")
                : "ColorBrushHalfWhite");
        }
    }

    private void EnsureTahoeButtonLayers()
    {
        if (_tahoeSurface == null)
        {
            _tahoeSurface = new TahoeGlassSurface
            {
                IsHitTestVisible = false,
                RefractionStrength = 0.05,
                BlurRadius = LiquidGlassCore.Profile.BlurRadius,
                Dispersion = LiquidGlassCore.Profile.Dispersion,
                HighlightStrength = LiquidGlassCore.Profile.Highlight,
                TintOpacity = 0.1
            };
            _tahoeWash = new Border { Background = Brushes.White, Opacity = 0, IsHitTestVisible = false };
            _tahoeBody = new Border { IsHitTestVisible = false, BorderThickness = new Thickness(.5) };
            _tahoeHighlight = new Border { Opacity = 0, IsHitTestVisible = false };
            _tahoeFocus = new Border
            {
                BorderThickness = new Thickness(2),
                Margin = new Thickness(-3),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _tahoeFocus.SetResourceReference(BorderBrushProperty, "ColorBrush2");
            PanButtonVisual.Children.Insert(0, _tahoeSurface);
            PanButtonVisual.Children.Insert(1, _tahoeBody);
            PanButtonVisual.Children.Insert(2, _tahoeWash);
            PanButtonVisual.Children.Insert(3, _tahoeHighlight);
            PanButtonVisual.Children.Add(_tahoeFocus);
        }
        if (_tahoeMotion == null && PanFore.RenderTransform is ScaleTransform scale)
            _tahoeMotion = new TahoeButtonMotion(this, scale,
                (TranslateTransform)PanButtonVisual.RenderTransform, _tahoeWash!, _tahoeHighlight!,
                _tahoeSurface.RequestRefreshSampling);
    }

    private void RefreshTahoeButtonGeometry()
    {
        if (!_tahoeApplied || _tahoeSurface == null) return;
        var radius = new CornerRadius(Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2));
        PanFore.CornerRadius = radius;
        _tahoeSurface.CornerRadius = radius.TopLeft;
        _tahoeWash!.CornerRadius = radius;
        _tahoeBody!.CornerRadius = radius;
        _tahoeHighlight!.CornerRadius = radius;
        _tahoeFocus!.CornerRadius = new CornerRadius(radius.TopLeft + 3);
        _tahoeSurface.RefreshSampling();
    }

    private void RefreshTahoeButtonAppearance()
    {
        if (!_tahoeApplied || _tahoeSurface == null) return;
        var highContrast = SystemParameters.HighContrast;
        bool dark = ThemeManager.IsDarkMode;
        bool primary = _ColorType == ColorState.Highlight;
        bool danger = _ColorType == ColorState.Red;
        bool hover = IsMouseOver && IsEnabled;
        var palette = LiquidGlassCore.CurrentPalette;
        LabText.FontWeight = primary ? FontWeights.SemiBold : _tahoeOriginalFontWeight;
        // Keep the optical lens perimeter visible on every action button while tinting primary actions like jewel glass.
        _tahoeSurface.Visibility = Visibility.Visible;
        _tahoeSurface.BackdropSource = TahoeThemeManager.BackdropSource;
        _tahoeBody!.Visibility = _tahoeWash!.Visibility = _tahoeHighlight!.Visibility = Visibility.Visible;
        _tahoeBody.Background = new SolidColorBrush(highContrast ? SystemColors.WindowColor
            : primary ? (hover ? palette.PrimaryButtonHoverFill : palette.PrimaryButtonFill)
            : danger ? Color.FromArgb(hover ? (byte)54 : (byte)24, 224, 58, 52)
            : hover ? Color.FromArgb(dark ? (byte)24 : (byte)64, 255, 255, 255)
            : Colors.Transparent);
        // The material owns its rim; avoid a second outline covering the bevel.
        _tahoeBody.BorderBrush = highContrast ? SystemColors.WindowTextBrush : Brushes.Transparent;
        _tahoeSurface.ForceCompatibility = highContrast;
        _tahoeSurface.IsDarkMode = dark;
        _tahoeSurface.DiffuseStrength = dark ? 0.14 : 0.17;
        _tahoeSurface.EdgeSharpness = 0.90;
        _tahoeSurface.BlurRadius = LiquidGlassCore.Profile.BlurRadius;
        _tahoeSurface.FrostRadius = PCL.Core.UI.LiquidGlassMaterial.FrostRadius(Config.Preference.TahoeGlass.FrostRadius);
        _tahoeSurface.RefractionStrength = PCL.Core.UI.LiquidGlassMaterial.RefractionStrength(Config.Preference.TahoeGlass.RefractionStrength);
        _tahoeSurface.Dispersion = LiquidGlassCore.Profile.Dispersion;
        _tahoeSurface.HighlightStrength = LiquidGlassCore.Profile.Highlight;
        _tahoeSurface.TintColor = highContrast ? SystemColors.WindowColor : palette.ButtonTint;
        _tahoeSurface.TintOpacity = highContrast ? 1 : PCL.Core.UI.LiquidGlassMaterial.SurfaceWashOpacity(dark,
            TahoeThemeManager.IsDesktopBackdropRequested, hover ? 0.16 : 0.12, hover ? 0.28 : 0.22);
        PanButtonVisual.Opacity = _tahoeOriginalOpacity * (IsEnabled || highContrast ? 1 : 0.5);
        _tahoeFocus!.Visibility = IsKeyboardFocusWithin && IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (highContrast) _tahoeFocus.SetResourceReference(BorderBrushProperty, SystemColors.HighlightBrushKey);
        else _tahoeFocus.BorderBrush = new SolidColorBrush(palette.Accent);
        _tahoeContrastForeground = true;
        LabText.Foreground = new SolidColorBrush(highContrast
            ? IsEnabled ? SystemColors.WindowTextColor : SystemColors.GrayTextColor
            : primary ? palette.PrimaryButtonText
            : danger ? dark ? Color.FromRgb(255, 115, 110) : Color.FromRgb(190, 45, 40)
            : palette.Text);
        if (!IsEnabled)
        {
            isMouseDown = false;
            _tahoeKeyboardPressed = false;
            _tahoeMotion?.Reset();
        }
    }

    private void RestoreTahoeForeground()
    {
        if (!_tahoeContrastForeground) return;
        _tahoeContrastForeground = false;
        BindingOperations.SetBinding(LabText, TextBlock.ForegroundProperty,
            new Binding(nameof(Border.BorderBrush)) { Source = PanFore });
    }

    private void TahoeButtonKeyDown(object sender, KeyEventArgs e)
    {
        if (!_tahoeApplied || !IsEnabled || e.Key is not (Key.Space or Key.Enter)) return;
        if (!CanHandleTahoeKeyboardInput()) return;
        e.Handled = true;
        if (e.IsRepeat || _tahoeKeyboardPressed) return;
        _tahoeKeyboardPressed = true;
        _tahoeKeyboardKey = e.Key;
        _tahoeMotion?.Press(new Point(ActualWidth / 2, ActualHeight / 2));
    }

    private void TahoeButtonKeyUp(object sender, KeyEventArgs e)
    {
        if (!_tahoeApplied || e.Key != _tahoeKeyboardKey || !_tahoeKeyboardPressed) return;
        if (!CanHandleTahoeKeyboardInput()) return;
        e.Handled = true;
        _tahoeKeyboardPressed = false;
        _tahoeMotion?.Release();
        if (!IsEnabled || !IsKeyboardFocusWithin) return;
        var mouseEvent = new MouseButtonEventArgs(Mouse.PrimaryDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = MouseLeftButtonUpEvent,
            Source = this
        };
        InvokeButtonClick(this, mouseEvent);
    }

    private bool CanHandleTahoeKeyboardInput()
    {
        var main = ModMain.frmMain;
        if (main is null || Window.GetWindow(this) != main || main.PanMsg.Children.Count == 0 ||
            main.PanMsg.IsAncestorOf(this)) return true;

        // Some modal dialogs retain the underlying control's keyboard focus.
        // Leave Enter unhandled so the window routes it to the current dialog.
        _tahoeKeyboardPressed = false;
        _tahoeKeyboardKey = Key.None;
        _tahoeMotion?.Release();
        return false;
    }
}
