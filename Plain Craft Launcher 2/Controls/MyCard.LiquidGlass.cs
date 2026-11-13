using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;

namespace PCL;

public partial class MyCard
{
    /// <summary>Allows empty-state content to rest directly on the canvas in Tahoe mode.</summary>
    public bool TahoeIsFlat { get; set; }
    private bool _tahoeCardApplied;
    private CornerRadius _originalCardCornerRadius;
    private CornerRadius _originalSurfaceCornerRadius;
    private Thickness _originalCardBorderThickness;
    private Brush? _originalCardBorderBrush;
    private Thickness _originalShadowMargin;
    private double _originalShadowRadius;
    private double _originalShadowOpacity;
    private Thickness _originalTitleMargin;
    private FontWeight _originalTitleWeight;

    private void OnTahoeCardLoaded(object sender, RoutedEventArgs e)
    {
        TahoeThemeManager.Changed += OnTahoeCardThemeChanged;
        ApplyTahoeCard();
    }

    private void OnTahoeCardUnloaded(object sender, RoutedEventArgs e)
    {
        TahoeThemeManager.Changed -= OnTahoeCardThemeChanged;
        ModAnimation.AniStop("MyCard Mouse " + uuid);
    }

    private void OnTahoeCardThemeChanged(object? sender, EventArgs e) => ApplyTahoeCard();

    private void ApplyTahoeCard()
    {
        if (TahoeThemeManager.IsEnabled && !ThemeControlRegistry.HasTemplate(this))
        {
            if (!_tahoeCardApplied)
            {
                _originalCardCornerRadius = MainChrome.CornerRadius;
                _originalSurfaceCornerRadius = mainBorder.CornerRadius;
                _originalCardBorderThickness = mainBorder.BorderThickness;
                _originalCardBorderBrush = mainBorder.BorderBrush;
                _originalShadowMargin = MainChrome.Margin;
                _originalShadowRadius = MainChrome.ShadowRadius;
                _originalShadowOpacity = MainChrome.Opacity;
                _originalTitleMargin = MainTextBlock.Margin;
                _originalTitleWeight = MainTextBlock.FontWeight;
                _tahoeCardApplied = true;
            }

            ModAnimation.AniStop("MyCard Mouse " + uuid);
            var palette = LiquidGlassCore.CurrentPalette;
            // Translucent theme-harmonized card plane lets desktop and wallpaper glow softly breathe through
            // while keeping typography crisp and preserving true refractive lenses for floating chrome.
            MainChrome.SetCurrentValue(MyDropShadow.CornerRadiusProperty, new CornerRadius(16));
            mainBorder.SetCurrentValue(Border.CornerRadiusProperty, new CornerRadius(16));
            mainBorder.SetCurrentValue(Border.BorderThicknessProperty, new Thickness(TahoeIsFlat ? 0 : .5));
            mainBorder.SetCurrentValue(Border.BorderBrushProperty, new SolidColorBrush(
                SystemParameters.HighContrast ? SystemColors.WindowTextColor : palette.CardBorder));
            mainBorder.Background = TahoeContentBrush();
            MainTextBlock.Foreground = new SolidColorBrush(
                SystemParameters.HighContrast ? SystemColors.WindowTextColor : palette.Text);
            MainTextBlock.Margin = new Thickness(20, 13, 20, 0);
            MainTextBlock.FontWeight = FontWeights.SemiBold;
            MainChrome.SetCurrentValue(MarginProperty, new Thickness(-6, -5, -6, -7));
            MainChrome.SetCurrentValue(MyDropShadow.ShadowRadiusProperty, 6d);
            MainChrome.SetCurrentValue(OpacityProperty, 0d);
        }
        else if (_tahoeCardApplied)
        {
            ModAnimation.AniStop("MyCard Mouse " + uuid);
            MainChrome.SetCurrentValue(MyDropShadow.CornerRadiusProperty, _originalCardCornerRadius);
            mainBorder.SetCurrentValue(Border.CornerRadiusProperty, _originalSurfaceCornerRadius);
            mainBorder.SetCurrentValue(Border.BorderThicknessProperty, _originalCardBorderThickness);
            mainBorder.SetCurrentValue(Border.BorderBrushProperty, _originalCardBorderBrush);
            MainChrome.SetCurrentValue(MarginProperty, _originalShadowMargin);
            MainChrome.SetCurrentValue(MyDropShadow.ShadowRadiusProperty, _originalShadowRadius);
            MainChrome.SetCurrentValue(OpacityProperty, _originalShadowOpacity);
            mainBorder.Background = BackgroundBrush;
            MainTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
            MainTextBlock.Margin = _originalTitleMargin;
            MainTextBlock.FontWeight = _originalTitleWeight;
            _tahoeCardApplied = false;
        }
    }

    private SolidColorBrush TahoeContentBrush() => TahoeIsFlat ? Brushes.Transparent
        : SystemParameters.HighContrast ? SystemColors.WindowBrush
        : new SolidColorBrush(PCL.Core.UI.LiquidGlassMaterial.CardTint(
            LiquidGlassCore.CurrentPalette.CardSurface, Config.Preference.TahoeGlass.CardOpacity));
}
