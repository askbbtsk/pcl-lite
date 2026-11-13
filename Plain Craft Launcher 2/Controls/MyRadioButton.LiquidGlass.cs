using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PCL;

public partial class MyRadioButton
{
    private bool _tahoeChromeApplied;
    private CornerRadius _originalCornerRadius;
    private Thickness _originalBorderThickness;
    private Brush? _originalBorderBrush;
    private FontWeight _originalFontWeight;

    private void OnTahoeThemeChanged(object? sender, EventArgs e)
    {
        ApplyTahoeChrome();
        RefreshColor(null, false);
    }

    private void ApplyTahoeChrome()
    {
        if (TahoeThemeManager.IsEnabled && !ThemeControlRegistry.HasTemplate(this))
        {
            if (!_tahoeChromeApplied)
            {
                _originalCornerRadius = CornerRadius;
                _originalBorderThickness = BorderThickness;
                _originalBorderBrush = BorderBrush;
                _originalFontWeight = LabText.FontWeight;
                _tahoeChromeApplied = true;
                ModAnimation.AniStop("MyRadioButton Checked " + Uuid);
                ModAnimation.AniStop("MyRadioButton Color " + Uuid);
            }

            // A subtle top-lit capsule sits fused inside the toolbar glass rather than fighting its rim.
            SetCurrentValue(CornerRadiusProperty, new CornerRadius(13.5));
            SetCurrentValue(BorderThicknessProperty, new Thickness(.5));
            LabText.SetCurrentValue(TextBlock.FontWeightProperty, Checked ? FontWeights.SemiBold : FontWeights.Normal);
        }
        else if (_tahoeChromeApplied)
        {
            SetCurrentValue(CornerRadiusProperty, _originalCornerRadius);
            SetCurrentValue(BorderThicknessProperty, _originalBorderThickness);
            SetCurrentValue(BorderBrushProperty, _originalBorderBrush);
            LabText.SetCurrentValue(TextBlock.FontWeightProperty, _originalFontWeight);
            _tahoeChromeApplied = false;
        }
    }

    private void RefreshTahoeColor(bool animate)
    {
        bool dark = ThemeManager.IsDarkMode;
        bool contrast = SystemParameters.HighContrast;
        var palette = LiquidGlassCore.CurrentPalette;
        var foreground = contrast
            ? Checked ? SystemColors.HighlightTextColor : SystemColors.WindowTextColor
            : Checked ? palette.ActionText : palette.Text;
        LabText.FontWeight = Checked ? FontWeights.SemiBold : FontWeights.Normal;
        var background = contrast
            ? Checked ? SystemColors.HighlightColor : SystemColors.WindowColor
            : Checked ? palette.NavCheckedFill
            : isMouseDown ? palette.NavPressFill
            : IsMouseOver ? palette.NavHoverFill
            : Colors.Transparent;

        SetCurrentValue(BorderBrushProperty, PCL.Core.UI.LiquidGlassMaterial.NavigationPillBorder(dark, Checked, contrast));

        // All states use the same readable foreground; selection is expressed by the material.
        var foregroundBrush = new SolidColorBrush(foreground);
        if (animate && IsLoaded && SystemParameters.ClientAreaAnimation && ModAnimation.AniControlEnabled == 0)
        {
            var duration = Checked ? animationTimeOfCheck : IsMouseOver ? animationTimeOfMouseIn : animationTimeOfMouseOut;
            ModAnimation.AniStart(new[]
            {
                ModAnimation.AaColor(ShapeLogo, Shape.FillProperty,
                    new ModBase.MyColor(foregroundBrush) - ShapeLogo.Fill, duration),
                ModAnimation.AaColor(LabText, TextBlock.ForegroundProperty,
                    new ModBase.MyColor(foregroundBrush) - LabText.Foreground, duration)
            }, "MyRadioButton Checked " + Uuid);
            ModAnimation.AniStart(ModAnimation.AaColor(this, BackgroundProperty,
                new ModBase.MyColor(new SolidColorBrush(background)) - Background, duration),
                "MyRadioButton Color " + Uuid);
        }
        else
        {
            ModAnimation.AniStop("MyRadioButton Checked " + Uuid);
            ModAnimation.AniStop("MyRadioButton Color " + Uuid);
            ShapeLogo.Fill = foregroundBrush;
            LabText.Foreground = foregroundBrush;
            Background = new SolidColorBrush(background);
        }
    }
}
