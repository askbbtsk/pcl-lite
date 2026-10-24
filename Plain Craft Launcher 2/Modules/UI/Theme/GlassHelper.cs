using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.UI.Effects;

namespace PCL;

/// <summary>
/// 液态玻璃材质的挂载器。
/// <para>
/// 只作用于控件自身（药丸形导航、圆形图标按钮等），<b>不会修改界面背景</b>。
/// 由配置项 <c>UiLiquidGlass</c> 统一开关，默认关闭。
/// </para>
/// <para>
/// 实现上<b>不会给控件本身加 Effect</b>：WPF 把带 Effect 的元素先渲染进一张中间位图
/// 再贴回来，控件落在非整数像素上时这张位图会被重采样，边缘那圈抗锯齿像素的透明度
/// 会被改写，看起来就是一圈毛边。所以玻璃是画在控件上方的一层独立覆盖层（Adorner），
/// 控件本体一个像素都不碰，文字和轮廓保持原样。
/// </para>
/// <para>
/// 只挂在真正圆形的控件上（圆角半径 ≥ 8）：药丸形导航、圆形图标按钮。
/// 方角按钮（圆角 3）边缘太长，玻璃会在描边上糊成一条脏亮边，所以直接跳过。
/// </para>
/// </summary>
public static class GlassHelper
{
    /// <summary>
    /// 判定「够圆」的圆角半径下限（像素）。
    /// </summary>
    private const double MinCornerRadius = 8d;

    /// <summary>
    /// 当前挂在界面上的所有玻璃覆盖层，用于拖滑块时批量刷新参数。
    /// </summary>
    private static readonly List<GlassAdorner> Live = [];

    /// <summary>
    /// 挂在控件上的玻璃覆盖层。
    /// </summary>
    private static readonly DependencyProperty AdornerProperty =
        DependencyProperty.RegisterAttached("GlassAdorner", typeof(GlassAdorner), typeof(GlassHelper),
            new PropertyMetadata(null));

    /// <summary>
    /// 当前是否启用液态玻璃。
    /// </summary>
    public static bool IsEnabled => Config.Preference.Theme.LiquidGlassEnabled;

    #region 入口

    /// <summary>给普通按钮应用材质（作用在内层 PanFore 上）。可重复调用。</summary>
    public static void Apply(MyButton? button)
    {
        if (button is null) return;
        ApplyCore(button, button.PanFore);
    }

    /// <summary>给药丸形导航按钮应用材质。可重复调用。</summary>
    public static void Apply(MyRadioButton? radio)
    {
        if (radio is null) return;
        ApplyCore(radio, radio);
    }

    /// <summary>给圆形图标按钮应用材质。可重复调用。</summary>
    public static void Apply(MyIconButton? icon)
    {
        if (icon is null) return;
        ApplyCore(icon, icon);
    }

    #endregion

    #region 核心

    private static void ApplyCore(FrameworkElement owner, Border? target)
    {
        if (target is null) return;

        if (!IsEligible(owner, target))
        {
            Remove(target);
            return;
        }

        if (target.GetValue(AdornerProperty) is GlassAdorner existing)
        {
            existing.Sync();
            return;
        }

        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null) return;

        var adorner = new GlassAdorner(target);
        layer.Add(adorner);
        target.SetValue(AdornerProperty, adorner);

        // 页面切走时把覆盖层摘掉，免得它留在 AdornerLayer 上变成幽灵
        target.Unloaded += OnTargetUnloaded;
    }

    private static void OnTargetUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border target) return;
        target.Unloaded -= OnTargetUnloaded;
        Remove(target);
    }

    private static void Remove(Border target)
    {
        if (target.GetValue(AdornerProperty) is not GlassAdorner adorner) return;
        target.ClearValue(AdornerProperty);
        target.Unloaded -= OnTargetUnloaded;
        adorner.Detach();
        // 覆盖层可能挂在更近的一层 AdornerLayer 上（比如页面自己的 AdornerDecorator）
        (AdornerLayer.GetAdornerLayer(adorner) ?? AdornerLayer.GetAdornerLayer(target))?.Remove(adorner);
    }

    /// <summary>
    /// 用户在设置页拖滑块时，只刷新已经存在的覆盖层，不重走一遍视觉树。
    /// </summary>
    public static void RefreshParams()
    {
        foreach (var adorner in Live.ToArray()) adorner.ApplyParams();
    }

    /// <summary>
    /// 该控件是否适合加玻璃。
    /// </summary>
    private static bool IsEligible(FrameworkElement owner, Border target)
    {
        if (!IsEnabled) return false;

        // 实心强调色按钮（如「启动游戏」）上面加玻璃只会变成一圈糊在描边上的亮边。
        if (owner is MyButton button && button.ColorType != MyButton.ColorState.Normal) return false;

        var w = target.ActualWidth;
        var h = target.ActualHeight;
        // 尺寸还没算出来时先放行，等 Sync 时再补
        if (w > 0 && h > 0 && (w < 18d || h < 18d)) return false;

        return EffectiveRadius(target) >= MinCornerRadius;
    }

    /// <summary>
    /// 控件实际的圆角半径。
    /// <para>
    /// PCL 用 <c>CornerRadius="1000"</c> 表示「尽可能圆」，必须按短边的一半收敛，
    /// 否则传给距离场的半径会大得离谱。
    /// </para>
    /// </summary>
    private static double EffectiveRadius(Border target)
    {
        var radius = target.CornerRadius.TopLeft;
        var w = target.ActualWidth;
        var h = target.ActualHeight;
        if (w <= 0 || h <= 0) return radius;
        return Math.Min(radius, Math.Min(w, h) * 0.5d);
    }

    #endregion

    #region 覆盖层

    /// <summary>
    /// 画在控件上方的玻璃覆盖层。自身没有任何可见内容，颜色全部由着色器按距离场现算。
    /// </summary>
    private sealed class GlassAdorner : Adorner
    {
        private readonly Border _target;
        private readonly Border _overlay;
        private readonly LiquidGlassEffect _effect;

        public GlassAdorner(Border target) : base(target)
        {
            _target = target;
            IsHitTestVisible = false;

            _effect = new LiquidGlassEffect
            {
                Tint = Color.FromArgb(0, 255, 255, 255)
            };

            _overlay = new Border
            {
                // 极低透明度而不是全透明：确保 WPF 一定会把这个元素渲染出来，
                // 着色器才有画布可画（输出会整体覆盖掉这块底色）。
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                IsHitTestVisible = false,
                Effect = _effect
            };

            AddVisualChild(_overlay);
            AddLogicalChild(_overlay);
            Live.Add(this);
            Sync();
        }

        /// <summary>从 AdornerLayer 上摘掉自己。</summary>
        public void Detach()
        {
            Live.Remove(this);
        }

        /// <summary>把控件尺寸与圆角同步给着色器。</summary>
        public void Sync()
        {
            var w = _target.ActualWidth;
            var h = _target.ActualHeight;
            if (w <= 0 || h <= 0) return;

            _effect.BorderSize = new Point(w, h);
            _effect.CornerRadius = EffectiveRadius(_target);
            ApplyParams();
        }

        /// <summary>
        /// 把用户在设置页调好的参数写进着色器。
        /// <para>
        /// 「折射强度」同时决定聚光强度和影响带宽：调大了不光边缘更亮，
        /// 那圈亮光也会往控件内部铺得更深，看起来更像一块有厚度的玻璃。
        /// </para>
        /// </summary>
        public void ApplyParams()
        {
            var refraction = Math.Clamp(Config.Preference.Theme.LiquidGlassRefraction, 0, 100) / 100d;
            var highlight = Math.Clamp(Config.Preference.Theme.LiquidGlassHighlight, 0, 100) / 100d;
            var dispersion = Math.Clamp(Config.Preference.Theme.LiquidGlassDispersion, 0, 100) / 100d;

            _effect.RefractionPower = refraction;
            _effect.Highlight = highlight;
            _effect.Dispersion = dispersion;

            var min = Math.Min(_target.ActualWidth, _target.ActualHeight);
            if (min <= 0) return;
            _effect.EffectWidth = Math.Clamp(min * (0.10d + 0.14d * refraction), 3d, 9d);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _overlay;

        protected override Size MeasureOverride(Size constraint)
        {
            _overlay.Measure(constraint);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _overlay.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }

    #endregion

    #region 批量刷新

    /// <summary>
    /// 配置切换后，刷新所有已打开窗口里的控件。
    /// </summary>
    public static void RefreshAll()
    {
        if (Application.Current is null) return;

        foreach (Window window in Application.Current.Windows)
        {
            try
            {
                Walk(window);
            }
            catch
            {
                // 单个窗口出错不影响其它窗口
            }
        }
    }

    private static void Walk(DependencyObject node)
    {
        switch (node)
        {
            case MyButton button:
                Apply(button);
                return;
            case MyRadioButton radio:
                Apply(radio);
                return;
            case MyIconButton icon:
                Apply(icon);
                return;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
    }

    #endregion
}
