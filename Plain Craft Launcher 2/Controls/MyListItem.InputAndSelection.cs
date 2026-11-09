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
    public delegate void ClickEventHandler(object sender, MouseButtonEventArgs e);


    public delegate void LogoClickEventHandler(object sender, MouseButtonEventArgs e);


    public object tag { get; set; }


    public void RefreshColor(object sender, EventArgs e)
    {
        // 菜单虚拟化检测
        if (ContentHandler is not null)
        {
            ContentHandler.Invoke(this, e);
            ContentHandler = null;
        }

        // 判断当前颜色
        string stateNew;
        int time;
        if (isMouseDown && !(Type == CheckType.RadioBox && Checked))
        {
            stateNew = "MouseDown";
            time = 120;
        }
        else if (IsMouseOver && isMouseOverAnimationEnabled)
        {
            stateNew = "MouseOver";
            time = 120;
        }
        else
        {
            stateNew = "Idle";
            time = 180;
        }

        if ((stateLast ?? "") == (stateNew ?? ""))
            return;
        stateLast = stateNew;
        // 触发颜色动画
        if (IsLoaded && ModAnimation.AniControlEnabled == 0) // 防止默认属性变更触发动画
        {
            // 页面进入动画会临时把整个列表项的 RenderTransform 设为 TranslateTransform。
            // 该动画结束前不能把它强转为 ScaleTransform，否则鼠标状态变化会导致启动器崩溃。
            var canAnimateScale = IsScaleAnimationEnabled && RenderTransform is ScaleTransform &&
                                  RectBack.RenderTransform is ScaleTransform;

            // 有动画
            var ani = new List<ModAnimation.AniData>();
            if (IsMouseOver && isMouseOverAnimationEnabled)
            {
                if (buttonStack is not null)
                {
                    ani.Add(ModAnimation.AaOpacity(buttonStack, 1d - buttonStack.Opacity, (int)Math.Round(time * 0.7d),
                        (int)Math.Round(time * 0.3d)));
                    ani.Add(ModAnimation.AaDouble(
                        i => ColumnPaddingRight.Width =
                            new GridLength(Math.Max(0, ColumnPaddingRight.Width.Value + (double)i)),
                        Math.Max(MinPaddingRight, 5 + Buttons.Count() * 25) - ColumnPaddingRight.Width.Value,
                        (int)Math.Round(time * 0.3d), (int)Math.Round(time * 0.7d)));
                }

                ani.AddRange(new[]
                {
                    ModAnimation.AaColor(RectBack, Border.BackgroundProperty,
                        isMouseDown ? "ColorBrush6" : "ColorBrushBg1", time),
                    ModAnimation.AaOpacity(RectBack, 1d - RectBack.Opacity, time,
                        ease: new ModAnimation.AniEaseOutFluent())
                });
                if (canAnimateScale)
                {
                    ani.Add(ModAnimation.AaScaleTransform(RectBack,
                        1d - ((ScaleTransform)RectBack.RenderTransform).ScaleX, (int)Math.Round(time * 1.6d),
                        ease: new ModAnimation.AniEaseOutFluent()));
                    if (isMouseDown)
                        ani.Add(ModAnimation.AaScaleTransform(this, 0.98d - ((ScaleTransform)RenderTransform).ScaleX,
                            (int)Math.Round(time * 0.9d), ease: new ModAnimation.AniEaseOutFluent()));
                    else
                        ani.Add(ModAnimation.AaScaleTransform(this, 1d - ((ScaleTransform)RenderTransform).ScaleX,
                            (int)Math.Round(time * 1.2d), ease: new ModAnimation.AniEaseOutFluent()));
                }
            }
            else
            {
                if (buttonStack is not null)
                {
                    ani.Add(ModAnimation.AaOpacity(buttonStack, -buttonStack.Opacity, (int)Math.Round(time * 0.4d)));
                    ani.Add(ModAnimation.AaDouble(
                        i => ColumnPaddingRight.Width =
                            new GridLength(Math.Max(0, ColumnPaddingRight.Width.Value + (double)i)),
                        MinPaddingRight - ColumnPaddingRight.Width.Value, (int)Math.Round(time * 0.4d)));
                }

                ani.Add(ModAnimation.AaOpacity(RectBack, -RectBack.Opacity, time));
                if (canAnimateScale)
                    ani.AddRange(new[]
                    {
                        ModAnimation.AaColor(RectBack, Border.BackgroundProperty,
                            isMouseDown ? "ColorBrush6" : "ColorBrush7", time),
                        ModAnimation.AaScaleTransform(this, 1d - ((ScaleTransform)RenderTransform).ScaleX, time * 3,
                            ease: new ModAnimation.AniEaseOutFluent()),
                        ModAnimation.AaScaleTransform(RectBack,
                            0.996d - ((ScaleTransform)RectBack.RenderTransform).ScaleX, time,
                            ease: new ModAnimation.AniEaseOutFluent()),
                        ModAnimation.AaScaleTransform(RectBack, -0.246d, 1, after: true)
                    });
            }

            ModAnimation.AniStart(ani, "ListItem Color " + Uuid);
        }
        else
        {
            // 无动画
            if (IsMouseOver && isMouseOverAnimationEnabled)
            {
                if (buttonStack is not null)
                {
                    buttonStack.Opacity = 1d;
                    ColumnPaddingRight.Width = new GridLength(Math.Max(MinPaddingRight, 5 + Buttons.Count() * 25));
                }

                // 由于鼠标已经移入，所以直接实例化 RectBack
                RectBack.Background = (Brush)ThemeManager.AppResources["ColorBrushBg1"];
                RectBack.Opacity = 1d;
                RectBack.RenderTransform = new ScaleTransform(1d, 1d);
                RenderTransform = new ScaleTransform(1d, 1d);
            }
            else
            {
                if (buttonStack is not null)
                {
                    buttonStack.Opacity = 0d;
                    ColumnPaddingRight.Width = new GridLength(MinPaddingRight);
                }

                RenderTransform = new ScaleTransform(1d, 1d);
                if (_RectBack is not null)
                {
                    if (IsScaleAnimationEnabled)
                        RectBack.RenderTransform = new ScaleTransform(0.75d, 0.75d);
                    RectBack.Background = (Brush)ThemeManager.AppResources["ColorBrush7"];
                    RectBack.Opacity = 0d;
                }
            }

            ModAnimation.AniStop("ListItem Color " + Uuid);
        }
    }


    private void MyListItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (Checked)
            SetResourceReference(ForegroundProperty, Height < 40d ? "ColorBrush3" : "ColorBrush2");
        else
            SetResourceReference(ForegroundProperty, "ColorBrush1");
        ColumnPaddingRight.Width = new GridLength(MinPaddingRight);
    }


    public override string ToString()
    {
        return Title;
    }

}
