using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCL;

public partial class MyListItem
{
    private bool _isTahoeNavigationItem;
    private bool _tahoeNavigationApplied;
    private Border? _tahoeNavigationChrome;
    private Visibility _originalNavigationHoverVisibility;
    private Visibility _originalNavigationCheckVisibility;
    private GridLength _originalNavigationPadding;
    private FontWeight _originalNavigationFontWeight;
    private Thickness? _originalNavigationActionMargin;

    private bool IsNavigationItem()
    {
        if (Type != CheckType.RadioBox || IsScaleAnimationEnabled || Height > 40) return false;
        DependencyObject? parent = this;
        while ((parent = VisualTreeHelper.GetParent(parent)) != null)
        {
            // These pages contain application navigation. Directory and running-instance
            // selectors also inherit MyPageLeft, so deliberately do not include them.
            if (parent is MyPageLeft)
                return parent is PageSetupLeft or PageDownloadLeft or PageToolsLeft or PagePluginsLeft
                    or PageInstanceLeft or PageInstanceSavesLeft or PageBedrockLeft or PageLunarLeft;
        }
        return false;
    }

    private void TahoeNavigationLoaded(object sender, RoutedEventArgs e)
    {
        _isTahoeNavigationItem = IsNavigationItem();
        if (_isTahoeNavigationItem) TahoeThemeManager.Changed += TahoeNavigationThemeChanged;
        RefreshTahoeNavigation(false);
    }

    private void TahoeNavigationUnloaded(object sender, RoutedEventArgs e)
    {
        TahoeThemeManager.Changed -= TahoeNavigationThemeChanged;
        _isTahoeNavigationItem = false;
        if (_tahoeNavigationApplied)
        {
            ModAnimation.AniStop("ListItem Color " + Uuid);
            ModAnimation.AniStop("MyListItem Checked " + Uuid);
        }
    }

    private void TahoeNavigationThemeChanged(object? sender, EventArgs e)
    {
        if (!RefreshTahoeNavigation(false)) RefreshColor(this, EventArgs.Empty);
    }

    private bool RefreshTahoeNavigation(bool animate)
    {
        if (!_isTahoeNavigationItem || !TahoeThemeManager.IsEnabled || ThemeControlRegistry.HasTemplate(this))
        {
            RestoreNavigationChrome();
            return false;
        }

        if (!_tahoeNavigationApplied)
        {
            ModAnimation.AniStop("ListItem Color " + Uuid);
            _originalNavigationHoverVisibility = RectBack.Visibility;
            _originalNavigationCheckVisibility = rectCheck?.Visibility ?? Visibility.Visible;
            _originalNavigationPadding = ColumnPaddingLeft.Width;
            _originalNavigationFontWeight = LabTitle.FontWeight;
            _tahoeNavigationChrome ??= new Border
            {
                Margin = new Thickness(16, 2, 14, 2),
                CornerRadius = new CornerRadius(12),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(.5),
                IsHitTestVisible = false,
                UseLayoutRounding = false,
                SnapsToDevicePixels = false
            };
            if (!(ThemeControlRegistry.ContentGrid(this) ?? this).Children.Contains(_tahoeNavigationChrome))
            {
                SetColumnSpan(_tahoeNavigationChrome, 999);
                SetRowSpan(_tahoeNavigationChrome, 999);
                (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Insert(0, _tahoeNavigationChrome);
            }
            RectBack.Visibility = Visibility.Collapsed;
            if (rectCheck != null) rectCheck.Visibility = Visibility.Collapsed;
            ColumnPaddingLeft.Width = new GridLength(_originalNavigationPadding.Value + 6);
            _tahoeNavigationChrome.Visibility = Visibility.Visible;
            _tahoeNavigationApplied = true;
        }

        ModAnimation.AniStop("MyListItem Checked " + Uuid);
        bool dark = ThemeManager.IsDarkMode;
        bool contrast = SystemParameters.HighContrast;
        bool hover = IsMouseOver && isMouseOverAnimationEnabled;
        var palette = LiquidGlassCore.CurrentPalette;
        var color = contrast
            ? Checked ? SystemColors.HighlightColor : SystemColors.WindowColor
            : Checked ? palette.NavCheckedFill
            : hover ? (isMouseDown ? palette.NavPressFill : palette.NavHoverFill)
            : Colors.Transparent;
        SetCurrentValue(ForegroundProperty, new SolidColorBrush(contrast
            ? Checked ? SystemColors.HighlightTextColor : SystemColors.WindowTextColor
            : Checked ? palette.ActionText : palette.Text));
        LabTitle.SetCurrentValue(TextBlock.FontWeightProperty, Checked ? FontWeights.SemiBold : FontWeights.Normal);
        _tahoeNavigationChrome!.BorderBrush = PCL.Core.UI.LiquidGlassMaterial.NavigationPillBorder(dark, Checked, contrast);

        if (buttonStack != null)
        {
            _originalNavigationActionMargin ??= buttonStack.Margin;
            var original = _originalNavigationActionMargin.Value;
            buttonStack.Margin = new Thickness(original.Left, original.Top,
                PCL.Core.UI.LiquidGlassInteraction.NavigationActionInset(original.Right, true), original.Bottom);
        }
        double padding = hover && buttonStack != null
            ? Math.Max(MinPaddingRight, 18 + Buttons.Count() * 25) : MinPaddingRight;
        if (animate && IsLoaded && SystemParameters.ClientAreaAnimation && ModAnimation.AniControlEnabled == 0)
        {
            var animations = new List<ModAnimation.AniData>
            {
                ModAnimation.AaColor(_tahoeNavigationChrome, Border.BackgroundProperty,
                    new ModBase.MyColor(new SolidColorBrush(color)) - _tahoeNavigationChrome.Background, 140)
            };
            if (buttonStack != null)
            {
                animations.Add(ModAnimation.AaOpacity(buttonStack, (hover ? 1d : 0d) - buttonStack.Opacity, 120));
                animations.Add(ModAnimation.AaDouble(delta => ColumnPaddingRight.Width = new GridLength(
                    Math.Max(0, ColumnPaddingRight.Width.Value + (double)delta)), padding - ColumnPaddingRight.Width.Value, 120));
            }
            ModAnimation.AniStart(animations, "ListItem Color " + Uuid);
        }
        else
        {
            ModAnimation.AniStop("ListItem Color " + Uuid);
            _tahoeNavigationChrome.Background = new SolidColorBrush(color);
            if (buttonStack != null) buttonStack.Opacity = hover ? 1 : 0;
            ColumnPaddingRight.Width = new GridLength(padding);
        }
        return true;
    }

    private void RestoreNavigationChrome()
    {
        if (!_tahoeNavigationApplied) return;
        ModAnimation.AniStop("ListItem Color " + Uuid);
        ModAnimation.AniStop("MyListItem Checked " + Uuid);
        _tahoeNavigationChrome!.Visibility = Visibility.Collapsed;
        if (buttonStack != null && _originalNavigationActionMargin is { } actionMargin)
            buttonStack.Margin = actionMargin;
        _originalNavigationActionMargin = null;
        RectBack.Visibility = _originalNavigationHoverVisibility;
        ColumnPaddingLeft.Width = _originalNavigationPadding;
        LabTitle.SetCurrentValue(TextBlock.FontWeightProperty, _originalNavigationFontWeight);
        if (rectCheck != null)
        {
            rectCheck.Visibility = _originalNavigationCheckVisibility;
            rectCheck.Height = Checked ? 20 : 0;
            rectCheck.Opacity = Checked ? 1 : 0;
            rectCheck.Margin = new Thickness(-1, 0, 0, 0);
            rectCheck.VerticalAlignment = VerticalAlignment.Center;
            rectCheck.RenderTransform = null;
        }
        SetResourceReference(ForegroundProperty, Checked ? Height < 40 ? "ColorBrush3" : "ColorBrush2" : "ColorBrush1");
        stateLast = null;
        _tahoeNavigationApplied = false;
    }
}
