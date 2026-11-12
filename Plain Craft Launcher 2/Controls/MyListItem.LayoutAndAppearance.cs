using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using PCL.Core.UI.Controls.SvgIcon;

namespace PCL;

public partial class MyListItem
{

    public Border RectBack
    {
        get
        {
            if (_RectBack is null)
            {
                var rect = new Border
                {
                    Name = "RectBack",
                    CornerRadius = new CornerRadius(IsScaleAnimationEnabled || Height > 40d ? 6 : 0),
                    RenderTransform = IsScaleAnimationEnabled ? new ScaleTransform(0.8d, 0.8d) : null,
                    RenderTransformOrigin = new Point(0.5d, 0.5d),
                    BorderThickness = new Thickness(ModBase.GetWPFSize(1d)),
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false,
                    Opacity = 0d
                };
                rect.SetResourceReference(Border.BackgroundProperty, "ColorBrush7");
                rect.SetResourceReference(Border.BorderBrushProperty, "ColorBrush6");
                SetColumnSpan(rect, 999);
                SetRowSpan(rect, 999);
                (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Insert(0, rect);
                _RectBack = rect;
                // <!--<Border x:Name = "RectBack" CornerRadius="3" RenderTransformOrigin="0.5,0.5" SnapsToDevicePixels="True" 
                // IsHitTestVisible = "False" Opacity="0" BorderThickness="1" 
                // Grid.ColumnSpan = "4" Background="{DynamicResource ColorBrush7}" BorderBrush="{DynamicResource ColorBrush6}"/>-->
            }

            return _RectBack;
        }
    }



    /// <summary>
    ///     Tags 的存放 StackPanel
    /// </summary>
    public StackPanel PanTags
    {
        get
        {
            if (field is not null)
                return field;
            var newStack = new StackPanel
            {
                IsHitTestVisible = false,
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(3.5d, 0d, -3, 0d)
            };
            SetColumn(newStack, 3);
            SetRow(newStack, 2);
            (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Add(newStack);
            field = newStack;
            return field;
        }
    }


    /// <summary>
    ///     标签，可以传入 String 和 List(Of String)
    /// </summary>
    public object Tags
    {
        set
        {
            var list = new List<string>();
            if (value is string str) list = str.Split("|").ToList();
            if (value is List<string>) list = (List<string>)value;
            PanTags.Children.Clear();
            PanTags.Visibility = list.Any() ? Visibility.Visible : Visibility.Collapsed;
            foreach (var TagText in list)
            {
                var newTag = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(17, 0, 0, 0)),
                    Padding = new Thickness(3d, 1d, 3d, 1d),
                    CornerRadius = new CornerRadius(3d),
                    Margin = new Thickness(0d, 0d, 3d, 0d),
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = false
                };
                var tagTextBlock = new TextBlock
                {
                    Text = TagText,
                    Foreground = new SolidColorBrush(Color.FromRgb(134, 134, 134)),
                    FontSize = 11d
                };
                newTag.Child = tagTextBlock;
                PanTags.Children.Add(newTag);
            }
        }
    }


    // 副文本

    public TextBlock LabInfo
    {
        get
        {
            if (field is null)
            {
                var lab = new TextBlock
                {
                    Name = "LabInfo",
                    SnapsToDevicePixels = false,
                    UseLayoutRounding = false,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Visibility = Visibility.Collapsed,
                    FontSize = 12d,
                    Margin = new Thickness(4d, 0d, 0d, 0d),
                    Opacity = 0.6d
                };
                SetColumn(lab, 4);
                SetRow(lab, 2);
                (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Add(lab);
                field = lab;
                // <TextBlock Grid.Row="2" SnapsToDevicePixels="False" UseLayoutRounding="False" HorizontalAlignment="Left" x:Name = "LabInfo" IsHitTestVisible="False" Grid.Column="2" 
                // TextTrimming = "CharacterEllipsis" Visibility="Collapsed" FontSize="12" Foreground="{StaticResource ColorBrushGray2}" Margin="4,0,0,0" />
            }

            return field;
        }
    }


    // 边距
    public int PaddingLeft
    {
        get => (int)Math.Round(ColumnPaddingLeft.Width.Value);
        set => ColumnPaddingLeft.Width = new GridLength(value);
    }


    // 按钮

    public IEnumerable<MyIconButton> Buttons
    {
        get => field;
        set
        {
            field = value;
            // 没有特殊按钮，移除原 Stack
            if (buttonStack is not null)
            {
                (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Remove(buttonStack);
                buttonStack = null;
            }

            // 添加新 Stack
            switch (value.Count())
            {
                case 0:
                {
                    break;
                }
                // 没有按钮，不添加新的
                case 1:
                {
                    // 只有一个按钮
                    foreach (var Btn in value)
                    {
                        if (Btn.Height.Equals(double.NaN))
                            Btn.Height = 25d;
                        if (Btn.Width.Equals(double.NaN))
                            Btn.Width = 25d;
                        Btn.Opacity = 0d;
                        Btn.Margin = new Thickness(0d, 0d, 5d, 0d);
                        Btn.SnapsToDevicePixels = false;
                        Btn.HorizontalAlignment = HorizontalAlignment.Right;
                        Btn.VerticalAlignment = VerticalAlignment.Center;
                        Btn.UseLayoutRounding = false;
                        SetColumnSpan(Btn, 10);
                        SetRowSpan(Btn, 10);
                        (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Add(Btn);
                        buttonStack = Btn;
                    }

                    break;
                }

                default:
                {
                    // 有复数按钮，使用 StackPanel
                    buttonStack = new StackPanel
                    {
                        Opacity = 0d, Margin = new Thickness(0d, 0d, 5d, 0d), SnapsToDevicePixels = false,
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = false
                    };
                    SetColumnSpan(buttonStack, 10);
                    SetRowSpan(buttonStack, 10);
                    // 构造按钮
                    foreach (var Btn in value)
                    {
                        if (Btn.Height.Equals(double.NaN))
                            Btn.Height = 25d;
                        if (Btn.Width.Equals(double.NaN))
                            Btn.Width = 25d;
                        ((StackPanel)buttonStack).Children.Add(Btn);
                    }

                    (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Add(buttonStack);
                    break;
                }
            }
        }
    }


    // 标题
    public InlineCollection Inlines => LabTitle.Inlines;


    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value.Replace("\r", "").Replace("\n", ""));
    }


    // 字号
    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }


    // 信息
    public string Info
    {
        get => (string)GetValue(InfoProperty);
        set
        {
            if (Info == value)
                return;
            value = value?.Replace("\r", "").Replace("\n", "");
            SetValue(InfoProperty, value);
        }
    }


    private static void OnInfoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MyListItem)d;
        var value = e.NewValue as string;
        control.LabInfo.Text = value;
        control.LabInfo.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }


    // 图片
    public string Logo
    {
        get => (string)GetValue(LogoProperty);
        set
        {
            if (Logo == value)
                return;
            SetValue(LogoProperty, value);
        }
    }


    public string SvgIcon
    {
        get => (string)GetValue(SvgIconProperty);
        set
        {
            if (SvgIcon == value)
                return;
            SetValue(SvgIconProperty, value);
        }
    }


    private static void OnLogoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MyListItem)d;
        control.UpdateLogo(e.NewValue as string);
    }


    private static void OnSvgIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MyListItem)d;
        control.UpdateLogo(control.Logo);
    }


    private bool IsUsingSvgIcon => SvgIconControlHelper.HasSvgIcon(SvgIcon);


    private void UpdateLogo(string logo)
    {
        // 删除旧 Logo
        if (pathLogo is not null)
            (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Remove(pathLogo);
        pathLogo = null;
        _canvasClipHandlerAdded = false; // 旧 Canvas 上的 SizeChanged 钩子随旧控件一起丢了

        // 添加新 Logo
        if (IsUsingSvgIcon)
        {
            pathLogo = new SvgIcon
            {
                Tag = this,
                IsHitTestVisible = LogoClickable,
                Icon = SvgIcon,
                Stretch = Stretch.Uniform,
                RenderTransformOrigin = new Point(0.5d, 0.5d),
                RenderTransform = new ScaleTransform { ScaleX = 1D, ScaleY = 1D },
                SnapsToDevicePixels = false,
                UseLayoutRounding = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            ((SvgIcon)pathLogo).SetBinding(
                Core.UI.Controls.SvgIcon.SvgIcon.IconBrushProperty,
                new Binding("Foreground") { Source = this });
        }
        else if (!string.IsNullOrEmpty(logo))
        {
            if (logo.StartsWithF("http", true))
            {
                // 网络图片
                pathLogo = new MyImage
                {
                    Tag = this,
                    IsHitTestVisible = LogoClickable,
                    Source = logo,
                    RenderTransformOrigin = new Point(0.5d, 0.5d),
                    RenderTransform = new ScaleTransform { ScaleX = LogoScale, ScaleY = LogoScale },
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = false
                };
                RenderOptions.SetBitmapScalingMode(pathLogo, BitmapScalingMode.Linear);
            }
            else if (logo.EndsWithF(".png", true) || logo.EndsWithF(".jpg", true) || logo.EndsWithF(".webp", true))
            {
                // 位图
                pathLogo = new Canvas
                {
                    Tag = this,
                    IsHitTestVisible = LogoClickable,
                    Background = new MyBitmap(logo),
                    RenderTransformOrigin = new Point(0.5d, 0.5d),
                    RenderTransform = new ScaleTransform { ScaleX = LogoScale, ScaleY = LogoScale },
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                if (logo.Contains(ModBase.pathTemp + @"Cache\Skin\Head") ||
                    logo.Contains(ModBase.pathTemp + @"Cache\Cape"))
                    RenderOptions.SetBitmapScalingMode(pathLogo, BitmapScalingMode.NearestNeighbor);
                else
                    RenderOptions.SetBitmapScalingMode(pathLogo, BitmapScalingMode.Linear);
            }
            else
            {
                // 矢量图
                pathLogo = new Path
                {
                    Tag = this,
                    IsHitTestVisible = LogoClickable,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Stretch = Stretch.Uniform,
                    Data = (Geometry)new GeometryConverter().ConvertFromString(logo),
                    RenderTransformOrigin = new Point(0.5d, 0.5d),
                    RenderTransform = new ScaleTransform { ScaleX = LogoScale, ScaleY = LogoScale },
                    SnapsToDevicePixels = false,
                    UseLayoutRounding = false
                };
                pathLogo.SetBinding(Shape.FillProperty, new Binding("Foreground") { Source = this });
            }
        }

        if (pathLogo is not null)
        {
            SetColumn(pathLogo, 2);
            SetRowSpan(pathLogo, 4);
            OnSizeChanged(); // 设置边距
            (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Add(pathLogo);
            ApplyLogoCornerRadius(); // 圆角可能在 Logo 之前就设好了，这里补一次
            // 图标的点击事件
            if (LogoClickable)
            {
                pathLogo.MouseLeave += (sender, e) => isLogoDown = false;
                pathLogo.MouseLeftButtonDown += (sender, e) => isLogoDown = true;
                pathLogo.MouseLeftButtonUp += (sender, e) =>
                {
                    if (isLogoDown)
                    {
                        isLogoDown = false;
                        LogoClick?.Invoke(((FrameworkElement)sender).Tag, e);
                    }
                };
            }
        }

        // 改变行距
        var hasLogo = IsUsingSvgIcon || !string.IsNullOrEmpty(logo);
        ColumnLogo.Width = new GridLength((hasLogo ? 34 : 0) + (Height < 40d ? 0 : 4));
    }


    // 勾选选项
    public enum CheckType
    {
        None,
        Clickable,
        RadioBox,
        CheckBox
    }


    public CheckType Type
    {
        get => _Type;
        set
        {
            if (_Type == value)
                return;
            _Type = value;
            // 切换左栏大小
            ColumnCheck.Width =
                new GridLength(_Type == CheckType.None || _Type == CheckType.Clickable ? Height < 40d ? 4 : 2 : 6);
            // 切换竖条控件
            if (_Type == CheckType.None || _Type == CheckType.Clickable)
            {
                // 移除竖条控件
                if (rectCheck is not null)
                {
                    (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Remove(rectCheck);
                    rectCheck = null;
                }

                SetChecked(false, false, false);
            }
            // 添加竖条控件
            else if (rectCheck is null)
            {
                rectCheck = new Border
                {
                    Width = 5d,
                    Height = Checked ? double.NaN : 0d,
                    CornerRadius = new CornerRadius(2d, 2d, 2d, 2d),
                    VerticalAlignment = Checked ? VerticalAlignment.Stretch : VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    UseLayoutRounding = false,
                    SnapsToDevicePixels = false,
                    Margin = Checked ? new Thickness(-1, 6d, 0d, 6d) : new Thickness(-1, 0d, 0d, 0d)
                };
                rectCheck.SetResourceReference(Border.BackgroundProperty, "ColorBrush3");
                SetRowSpan(rectCheck, 4);
                (ThemeControlRegistry.ContentGrid(this) ?? this).Children.Add(rectCheck);
            }
        }
    }


    // 适应尺寸
    private void OnSizeChanged()
    {
        var logo = Logo;
        var hasLogo = IsUsingSvgIcon || !string.IsNullOrEmpty(logo);
        ColumnCheck.Width = new GridLength(_Type is CheckType.None or CheckType.Clickable
            ? Height < 40d
                ? 4
                : 2
            : 6);
        ColumnLogo.Width = new GridLength((hasLogo ? 34 : 0) + (Height < 40d ? 0 : 4));

        if (pathLogo is not null)
        {
            if (!IsUsingSvgIcon && (logo.EndsWithF(".png", true) ||
                                    logo.EndsWithF(".jpg", true) ||
                                    logo.EndsWithF(".webp", true)))
                pathLogo.Margin = new Thickness(4d, 5d, 3d, 5d);
            else
                pathLogo.Margin = new Thickness(Height < 40d ? 6 : 8, 8d, Height < 40d ? 4 : 6, 8d);
        }

        LabTitle.Margin = new Thickness(4d, 0d, 0d, Height < 40d ? 0 : 2);
    }


    public bool Checked
    {
        get => _Checked;
        set => SetChecked(value, false, value != _Checked); // 仅在值发生变化时触发动画 (#4596)
    }

}
