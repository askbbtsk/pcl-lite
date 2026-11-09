using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Network;

using PCL.Core.App.Localization;
namespace PCL;

public static partial class ModAnimation
{

    /// <summary>
    ///     开始动画执行。
    /// </summary>
    public static void AniStart()
    {
        // 初始化计时器
        aniLastTick = TimeUtils.GetTimeTick();
        aniFPSTimer = aniLastTick;
        aniRunning = true; // 标记动画执行开始

        var minFrameGap = 1000d / (Config.System.AnimationFpsLimit + 1) / 2;


        ModBase.RunInNewThread(() =>
        {
            try
            {
                ModBase.Log("[Animation] 动画线程开始");
                while (true)
                {
                    // 两帧之间的间隔时间
                    var deltaTime =
                        (long)Math.Round(ModBase.MathClamp(TimeUtils.GetTimeTick() - aniLastTick, 0, 100000));
                    if (deltaTime < minFrameGap)
                    {
                        // 限制 FPS
                        Thread.Sleep(1);
                        continue;
                    }

                    aniLastTick = TimeUtils.GetTimeTick();
                    // 记录 FPS
                    if (ModBase.modeDebug)
                    {
                        if (ModBase.MathClamp(aniLastTick - aniFPSTimer, 0d, 100000d) >= 500d)
                        {
                            aniFPS = aniFPSCounter;
                            aniFPSCounter = 0;
                            aniFPSTimer = aniLastTick;
                        }

                        aniFPSCounter += 2;
                    }

                    // 执行动画
                    ModBase.RunInUiWait(() =>
                    {
                        aniCount = 0;
                        AniTimer((int)Math.Round(deltaTime * aniSpeed));
                        // #If DEBUG Then
                        // FrmMain.Title = "F " & AniFPS & ", A " & AniCount & ", R " & NetManage.FileRemain
                        // #Else
                        // If ModeDebug Then FrmMain.Title = "FPS " & AniFPS & ", 动画 " & AniCount & ", 下载中 " & NetManage.FileRemain
                        // #End If
                        if (RandomUtils.NextInt(0, 64 * (ModBase.modeDebug ? 5 : 30)) == 0 &&
                            ((aniFPS < 62 && aniFPS > 0) || aniCount > 4 || ModNet.NetManager.FileRemain != 0))
                            ModBase.Log("[Report] FPS " + aniFPS + ", 动画 " + aniCount + ", 下载中 " +
                                        ModNet.NetManager.FileRemain + "（" +
                                        ModBase.GetString(ModNet.NetManager.Speed) + "/s）");
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "动画帧执行失败",
                    ModBase.LogLevel.Critical,
                    userSummary: Lang.Text("Application.Animation.Error.OperationFailed"));
            }
        }, "Animation", ThreadPriority.AboveNormal);
    }


    /// <summary>
    ///     动画定时器事件。
    /// </summary>
    public static void AniTimer(int deltaTick)
    {
        try
        {
            if (deltaTick / aniSpeed > 100d)
                ModBase.Log("[Animation] 两个动画帧间隔 " + deltaTick + " ms", ModBase.LogLevel.Developer);
            var i = -1;
            // 循环每个动画组
            while (i + 1 < aniGroups.Count)
            {
                i += 1;
                // 初始化
                var entry = aniGroups.Values.ElementAtOrDefault(i);
                if (entry.startTick > aniLastTick)
                    continue; // 跳过本刻之后开始的动画
                var canRemoveAfter = true; // 是否应该去除“之后”标记
                var ii = 0;

                // 循环每个动画
                while (ii < entry.data.Count)
                {
                    var anim = entry.data[ii];
                    // 执行种类
                    if (!anim.isAfter) // 之前
                    {
                        canRemoveAfter = false; // 取消“之后”标记 
                        // 增加执行时间
                        anim.timeFinished += deltaTick;
                        // 执行动画
                        if (anim.timeFinished > 0)
                        {
                            anim = AniRun(anim);
                            aniCount += 1;
                        }

                        // 如果当前动画已执行完毕
                        if (anim.timeFinished >= anim.timeTotal)
                        {
                            // 如果是去向颜色资源的动画，设置引用
                            if (anim.typeMain == AniType.Color &&
                                !string.Equals(((dynamic)anim.obj)[2] as string, "", StringComparison.Ordinal))
                                ((dynamic)anim.obj)[0]
                                    .SetResourceReference(((dynamic)anim.obj)[1], ((dynamic)anim.obj)[2]);
                            // 删除
                            entry.data.RemoveAt(ii);
                            goto NextAni;
                        }

                        entry.data[ii] = anim;
                    }
                    else if (canRemoveAfter) // 之后
                    {
                        // 之后改为之前
                        canRemoveAfter = false;
                        anim.isAfter = false;
                        entry.data[ii] = anim;
                        // 重新循环该动画
                        goto NextAni;
                    }
                    else
                    {
                        // 不能去除该“之后”标记，结束该动画组
                        break;
                    }

                    ii += 1;
                    NextAni: ;
                }

                // 如果当前动画组都执行完毕则删除
                if (!entry.data.Any())
                {
                    // 为了避免新添加的动画影响顺序，不能 RemoveAt(i)
                    // 为了允许动画在执行中添加同名动画组，不能按名字移除
                    for (int current = 0, loopTo = aniGroups.Count - 1; current <= loopTo; current++)
                        if (aniGroups.ElementAt(current).Value.Uuid == entry.Uuid)
                        {
                            aniGroups.Remove(aniGroups.ElementAt(current).Key, out _);
                            break;
                        }

                    i -= 1;
                }
            }
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "动画刻执行失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Application.Animation.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     执行一个动画。
    /// </summary>
    /// <param name="ani">执行的动画对象。</param>
    private static AniData AniRun(AniData ani)
    {
        try
        {
            switch (ani.typeMain)
            {
                case AniType.Number:
                {
                    var delta = ModBase.MathPercent(0d, (double)ani.value,
                        ani.ease.GetDelta(ani.timeFinished / (double)ani.timeTotal, ani.timePercent));
                    if (delta != 0d)
                        switch (ani.typeSub)
                        {
                            case AniTypeSub.X:
                            {
                                ModBase.DeltaLeft((FrameworkElement)ani.obj, delta);
                                break;
                            }
                            case AniTypeSub.Y:
                            {
                                ModBase.DeltaTop((FrameworkElement)ani.obj, delta);
                                break;
                            }
                            case AniTypeSub.Opacity:
                            {
                                ((dynamic)ani.obj).Opacity = ModBase.MathClamp(
                                    Convert.ToDouble(((dynamic)ani.obj).Opacity) + delta, 0d, 1d);
                                break;
                            }
                            case AniTypeSub.Width:
                            {
                                var obj = (FrameworkElement)ani.obj;
                                obj.Width = Math.Max((double.IsNaN(obj.Width) ? obj.ActualWidth : obj.Width) + delta,
                                    0d);
                                break;
                            }
                            case AniTypeSub.Height:
                            {
                                var obj = (FrameworkElement)ani.obj;
                                obj.Height =
                                    Math.Max((double.IsNaN(obj.Height) ? obj.ActualHeight : obj.Height) + delta, 0d);
                                break;
                            }
                            case AniTypeSub.Value:
                            {
                                ((dynamic)ani.obj).Value += delta;
                                break;
                            }
                            case AniTypeSub.Radius:
                            {
                                ((dynamic)ani.obj).Radius += delta;
                                break;
                            }
                            case AniTypeSub.StrokeThickness:
                            {
                                ((dynamic)ani.obj).StrokeThickness =
                                    Math.Max(Convert.ToDouble(((dynamic)ani.obj).StrokeThickness) + delta, 0);
                                break;
                            }
                            case AniTypeSub.BorderThickness:
                            {
                                ((dynamic)ani.obj).BorderThickness =
                                    new Thickness(((Thickness)((dynamic)ani.obj).BorderThickness).Bottom + delta);
                                break;
                            }
                            case AniTypeSub.TranslateX:
                            {
                                if (((dynamic)ani.obj).RenderTransform is null ||
                                    !(((dynamic)ani.obj).RenderTransform is TranslateTransform))
                                    ((dynamic)ani.obj).RenderTransform = new TranslateTransform(0d, 0d);
                                ((TranslateTransform)((dynamic)ani.obj).RenderTransform).X += delta;
                                break;
                            }
                            case AniTypeSub.TranslateY:
                            {
                                if (((dynamic)ani.obj).RenderTransform is null ||
                                    !(((dynamic)ani.obj).RenderTransform is TranslateTransform))
                                    ((dynamic)ani.obj).RenderTransform = new TranslateTransform(0d, 0d);
                                ((TranslateTransform)((dynamic)ani.obj).RenderTransform).Y += delta;
                                break;
                            }
                            case AniTypeSub.Double:
                            {
                                ((dynamic)ani.obj)[0].SetValue(((dynamic)ani.obj)[1],
                                    Convert.ToDouble(((dynamic)ani.obj)[0].GetValue(((dynamic)ani.obj)[1])) + delta);
                                break;
                            }
                            case AniTypeSub.DoubleParam:
                            {
                                ((ParameterizedThreadStart)ani.obj)(delta);
                                break;
                            }
                            case AniTypeSub.GridLengthWidth:
                            {
                                ((dynamic)ani.obj).Width =
                                    new GridLength(
                                        Convert.ToDouble(
                                            Math.Max(Convert.ToDouble(((dynamic)ani.obj).Width.Value) + delta, 0)),
                                        GridUnitType.Star);
                                break;
                            }
                        }

                    break;
                }

                case AniType.Color:
                {
                    // 利用 Last 记录了余下的小数值
                    var delta = ModBase.MathPercent(new ModBase.MyColor(0d, 0d, 0d, 0d), (ModBase.MyColor)ani.value,
                                    ani.ease.GetDelta(ani.timeFinished / (double)ani.timeTotal, ani.timePercent)) +
                                (ModBase.MyColor)ani.valueLast;
                    var obj = (FrameworkElement)((dynamic)ani.obj)[0];
                    var prop = (DependencyProperty)((dynamic)ani.obj)[1];
                    var newColor = new ModBase.MyColor(obj.GetValue(prop)) + delta;
                    obj.SetValue(prop, prop.PropertyType.Name == "Color" ? (Color)newColor : (SolidColorBrush)newColor);
                    ani.valueLast = newColor - new ModBase.MyColor(obj.GetValue(prop));
                    break;
                }

                case AniType.Scale:
                {
                    var obj = (FrameworkElement)ani.obj;
                    var delta = ani.ease.GetDelta(ani.timeFinished / (double)ani.timeTotal, ani.timePercent);
                    obj.Margin = new Thickness(
                        obj.Margin.Left +
                        ModBase.MathPercent(0d, Convert.ToDouble(((dynamic)ani.value).Left), delta),
                        obj.Margin.Top + ModBase.MathPercent(0d, Convert.ToDouble(((dynamic)ani.value).Top), delta),
                        obj.Margin.Right +
                        ModBase.MathPercent(0d, Convert.ToDouble(((dynamic)ani.value).Left), delta),
                        obj.Margin.Bottom +
                        ModBase.MathPercent(0d, Convert.ToDouble(((dynamic)ani.value).Top), delta));
                    obj.Width = Math.Max(
                        obj.Width + ModBase.MathPercent(0d, Convert.ToDouble(((dynamic)ani.value).Width), delta), 0d);
                    obj.Height =
                        Math.Max(
                            obj.Height + ModBase.MathPercent(0d, Convert.ToDouble(((dynamic)ani.value).Height), delta), 0d);
                    break;
                }

                case AniType.TextAppear:
                {
                    var hideFlag = (bool)((dynamic)ani.value)[1];
                    var textLength = ((dynamic)ani.value)[0].ToString().Length;
                    var textCount = (int)Math.Round(
                        (double)(hideFlag ? textLength : 0) + Math.Round(
                            textLength *
                            (hideFlag ? -1 : 1) *
                            ani.ease.GetDelta(ani.timeFinished / (double)ani.timeTotal, 0d)));
                    var originalText = ((dynamic)ani.value)[0].ToString();
                    var newText = originalText.Substring(0, Math.Min(textCount, originalText.Length));
                    // 添加乱码
                    if (textCount < originalText.Length)
                    {
                        var nextText = originalText.Substring(textCount, 1);
                        if (Convert.ToInt32(Convert.ToChar(nextText)) >= Convert.ToInt32(Convert.ToChar(128)))
                            newText += Encoding.GetEncoding("GB18030").GetString(new[]
                            {
                                (byte)RandomUtils.NextInt(16 + 160, 87 + 160),
                                (byte)RandomUtils.NextInt(1 + 160, 89 + 160)
                            });
                        else
                            newText += RandomUtils.PickRandom(
                                @"0123456789./*-+\[]{};':/?,!@#$%^&*()_+-=qwwertyuiopasdfghjklzxcvbnmQWERTYUIOPASDFGHJKLZXCVBNM"
                                    .ToCharArray());
                    }

                    // 设置文本
                    if (ani.obj is TextBlock)
                        ((dynamic)ani.obj).Text = newText;
                    else
                        ((dynamic)ani.obj).Context = newText;

                    break;
                }

                case AniType.Code:
                {
                    ((ThreadStart)ani.value)();
                    break;
                }

                case AniType.ScaleTransform:
                {
                    var obj = (FrameworkElement)ani.obj;
                    if (!(obj.RenderTransform is ScaleTransform))
                    {
                        obj.RenderTransformOrigin = new Point(0.5d, 0.5d);
                        obj.RenderTransform = new ScaleTransform(1d, 1d);
                    }

                    var delta = ModBase.MathPercent(0d, (double)ani.value,
                        ani.ease.GetDelta(ani.timeFinished / (double)ani.timeTotal, ani.timePercent));
                    ((ScaleTransform)obj.RenderTransform).ScaleX =
                        Math.Max(((ScaleTransform)obj.RenderTransform).ScaleX + delta, 0d);
                    ((ScaleTransform)obj.RenderTransform).ScaleY =
                        Math.Max(((ScaleTransform)obj.RenderTransform).ScaleY + delta, 0d);
                    break;
                }

                case AniType.RotateTransform:
                {
                    var obj = (FrameworkElement)ani.obj;
                    if (!(obj.RenderTransform is RotateTransform))
                    {
                        obj.RenderTransformOrigin = new Point(0.5d, 0.5d);
                        obj.RenderTransform = new RotateTransform(0d);
                    }

                    var delta = ModBase.MathPercent(0d, (double)ani.value,
                        ani.ease.GetDelta(ani.timeFinished / (double)ani.timeTotal, ani.timePercent));
                    ((RotateTransform)obj.RenderTransform).Angle = ((RotateTransform)obj.RenderTransform).Angle + delta;
                    break;
                }
            }

            ani.timePercent = ani.timeFinished / (double)ani.timeTotal; // 修改执行百分比
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "执行动画失败：" + ani,
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Application.Animation.Error.OperationFailed"));
        }

        return ani;
    }

}
