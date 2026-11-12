using System;
using System.Windows;
using System.Windows.Media;

namespace PCL;

public partial class MyTextBox
{
    private bool _tahoeTextBoxApplied;
    private CornerRadius _originalTextBoxCornerRadius;

    private void InitializeTahoeTextBox()
    {
        Loaded += TahoeTextBoxLoaded;
        Unloaded += TahoeTextBoxUnloaded;
    }

    private void TahoeTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        TahoeThemeManager.Changed += TahoeTextBoxThemeChanged;
        RefreshColor();
        RefreshTextColor();
    }

    private void TahoeTextBoxUnloaded(object sender, RoutedEventArgs e)
    {
        TahoeThemeManager.Changed -= TahoeTextBoxThemeChanged;
        ModAnimation.AniStop("MyTextBox Color " + Uuid);
        ModAnimation.AniStop("MyTextBox TextColor " + Uuid);
    }

    private void TahoeTextBoxThemeChanged(object? sender, EventArgs e)
    {
        RefreshColor();
        RefreshTextColor();
    }

    private bool RefreshTahoeTextBoxChrome()
    {
        // Editable combo boxes own the surrounding field surface.
        if (TemplatedParent is MyComboBox) return false;
        if (!TahoeThemeManager.IsEnabled || ThemeControlRegistry.HasTemplate(this))
        {
            if (_tahoeTextBoxApplied)
            {
                SetCurrentValue(CornerRadiusProperty, _originalTextBoxCornerRadius);
                _tahoeTextBoxApplied = false;
            }
            return false;
        }
        if (!_tahoeTextBoxApplied)
        {
            _originalTextBoxCornerRadius = CornerRadius;
            _tahoeTextBoxApplied = true;
        }
        ModAnimation.AniStop("MyTextBox Color " + Uuid);
        ModAnimation.AniStop("MyTextBox TextColor " + Uuid);
        bool contrast = SystemParameters.HighContrast;
        bool dark = ThemeManager.IsDarkMode;
        bool invalid = IsEnabled && !IsValidated && isTextChanged;
        SetCurrentValue(CornerRadiusProperty, new CornerRadius(10));
        Foreground = new SolidColorBrush(contrast
            ? IsEnabled ? SystemColors.WindowTextColor : SystemColors.GrayTextColor
            : !IsEnabled ? dark ? Color.FromRgb(147, 158, 171) : Color.FromRgb(95, 111, 124)
            : dark ? Color.FromRgb(242, 243, 245) : Color.FromRgb(37, 40, 48));
        BorderBrush = new SolidColorBrush(contrast ? SystemColors.WindowTextColor
            : invalid ? dark ? Color.FromRgb(255, 115, 110) : Color.FromRgb(190, 45, 40)
            : IsKeyboardFocusWithin ? dark ? Color.FromRgb(10, 132, 255) : Color.FromRgb(0, 122, 255)
            : dark ? Color.FromArgb(96, 255, 255, 255) : Color.FromArgb(48, 0, 0, 0));
        Background = !HasBackground ? Brushes.Transparent : new SolidColorBrush(contrast ? SystemColors.WindowColor
            : invalid ? dark ? Color.FromArgb(55, 140, 35, 30) : Color.FromArgb(32, 255, 90, 80)
            : dark ? Color.FromArgb(66, 12, 20, 30)
            : Color.FromArgb(IsKeyboardFocusWithin ? (byte)78 : (byte)46, 255, 255, 255));
        return true;
    }
}
