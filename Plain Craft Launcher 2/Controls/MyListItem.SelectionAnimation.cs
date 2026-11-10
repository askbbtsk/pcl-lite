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

    /// <summary>
    ///     手动设置 Checked 属性。
    /// </summary>
    /// <param name="value">新的 Checked 属性。</param>
    /// <param name="user">是否由用户引发。</param>
    /// <param name="anime">是否执行动画。</param>
    public void SetChecked(bool value, bool user, bool anime)
    {
        try
        {
            // 自定义属性基础

            var ChangedEventArgs = new ModBase.RouteEventArgs(user);
            var rawValue = _Checked;
            if (Type == CheckType.RadioBox)
            {
                if (IsInitialized && value != _Checked)
                {
                    _Checked = value;
                    Changed?.Invoke(this, ChangedEventArgs);
                    if (ChangedEventArgs.handled)
                    {
                        _Checked = rawValue;
                        return;
                    }
                }

                _Checked = value;
            }
            else
            {
                if (value == _Checked)
                    return;
                _Checked = value;
                if (IsInitialized)
                {
                    Changed?.Invoke(this, ChangedEventArgs);
                    if (ChangedEventArgs.handled)
                    {
                        _Checked = rawValue;
                        return;
                    }
                }
            }

            if (value)
            {
                var checkEventArgs = new ModBase.RouteEventArgs(user);
                Check?.Invoke(this, checkEventArgs);
                if (checkEventArgs.handled)
                    return;
            }

            // 保证只有一个单选 ListItem 选中

            if (Type == CheckType.RadioBox)
            {
                if (Parent is null)
                    return;
                var radioboxList = new List<MyListItem>();
                var checkedCount = 0;
                // 收集控件列表与选中个数
                foreach (var ControlRaw in ((Panel)Parent).Children)
                {
                    var control = MyVirtualizingElement.TryInit((FrameworkElement)ControlRaw);
                    if (control is MyListItem listItem && listItem.Type == CheckType.RadioBox)
                    {
                        radioboxList.Add(listItem);
                        if (listItem.Checked)
                            checkedCount += 1;
                    }
                }

                // 判断选中情况
                switch (checkedCount)
                {
                    case 0:
                    {
                        // 没有任何单选框被选中，选择第一个
                        radioboxList[0].Checked = true;
                        break;
                    }
                    case var @case when @case > 1:
                    {
                        // 选中项目多于 1 个
                        if (Checked)
                        {
                            // 如果本控件选中，则取消其他所有控件的选中
                            foreach (var Control in radioboxList)
                                if (Control.Checked && !Control.Equals(this))
                                    Control.Checked = false;
                        }
                        else
                        {
                            // 如果本控件未选中，则只保留第一个选中的控件
                            var firstChecked = false;
                            foreach (var Control in radioboxList)
                                if (Control.Checked)
                                {
                                    if (firstChecked)
                                        Control.Checked = false; // 修改 Checked 会自动触发 Change 事件，所以不用额外触发
                                    else
                                        firstChecked = true;
                                }
                        }

                        break;
                    }
                }
            }

            if (RefreshTahoeNavigation(anime)) return;

            var customHeight = 20d; // 修改左侧神秘动画条的高度

            if (IsLoaded && ModAnimation.AniControlEnabled == 0 && anime) // 防止默认属性变更触发动画
            {
                var anim = new List<ModAnimation.AniData>();
                if (Checked)
                {
                    // 由无变有
                    if (rectCheck is not null)
                    {
                        // 统一静态属性：固定高度、居中对齐、无额外边距
                        rectCheck.Height = customHeight;
                        rectCheck.VerticalAlignment = VerticalAlignment.Center;
                        rectCheck.Margin = new Thickness(-1, 0d, 0d, 0d);
                        rectCheck.Opacity = 1d;

                        // 初始化缩放中心为正中心
                        var scale = new ScaleTransform(1d, 0d);
                        rectCheck.RenderTransformOrigin = new Point(0.5d, 0.5d);
                        rectCheck.RenderTransform = scale;

                        // 动画：让 ScaleY 从 0 弹性放大到 1
                        anim.Add(ModAnimation.AaDouble(
                            i => scale.ScaleY = Math.Max(0d, scale.ScaleY + (double)i),
                            1d - scale.ScaleY,
                            300,
                            ease: new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)
                        ));
                    }

                    anim.Add(ModAnimation.AaColor(this, ForegroundProperty,
                        Height < 40d ? "ColorBrush3" : "ColorBrush2", 200));
                }
                else
                {
                    // 由有变无
                    if (rectCheck is not null)
                    {
                        if (rectCheck.RenderTransform is not ScaleTransform)
                        {
                            rectCheck.RenderTransformOrigin = new Point(0.5d, 0.5d);
                            rectCheck.RenderTransform = new ScaleTransform(1d, 1d);
                        }

                        var scale = (ScaleTransform)rectCheck.RenderTransform;

                        // 动画：让 ScaleY 从当前值缩减到 0
                        anim.Add(ModAnimation.AaDouble(
                            i => scale.ScaleY = Math.Max(0d, scale.ScaleY + (double)i),
                            -scale.ScaleY,
                            120,
                            ease: new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)
                        ));
                        anim.Add(ModAnimation.AaOpacity(rectCheck, -rectCheck.Opacity, 70, 40));
                    }

                    anim.Add(ModAnimation.AaColor(this, ForegroundProperty, "ColorBrush1", 120));
                }

                ModAnimation.AniStart(anim, "MyListItem Checked " + Uuid);
            }
            else
            {
                // 不使用动画
                ModAnimation.AniStop("MyListItem Checked " + Uuid);
                if (Checked)
                {
                    if (rectCheck is not null)
                    {
                        rectCheck.Height = customHeight; // 应用自定义固定高度
                        rectCheck.Margin = new Thickness(-1, 0d, 0d, 0d);
                        rectCheck.Opacity = 1d;
                        rectCheck.VerticalAlignment = VerticalAlignment.Center; // 居中
                        rectCheck.RenderTransform = null; // 清除缩放
                    }

                    SetResourceReference(ForegroundProperty, Height < 40d ? "ColorBrush3" : "ColorBrush2");
                }
                else
                {
                    if (rectCheck is not null)
                    {
                        rectCheck.Height = 0d;
                        rectCheck.Margin = new Thickness(-1, 0d, 0d, 0d);
                        rectCheck.Opacity = 0d;
                        rectCheck.VerticalAlignment = VerticalAlignment.Center;
                        rectCheck.RenderTransform = null;
                    }

                    SetResourceReference(ForegroundProperty, "ColorBrush1");
                }
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "设置 Checked 失败");
        }
    }


    // 前景色绑定
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }


    // 菜单与按钮绑定
    public Action<MyListItem, EventArgs> ContentHandler { get; set; }

    
    private bool IsLogoCornerRadiusEnabled() => 
        LogoCornerRadius is { TopLeft: >= 0, TopRight: >= 0, BottomLeft: >= 0, BottomRight: >= 0 };


    private void UpdateCanvasClip(Canvas c)
    {
        if (c is { ActualWidth: > 0, ActualHeight: > 0 })
        {
            var r = LogoCornerRadius;
            double radius = Math.Max(Math.Max(r.TopLeft, r.TopRight), Math.Max(r.BottomLeft, r.BottomRight));
            c.Clip = new RectangleGeometry(new Rect(0, 0, c.ActualWidth, c.ActualHeight), radius, radius);
        }
    }


    private void ApplyLogoCornerRadius()
    {
        if (pathLogo is null || !IsLogoCornerRadiusEnabled()) return;

        switch (pathLogo)
        {
            case MyImage myImage:
                myImage.CornerRadius = LogoCornerRadius;
                break;
            case Canvas canvas when !_canvasClipHandlerAdded && canvas is { ActualWidth: 0, ActualHeight: 0 }:
                UpdateCanvasClip(canvas);
                canvas.SizeChanged += OnCanvasLogoSizeChanged;
                _canvasClipHandlerAdded = true;
                break;
            case Canvas canvas:
                UpdateCanvasClip(canvas);
                break;
        }
    }


    private void OnCanvasLogoSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Canvas { ActualWidth: > 0, ActualHeight: > 0 } canvas) return;
    
        UpdateCanvasClip(canvas);
        canvas.SizeChanged -= OnCanvasLogoSizeChanged;
        _canvasClipHandlerAdded = false;
    }



    // 触发点击事件
    private void Button_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!isMouseDown)
            return;
        Click?.Invoke(sender, e);
        if (e.Handled)
            return;
        // 触发自定义事件
        var dependencyObject = (DependencyObject)sender;
        if (CustomEventService.GetEventType(dependencyObject) != EventType.None)
        {
            ModMain.RaiseCustomEvent(this);
            e.Handled = true;
        }

        if (e.Handled)
            return;
        // 实际的单击处理
        switch (Type)
        {
            case CheckType.Clickable:
            {
                ModBase.Log("[Control] 按下单击列表项：" + Title);
                break;
            }
            case CheckType.RadioBox:
            {
                ModBase.Log("[Control] 按下单选列表项：" + Title);
                if (!Checked)
                    SetChecked(true, true, true);
                break;
            }
            case CheckType.CheckBox:
            {
                ModBase.Log("[Control] 按下复选列表项（" + !Checked + "）：" + Title);
                SetChecked(!Checked, true, true);
                break;
            }
        }
    }


    private void Button_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseDirectlyOver && !(Type == CheckType.None))
        {
            isMouseDown = true;
            if (buttonStack is not null)
                buttonStack.IsHitTestVisible = false;
        }
    }


    private void Button_MouseLeave(object sender, object e)
    {
        isMouseDown = false;
        if (buttonStack is not null)
            buttonStack.IsHitTestVisible = true;
    }

}
