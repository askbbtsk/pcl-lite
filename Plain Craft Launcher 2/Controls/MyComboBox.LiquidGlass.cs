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
        if (!TahoeThemeManager.IsEnabled)
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
        bool dark = ThemeManager.IsDarkMode;
        bool focus = IsEnabled && (IsKeyboardFocusWithin || IsDropDownOpen);
        Foreground = new SolidColorBrush(!IsEnabled
            ? contrast ? SystemColors.GrayTextColor : dark ? Color.FromRgb(151, 157, 167) : Color.FromRgb(112, 116, 124)
            : contrast ? SystemColors.WindowTextColor
            : dark ? Color.FromRgb(242, 243, 245) : Color.FromRgb(37, 40, 48));
        Background = new SolidColorBrush(contrast ? SystemColors.WindowColor
            : dark ? Color.FromArgb(66, 12, 20, 30) : Color.FromArgb(focus ? (byte)78 : (byte)46, 255, 255, 255));
        if (Template?.FindName("border", this) is Border border)
        {
            border.CornerRadius = new CornerRadius(10);
            border.BorderBrush = new SolidColorBrush(!IsEnabled
                ? contrast ? SystemColors.GrayTextColor : dark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0)
                : contrast ? SystemColors.WindowTextColor
                : focus ? dark ? Color.FromRgb(10, 132, 255) : Color.FromRgb(0, 122, 255)
                : dark ? Color.FromArgb(96, 255, 255, 255) : Color.FromArgb(48, 0, 0, 0));
        }
        return true;
    }
}
