using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCL;

public partial class MyComboBox
{
    private bool _tahoeComboApplied;

    private void TahoeComboLoaded(object sender, RoutedEventArgs e)
    {
        TahoeThemeManager.Changed += TahoeComboThemeChanged;
        RefreshColor();
    }

    private void TahoeComboUnloaded(object sender, RoutedEventArgs e)
    {
        TahoeThemeManager.Changed -= TahoeComboThemeChanged;
        ModAnimation.AniStop("MyComboBox Color " + Uuid);
    }

    private void TahoeComboThemeChanged(object? sender, EventArgs e) => RefreshColor();

    private bool RefreshTahoeComboChrome()
    {
        if (!TahoeThemeManager.IsEnabled || ThemeControlRegistry.HasTemplate(this))
        {
            if (_tahoeComboApplied && Template?.FindName("border", this) is Border original)
            {
                original.CornerRadius = new CornerRadius(3);
                // Restore the original template binding, including subsequent color changes.
                original.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
            }
            _tahoeComboApplied = false;
            return false;
        }

        _tahoeComboApplied = true;
        ModAnimation.AniStop("MyComboBox Color " + Uuid);
        bool contrast = SystemParameters.HighContrast;
        bool focus = IsEnabled && (IsKeyboardFocusWithin || IsDropDownOpen);
        var palette = LiquidGlassCore.CurrentPalette;
        Foreground = new SolidColorBrush(contrast
            ? IsEnabled ? SystemColors.WindowTextColor : SystemColors.GrayTextColor
            : IsEnabled ? palette.Text : palette.SecondaryText);
        Background = new SolidColorBrush(contrast ? SystemColors.WindowColor
            : focus ? palette.FieldFocusSurface : palette.FieldSurface);
        if (Template?.FindName("border", this) is Border border)
        {
            border.CornerRadius = new CornerRadius(10);
            border.BorderBrush = new SolidColorBrush(!IsEnabled
                ? contrast ? SystemColors.GrayTextColor
                    : Color.FromArgb((byte)(palette.FieldBorder.A / 2), palette.FieldBorder.R, palette.FieldBorder.G, palette.FieldBorder.B)
                : contrast ? SystemColors.WindowTextColor
                : focus ? palette.Accent
                : palette.FieldBorder);
        }
        return true;
    }
}
