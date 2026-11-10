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
        if (TahoeThemeManager.IsEnabled)
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

            // A stable capsule outline belongs to the navigation chrome, not the pointer highlight.
            SetCurrentValue(CornerRadiusProperty, new CornerRadius(13.5));
            SetCurrentValue(BorderThicknessProperty, new Thickness(0));
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
        var dark = ThemeManager.IsDarkMode;
        var foreground = Checked
            ? dark ? Color.FromRgb(226, 246, 255) : Color.FromRgb(0, 61, 115)
            : dark ? Color.FromRgb(225, 235, 243) : Color.FromRgb(35, 50, 63);
        LabText.FontWeight = Checked ? FontWeights.SemiBold : FontWeights.Normal;
        var background = Checked
            ? dark ? Color.FromArgb(50, 170, 215, 250) : Color.FromArgb(150, 255, 255, 255)
            : isMouseDown
                ? dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(188, 255, 255, 255)
                : IsMouseOver
                    ? dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(102, 255, 255, 255)
                    : Colors.Transparent;
        if (SystemParameters.HighContrast)
        {
            foreground = Checked ? SystemColors.HighlightTextColor : SystemColors.WindowTextColor;
            background = Checked ? SystemColors.HighlightColor : SystemColors.WindowColor;
        }

        SetCurrentValue(BorderBrushProperty, new SolidColorBrush(Checked
            ? dark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(210, 255, 255, 255)
            : Colors.Transparent));

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
