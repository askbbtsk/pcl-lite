using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Minecraft.Profile;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft;
using PCL.Core.Plugins;
using PCL.Core.UI;
using PCL.Core.UI.Navigation;
using PCL.Core.UI.Theme;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Plugin.SDK;

namespace PCL;

public partial class FormMain
{

    // 重写窗口边缘判定以使 DWM 自带的 resizer 行为看起来比较正常
    private nint _SizeWndProc(nint hWnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // 窗口活动常量
        const int WM_NCHITTEST = 0x84;
        const int HTCLIENT = 1;
        const int HTLEFT = 10;
        const int HTRIGHT = 11;
        const int HTTOP = 12;
        const int HTTOPLEFT = 13;
        const int HTTOPRIGHT = 14;
        const int HTBOTTOM = 15;
        const int HTBOTTOMLEFT = 16;
        const int HTBOTTOMRIGHT = 17;

        // WPF 尺寸的 offset
        const int offsetWpf = 6;
        const int hitWidthWpf = 5;

        // 过滤非 WM_NCHITTEST 事件
        if (msg != WM_NCHITTEST)
            return nint.Zero;

        // 提取鼠标坐标
        // 没妈的 VB 强转还得检查一下幻想的妈是不是还活着
        var mouseBytes = BitConverter.GetBytes(lParam.ToInt64());
        var xMouse = BitConverter.ToInt16(mouseBytes, 0);
        var yMouse = BitConverter.ToInt16(mouseBytes, 2);

        // 获取窗口参数
        var windowRect = WindowInterop.GetWindowRectangle(hWnd);
        var windowBounds = windowRect.ToWindowBounds();

        // 判断鼠标是否在窗口范围内
        var isInWindow = xMouse >= windowRect.Left && xMouse <= windowRect.Right && yMouse >= windowRect.Top &&
                         yMouse <= windowRect.Bottom;

        // 过滤不在窗口内的请求
        if (!isInWindow)
            return nint.Zero;

        // 如果 CanResize 为 False，直接返回 HTCLIENT
        if (!canResize)
            return new nint(HTCLIENT);

        // 真实像素尺寸的 offset
        var dpi = VisualTreeHelper.GetDpi(this);
        var offsetPxX = offsetWpf * dpi.DpiScaleX;
        var offsetPxY = offsetWpf * dpi.DpiScaleY;
        var hitWidthPxX = hitWidthWpf * dpi.DpiScaleX;
        var hitWidthPxY = hitWidthWpf * dpi.DpiScaleY;

        // 计算鼠标相对于窗口左上角的物理像素位置
        var relX = xMouse - windowRect.Left;
        var relY = yMouse - windowRect.Top;
        var w = windowBounds.Width;
        var h = windowBounds.Height;

        // 判定是否命中偏移后的热区
        var inLeft = relX >= offsetPxX && relX <= offsetPxX + hitWidthPxX;
        var inRight = relX <= w - offsetPxX && relX >= w - offsetPxX - hitWidthPxX;
        var inTop = relY >= offsetPxY && relY <= offsetPxY + hitWidthPxY;
        var inBottom = relY <= h - offsetPxY && relY >= h - offsetPxY - hitWidthPxY;

        handled = true; // 接管该区域的消息

        // 返回结果
        if (inTop && inLeft)
            return new nint(HTTOPLEFT);
        if (inTop && inRight)
            return new nint(HTTOPRIGHT);
        if (inBottom && inLeft)
            return new nint(HTBOTTOMLEFT);
        if (inBottom && inRight)
            return new nint(HTBOTTOMRIGHT);
        if (inLeft)
            return new nint(HTLEFT);
        if (inRight)
            return new nint(HTRIGHT);
        if (inTop)
            return new nint(HTTOP);
        if (inBottom)
            return new nint(HTBOTTOM);

        // 如果在 0-offset 范围内，返回 HTCLIENT 杀掉默认缩放
        return new nint(HTCLIENT);
    }


    protected override void OnSourceInitialized(EventArgs e)
    {
        // 硬件加速
        if (Config.System.DisableHardwareAcceleration)
        {
            var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (hwndSource is not null) hwndSource.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        }

        base.OnSourceInitialized(e);

        // 获取当前窗口句柄
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        if (source is not null)
        {
            // 渲染层允许 Alpha 通道通过
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            // 魔改窗口边缘判定
            source.AddHook(_SizeWndProc);
        }

        // 设置 DWM 窗口框架
        try
        {
            WindowInterop.ExtendFrameIntoClientArea(hwnd, -1);
        }
        catch (Exception ex)
        {
            LogWrapper.Error("DWM 窗口框架应用失败: " + ex.Message);
        }
    }


    // 关闭
    private void FormMain_Closing(object sender, CancelEventArgs e)
    {
        EndProgram(true);
        e.Cancel = true;
    }


    /// <summary>
    ///     正常关闭程序。程序将在执行此方法后约 0.3s 退出。
    /// </summary>
    /// <param name="sendWarning">是否在还有下载任务未完成时发出警告。</param>
    /// <param name="isUpdating">是否正在更新重启</param>
    public void EndProgram(bool sendWarning, bool isUpdating = false)
    {
        // 发出警告
        if (sendWarning && ModNet.HasDownloadingTask())
        {
            if (ModMain.MyMsgBox(Lang.Text("Main.Exit.HasDownloadingTask"), Lang.Text("Common.Dialog.Title"), Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")) == 1)
                // 强行结束下载任务
                ModBase.RunInNewThread(() =>
                {
                    ModBase.Log("[System] 正在强行停止任务");
                    foreach (var Task in ModLoader.loaderTaskbar.ToList())
                        Task.Abort();
                }, "强行停止下载任务");
            else
                return;
        }

        // 关闭
        ModBase.RunInUiWait(() =>
        {
            // 清理视频背景
            VideoBack.Stop();
            VideoBack.Source = null;
            VideoBack.Close();
            IsHitTestVisible = false;
            if (RenderTransform is null)
            {
                var transformPos = new TranslateTransform(0d, 0d);
                var transformRotate = new RotateTransform(0d);
                var transformScale = new ScaleTransform(1d, 1d);
                transformScale.CenterX = Width / 2d;
                transformScale.CenterY = Height / 2d;
                RenderTransform = new TransformGroup
                    { Children = new TransformCollection([transformRotate, transformPos, transformScale]) };
                ModAnimation.AniStart(new[]
                {
                    ModAnimation.AaOpacity(this, -Opacity, 140, 40,
                        new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                    ModAnimation.AaDouble(i =>
                    {
                        transformScale.ScaleX += (double)i;
                        transformScale.ScaleY += (double)i;
                    }, 0.88d - transformScale.ScaleX, 180),
                    ModAnimation.AaDouble(i => transformPos.Y += (double)i,
                        20d - transformPos.Y, 180, 0,
                        new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                    ModAnimation.AaDouble(i => transformRotate.Angle += (double)i,
                        0.6d - transformRotate.Angle, 180, 0,
                        new ModAnimation.AniEaseInoutFluent(ModAnimation.AniEasePower.Weak)),
                    ModAnimation.AaCode(() =>
                    {
                        IsHitTestVisible = false;
                        Visibility = Visibility.Collapsed;
                        ShowInTaskbar = false;
                    }, 210),
                    ModAnimation.AaCode(() => EndProgramForce(force: false, isUpdating: isUpdating), 230)
                }, "Form Close");
            }
            else
            {
                EndProgramForce(force: false, isUpdating: isUpdating);
            }

            ModBase.Log("[System] 收到关闭指令");
        });
    }


    public static void EndProgramForce(ModBase.ProcessReturnValues returnCode = ModBase.ProcessReturnValues.Success,
        bool force = true, bool isUpdating = false)
    {
        // On Error Resume Next
        ModBase.isProgramEnded = true;
        ModAnimation.AniControlEnabled += 1;
        if (UpdateManager.isUpdateWaitingRestart && !isUpdating)
            UpdateManager.UpdateRestart(false, false);
        if (returnCode == ModBase.ProcessReturnValues.Exception)
        {
            if (!isLogShown)
            {
                ModBase.FeedbackInfo();
                ModBase.Log($"请在 {ProjectLinks.Issues} 提交错误报告，以便于解决此问题！（这也有可能是原版 PCL 的问题）");
                isLogShown = true;
                ModBase.ShellOnly(LogWrapper.CurrentLogger.CurrentLogFiles.Last());
            }

            Thread.Sleep(500); // 防止 PCL 在记事本打开前就被掐掉
        }

        ModBase.Log("[System] 程序已退出，返回值：" + ModBase.GetStringFromEnum(returnCode));
        // If ReturnCode <> ProcessReturnValues.Success Then Environment.Exit(ReturnCode)
        // Process.GetCurrentProcess.Kill()
        Lifecycle.Shutdown((int)returnCode, force);
    }


    private void BtnTitleClose_Click(object sender, EventArgs e)
    {
        EndProgram(true);
    }


    // 移动
    private void FormDragMove(object sender, MouseButtonEventArgs e)
    {
        // On Error Resume Next
        if (((Grid)sender).IsMouseDirectlyOver)
            DragMove();
    }


    // 改变大小
    private void FormMain_SizeChanged(object? sender = null, EventArgs? e = null)
    {
        // 拖窗口只改「这一次」的尺寸，不写回配置。
        // 设置里的「窗口大小」才是下次启动的默认值，随手拖两下不该把它冲掉
        // （否则选了「PCL 经典」，拖一下窗口再进设置页就变成「自定义」了）。
        // 用户主动选预设 / 填自定义尺寸时走 ApplyWindowSize，那条路自己会写 States.UI。
        if (PanBack is not null)
        {
            RectForm.Rect = new Rect(0d, 0d, PanBack.ActualWidth, PanBack.ActualHeight);

            var formWidth = PanBack.ActualWidth + 0.001d;
            var formHeight = PanBack.ActualHeight + 0.001d;

            PanForm.Width = formWidth;
            PanForm.Height = formHeight;
            PanMain.Width = formWidth;

            if (PanTitle is not null)
                PanMain.Height = Math.Max(0d, formHeight - PanTitle.ActualHeight);
            else
                PanMain.Height = formHeight;

            VideoBack.Width = formWidth;
            VideoBack.Height = formHeight;
        }

        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal; // 修复 #1938

        QueueNavigationOverflowRefresh();
    }


    /// <summary>根据用户折叠列表，把折叠页面排在导航三角按钮后面。</summary>
    public void RefreshNavigationOverflow()
    {
        if (PanTitleSelect is null || BtnTitleNavOverflow is null) return;
        if (!IsLoaded || PanTitle.ActualWidth <= 0d || PanTitleSelectViewport.ActualHeight <= 0d)
        {
            QueueNavigationOverflowRefresh();
            return;
        }

        var buttons = new (MyRadioButton Button, int Id)[]
        {
            (BtnTitleSelect0, 0), (BtnTitleSelect1, 1), (BtnTitleSelect2, 2),
            (BtnTitleSelectLunar, 13), (BtnTitleSelect4, 4),
            (BtnTitleSelect3, 3), (BtnTitleSelectPlugins, 14)
        };
        var preferredCollapsedIds = ParseCollapsedNavigationIds(Config.Preference.TopBarCollapsedTabs);
        var visibleButtons = buttons.Where(item => !IsNavigationFeatureHidden(item.Id)).ToArray();
        var foldingEnabled = visibleButtons.Length > 5;
        if (!foldingEnabled) _navigationExpanded = false;

        var infiniteSize = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var sideReservedWidth = Math.Max(GetTitleLeftReservedWidth(), GetTitleRightReservedWidth());
        var viewportMargins = PanTitleSelectViewport.Margin.Left + PanTitleSelectViewport.Margin.Right;
        var availableNavigationWidth = Math.Max(0d,
            PanTitle.ActualWidth - sideReservedWidth * 2d - viewportMargins - 2d);
        PanTitleSelectViewport.MaxWidth = availableNavigationWidth;

        var measuredTabs = new List<TopBarNavigationItem>(visibleButtons.Length);
        foreach (var (button, id) in visibleButtons)
        {
            var previousVisibility = button.Visibility;
            button.Visibility = Visibility.Visible;
            button.Measure(infiniteSize);
            measuredTabs.Add(new TopBarNavigationItem(id, button.DesiredSize.Width));
            button.Visibility = previousVisibility;
        }

        var overflowButtonWidth = BtnTitleNavOverflow.Width + BtnTitleNavOverflow.Margin.Left + BtnTitleNavOverflow.Margin.Right;
        var autoCollapsedIds = foldingEnabled
            ? TopBarNavigationLayout.SelectCollapsedTabs(measuredTabs, preferredCollapsedIds,
                availableNavigationWidth, overflowButtonWidth)
            : new HashSet<int>();
        if (autoCollapsedIds.Count == 0) _navigationExpanded = false;
        _navigationAutoCollapsedIds = autoCollapsedIds.ToHashSet();

        var mainButtons = new List<MyRadioButton>();
        var overflowButtons = new List<MyRadioButton>();
        foreach (var (button, id) in buttons)
        {
            var featureHidden = IsNavigationFeatureHidden(id);
            var isCollapsed = autoCollapsedIds.Contains(id);
            button.Visibility = featureHidden || isCollapsed && !_navigationExpanded
                ? Visibility.Collapsed
                : Visibility.Visible;
            (isCollapsed ? overflowButtons : mainButtons).Add(button);
        }

        var desiredChildren = mainButtons.Cast<UIElement>()
            .Append(BtnTitleNavOverflow)
            .Concat(overflowButtons)
            .ToArray();
        if (PanTitleSelect.Children.Count != desiredChildren.Length ||
            !PanTitleSelect.Children.Cast<UIElement>().SequenceEqual(desiredChildren))
        {
            PanTitleSelect.Children.Clear();
            foreach (var child in desiredChildren) PanTitleSelect.Children.Add(child);
        }

        BtnTitleNavOverflow.Visibility = _navigationAutoCollapsedIds.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnTitleNavOverflow.SvgIcon = _navigationExpanded
            ? "lucide/chevron-down"
            : "lucide/chevron-right";
        BtnTitleNavOverflow.ToolTip = Lang.Text(_navigationExpanded
            ? "Main.Tab.OverflowHide"
            : "Main.Tab.OverflowShow");
        UpdateNavigationOverflowPosition();
    }


    private static HashSet<int> ParseCollapsedNavigationIds(string? value)
    {
        var validIds = new HashSet<int> { 1, 2, 3, 4, 13, 14 };
        return (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => int.TryParse(text, out var id) ? id : -1)
            .Where(validIds.Contains)
            .ToHashSet();
    }


    private double GetTitleRightReservedWidth()
    {
        var reservedWidth = 0d;
        foreach (var button in new[] { BtnTitleClose, BtnTitleMin, BtnTitleHelp })
        {
            if (button.Visibility != Visibility.Visible) continue;
            var buttonWidth = double.IsFinite(button.Width)
                ? button.Width
                : Math.Max(0d, button.DesiredSize.Width - button.Margin.Left - button.Margin.Right);
            reservedWidth = Math.Max(reservedWidth, button.Margin.Right + buttonWidth);
        }
        return reservedWidth;
    }


    private double GetTitleLeftReservedWidth()
    {
        var infiniteSize = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var columnWidths = new double[PanTitleLeft.ColumnDefinitions.Count];
        foreach (var child in PanTitleLeft.Children.OfType<FrameworkElement>())
        {
            if (child.Visibility != Visibility.Visible) continue;
            child.Measure(infiniteSize);
            var column = Grid.GetColumn(child);
            if (column >= 0 && column < columnWidths.Length)
                columnWidths[column] = Math.Max(columnWidths[column], child.DesiredSize.Width);
        }

        // PanTitle_SizeChanged reserves 30 DIPs around the logo column. Include that
        // room here so the middle navigation never forces the PCL Lite mark to shrink.
        return columnWidths.Sum() + 30d;
    }


    private static bool IsNavigationFeatureHidden(int id)
    {
        if (PageSetupUI.HiddenForceShow) return false;
        var hidden = Config.Preference.Hide;
        return id switch
        {
            1 => hidden.PageDownload,
            2 => hidden.PageBedrock,
            3 => hidden.PageSetup,
            4 => hidden.PageTools,
            13 => hidden.PageLunar,
            _ => false
        };
    }


    private void BtnTitleNavOverflow_Click(object sender, EventArgs e)
    {
        if (_navigationAutoCollapsedIds.Count == 0) return;
        _navigationExpanded = !_navigationExpanded;
        RefreshNavigationOverflow();
    }


    private void PanTitleSelect_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(UpdateNavigationOverflowPosition, System.Windows.Threading.DispatcherPriority.Loaded);
        QueueNavigationOverflowRefresh();
    }


    private void QueueNavigationOverflowRefresh()
    {
        if (_navigationReflowQueued || PanTitleSelect is null || !IsLoaded) return;
        _navigationReflowQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _navigationReflowQueued = false;
            RefreshNavigationOverflow();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }


    private void UpdateNavigationOverflowPosition()
    {
        if (PanTitleSelect is null || PanTitleSelectViewport is null || !PanTitleSelectViewport.IsLoaded) return;

        // 正常态保持居中；展开态靠右排列折叠导航。
        PanTitleSelect.HorizontalAlignment = _navigationExpanded
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Center;
        PanTitleSelect.RenderTransform = Transform.Identity;
    }


    // 标题栏改变大小
    private void PanTitle_SizeChanged(object sender, EventArgs e)
    {
        if (PanTitleMain.ColumnDefinitions[0].ActualWidth - 30 <= 0)
            PanTitleLeft.ColumnDefinitions[0].MaxWidth = 0;
        else
            PanTitleLeft.ColumnDefinitions[0].MaxWidth = PanTitleMain.ColumnDefinitions[0].ActualWidth - 30;
        QueueNavigationOverflowRefresh();
    }


    // 最小化
    private void BtnTitleMin_Click(object sender, EventArgs e)
    {
        WindowState = WindowState.Minimized;
    }


    private void BtnTitleHelp_Click(object sender, EventArgs e)
    {
        ModBase.OpenWebsite("https://www.bilibili.com/video/BV1uT4y1P7CX");
    }




    public void AddResizer()
    {
        canResize = true;
    }


    public void RemoveResizer()
    {
        canResize = false;
    }


    // 按键事件
    private void FormMain_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat)
            return;
        // 调用弹窗：回车选择第一个，Esc 选择最后一个
        if (PanMsg.Children.Count > 0)
        {
            if (e.Key == Key.Enter)
            {
                var msg = PanMsg.Children[0];
                Action? enterAction = msg switch
                {
                    MyMsgInput input => () => input.Btn1_Click(sender, null),
                    MyMsgSelect select => () => select.Btn1_Click(sender, null),
                    MyMsgText text => () => text.Btn1_Click(sender, null),
                    MyMsgMarkdown markdown => () => markdown.Btn1_Click(sender, null),
                    MyMsgLogin login => () => login.Btn1_Click(sender, null),
                    _ => null
                };
                enterAction?.Invoke();
                return;
            }

            if (e.Key == Key.Escape)
            {
                var msg = PanMsg.Children[0];
                Action? escapeAction = msg switch
                {
                    MyMsgInput input => input.Btn2.Visibility == Visibility.Visible
                        ? () => input.Btn2_Click(sender, null)
                        : () => input.Btn1_Click(sender, null),
                    MyMsgSelect select => select.Btn2.Visibility == Visibility.Visible
                        ? () => select.Btn2_Click(sender, null)
                        : () => select.Btn1_Click(sender, null),
                    MyMsgText text => text.Btn3.Visibility == Visibility.Visible
                        ? () => text.Btn3_Click(sender, null)
                        : text.Btn2.Visibility == Visibility.Visible
                            ? () => text.Btn2_Click(sender, null)
                            : () => text.Btn1_Click(sender, null),
                    MyMsgMarkdown markdown => markdown.Btn3.Visibility == Visibility.Visible
                        ? () => markdown.Btn3_Click(sender, null)
                        : markdown.Btn2.Visibility == Visibility.Visible
                            ? () => markdown.Btn2_Click(sender, null)
                            : () => markdown.Btn1_Click(sender, null),
                    MyMsgLogin login => login.Btn3.Visibility == Visibility.Visible
                        ? () => login.Btn3_Click(sender, null)
                        : () => login.Btn1_Click(sender, null),
                    _ => null
                };
                escapeAction?.Invoke();
                return;
            }
        }

        // 按 ESC 返回上一级
        if (e.Key == Key.Escape)
            TriggerPageBack();
        // 更改隐藏实例可见性
        if (e.Key == Key.F11 && pageCurrent == PageType.InstanceSelect)
        {
            ModMain.frmSelectRight.showHidden = !ModMain.frmSelectRight.showHidden;
            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
            return;
        }

        // 更改功能隐藏可见性
        if (e.Key == Key.F12)
        {
            PageSetupUI.HiddenForceShow = !PageSetupUI.HiddenForceShow;
            if (PageSetupUI.HiddenForceShow)
                HintService.Hint(Lang.Text("Main.HiddenFeature.Disabled"), HintType.Success);
            else
                HintService.Hint(Lang.Text("Main.HiddenFeature.Enabled"), HintType.Success);
            PageSetupUI.HiddenRefresh();
            return;
        }

        // 按 F5 刷新页面
        if (e.Key == Key.F5)
        {
            if (pageLeft is IRefreshable)
                ((IRefreshable)pageLeft).Refresh();
            if (pageRight is IRefreshable)
                ((IRefreshable)pageRight).Refresh();
            return;
        }

        // 调用启动游戏
        if (e.Key == Key.Enter && pageCurrent == PageType.Launch)
        {
            if (ModMain.isAprilEnabled && !ModMain.isAprilGiveup)
                HintService.Hint(Lang.Text("Main.April.Nope"));
            else
                ModMain.frmLaunchLeft.LaunchButtonClick();
        }

        // 修复按下 Alt 后误认为弹出系统菜单导致的冻结
        if (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt)
            e.Handled = true;
    }


    private void FormMain_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 鼠标侧键返回上一级
        if (ModMain.frmMain!.PanMsg.Children.Count > 0 || ModMain.WaitingMyMsgBox.Any())
            return; // 弹窗中（#5513）
        if (e.ChangedButton == MouseButton.XButton1 || e.ChangedButton == MouseButton.XButton2)
            TriggerPageBack();
    }


    private void TriggerPageBack()
    {
        if (pageCurrent == PageType.Download && PageCurrentSub == PageSubType.DownloadInstall &&
            ModMain.frmDownloadInstall.isInSelectPage)
            ModMain.frmDownloadInstall.ExitSelectPage();
        else if (pageCurrent == PageType.InstanceSetup && PageCurrentSub == PageSubType.VersionInstall &&
                 ModMain.frmInstanceInstall.isInSelectPage)
            ModMain.frmInstanceInstall.ExitSelectPage();
        else
            PageBack();
    }


    // 切回窗口
    private void FormMain_Activated(object sender, EventArgs e)
    {
        try
        {
            if (Config.Download.Comp.ReadClipboard)
                ModComp.CompClipboard.GetClipboardResource();
            if (pageCurrent == PageType.InstanceSetup && PageCurrentSub == PageSubType.VersionMod)
            {
                // Mod 管理自动刷新
                ModMain.frmInstanceMod.ReloadCompFileList();
            }
            else if (pageCurrent == PageType.InstanceSetup && PageCurrentSub == PageSubType.VersionResourcePack)
            {
                // 资源包管理自动刷新
                if (ModMain.frmInstanceResourcePack is not null)
                    ModMain.frmInstanceResourcePack.ReloadCompFileList();
            }
            else if (pageCurrent == PageType.InstanceSetup && PageCurrentSub == PageSubType.VersionShader)
            {
                // 光影包管理自动刷新
                if (ModMain.frmInstanceShader is not null)
                    ModMain.frmInstanceShader.ReloadCompFileList();
            }
            else if (pageCurrent == PageType.InstanceSetup && PageCurrentSub == PageSubType.VersionSchematic)
            {
                // 投影原理图管理自动刷新
                if (ModMain.frmInstanceSchematic is not null)
                    ModMain.frmInstanceSchematic.ReloadCompFileList();
            }
            else if (pageCurrent == PageType.InstanceSelect)
            {
                // 实例选择自动刷新
                ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                    ModLoader.LoaderFolderRunType.RunOnUpdated, 1, @"versions\");
            }
            else if (ModMain.frmMain.pageRight is PageInstanceSavesDatapack &&
                     ModMain.frmInstanceSavesDatapack is not null)
            {
                // 数据包管理自动刷新
                ModMain.frmInstanceSavesDatapack.ReloadDatapackFileList();
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "切回窗口时出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }
    }

}
