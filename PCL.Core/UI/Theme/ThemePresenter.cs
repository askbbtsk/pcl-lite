using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCL.Core.UI.Theme;

/// <summary>
/// Stable visual-only template host. The enclosing PCL control owns input and
/// functionality; templates must preserve Content and cannot replace that owner.
/// </summary>
public sealed class ThemePresenter : ContentControl
{
    public ThemePresenter()
    {
        Focusable = false;
        IsHitTestVisible = false;
        SetResourceReference(ForegroundProperty, "ColorBrush1");
        SetResourceReference(FontFamilyProperty, "LaunchFontFamily");
    }

    public static readonly DependencyProperty TitleProperty = Register(nameof(Title), "");
    public static readonly DependencyProperty DescriptionProperty = Register(nameof(Description), "");
    public static readonly DependencyProperty RoleProperty = Register(nameof(Role), "");
    public static readonly DependencyProperty IconDataProperty =
        DependencyProperty.Register(nameof(IconData), typeof(Geometry), typeof(ThemePresenter));
    public static readonly DependencyProperty IsPressedProperty = Register(nameof(IsPressed), false);
    public static readonly DependencyProperty IsSelectedProperty = Register(nameof(IsSelected), false);
    public static readonly DependencyProperty IsCheckedProperty = Register(nameof(IsChecked), false);
    public static readonly DependencyProperty IsExpandedProperty = Register(nameof(IsExpanded), false);
    public static readonly DependencyProperty IsInvalidProperty = Register(nameof(IsInvalid), false);
    public static readonly DependencyProperty IsHighlightedProperty = Register(nameof(IsHighlighted), false);
    public static readonly DependencyProperty IsDangerProperty = Register(nameof(IsDanger), false);
    public static readonly DependencyProperty IsGlassProperty = Register(nameof(IsGlass), false);
    public static readonly DependencyProperty HasFocusProperty = Register(nameof(HasFocus), false);
    public static readonly DependencyProperty IsHoveredProperty = Register(nameof(IsHovered), false);
    public static readonly DependencyProperty ProgressProperty = Register(nameof(Progress), 0d);
    public static readonly DependencyProperty CornerRadiusProperty = Register(nameof(CornerRadius), new CornerRadius(3));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Role { get => (string)GetValue(RoleProperty); set => SetValue(RoleProperty, value); }
    public Geometry? IconData { get => (Geometry?)GetValue(IconDataProperty); set => SetValue(IconDataProperty, value); }
    public bool IsPressed { get => (bool)GetValue(IsPressedProperty); set => SetValue(IsPressedProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public bool IsInvalid { get => (bool)GetValue(IsInvalidProperty); set => SetValue(IsInvalidProperty, value); }
    public bool IsHighlighted { get => (bool)GetValue(IsHighlightedProperty); set => SetValue(IsHighlightedProperty, value); }
    public bool IsDanger { get => (bool)GetValue(IsDangerProperty); set => SetValue(IsDangerProperty, value); }
    public bool IsGlass { get => (bool)GetValue(IsGlassProperty); set => SetValue(IsGlassProperty, value); }
    public bool HasFocus { get => (bool)GetValue(HasFocusProperty); set => SetValue(HasFocusProperty, value); }
    public bool IsHovered { get => (bool)GetValue(IsHoveredProperty); set => SetValue(IsHoveredProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    private static DependencyProperty Register<T>(string name, T value) =>
        DependencyProperty.Register(name, typeof(T), typeof(ThemePresenter), new FrameworkPropertyMetadata(value));
}
