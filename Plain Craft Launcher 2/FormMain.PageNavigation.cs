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

    /// <summary>
    ///     刷新次级页面的名称。
    /// </summary>
    public void PageNameRefresh(PageStackData type)
    {
        LabTitleInner.Text = PageNameGet(type);
    }


    /// <summary>
    ///     刷新次级页面的名称。
    /// </summary>
    public void PageNameRefresh()
    {
        PageNameRefresh(pageCurrent);
    }


    /// <summary>
    ///     当前的子页面。
    /// </summary>
    public PageSubType PageCurrentSub
    {
        get
        {
            switch (pageCurrent.page)
            {
                case PageType.Download:
                {
                    if (ModMain.frmDownloadLeft is null)
                        ModMain.frmDownloadLeft = new PageDownloadLeft();
                    return ModMain.frmDownloadLeft.pageID;
                }

                case PageType.Setup:
                {
                    if (ModMain.frmSetupLeft is null)
                        ModMain.frmSetupLeft = new PageSetupLeft();
                    return ModMain.frmSetupLeft.pageID;
                }

                case PageType.Bedrock:
                {
                    if (ModMain.frmBedrockLeft is null)
                        ModMain.frmBedrockLeft = new PageBedrockLeft();
                    return ModMain.frmBedrockLeft.pageID;
                }

                case PageType.Plugins:
                {
                    ModMain.frmPluginsLeft ??= new PagePluginsLeft();
                    return ModMain.frmPluginsLeft.pageID;
                }

                case PageType.Lunar:
                {
                    if (ModMain.frmLunarLeft is null)
                        ModMain.frmLunarLeft = new PageLunarLeft();
                    return ModMain.frmLunarLeft.pageID;
                }

                case PageType.InstanceSetup:
                {
                    if (ModMain.frmInstanceLeft is null)
                        ModMain.frmInstanceLeft = new PageInstanceLeft();
                    return ModMain.frmInstanceLeft.pageID;
                }

                default:
                {
                    return 0; // 没有子页面
                }
            }
        }
    }


    public class PageStackData
    {
        /// <summary>
        /// <list type="bullet">
        ///   <item><description>CompDetail: (CompProject, ExpandedTitles, TargetVersion, TargetLoader, ResourceType)</description></item>
        ///   <item><description>VersionSaves: SavePath</description></item>
        /// </list>
        /// </summary>
        public (
            ModComp.CompProject CompProject,
            List<string> ExpandedTitles,
            string TargetVersion,
            ModComp.CompLoaderType TargetLoader,
            ModComp.CompType ResourceType,
            string SavePath
        )? additional;

        /// <summary>
        ///     帮助详情页实例。仅在 <see cref="PageType.HelpDetail"/> 中使用。
        /// </summary>
        public PageHelpDetail? helpPage;

        public PageType page;

        public override bool Equals(object other)
        {
            if (other is null)
                return false;
            if (other is PageStackData)
            {
                var pageOther = (PageStackData)other;
                if (page != pageOther.page)
                    return false;
                if (helpPage is not null || pageOther.helpPage is not null)
                    return ReferenceEquals(helpPage, pageOther.helpPage);
                if (additional is null) return pageOther.additional is null;

                return pageOther.additional is not null && additional.Equals(pageOther.additional);
            }

            if (other is int o)
            {
                if ((int)page == o)
                    return false;
                return additional is null;
            }

            return false;
        }

        public override int GetHashCode() => page.GetHashCode();

        public static bool operator ==(PageStackData left, PageStackData right)
        {
            return EqualityComparer<PageStackData>.Default.Equals(left, right);
        }

        public static bool operator !=(PageStackData left, PageStackData right)
        {
            return !(left == right);
        }

        public static implicit operator PageStackData(PageType value)
        {
            return new PageStackData { page = value };
        }

        public static implicit operator PageType(PageStackData value)
        {
            return value.page;
        }
    }


    /// <summary>
    ///     切换页面，并引起对应选择 UI 的改变。
    /// </summary>
    public void PageChange(PageStackData stack, PageSubType subType = PageSubType.Default)
    {
        if (!EnsureLunarCredentialConsent(stack)) return;
        if (string.IsNullOrEmpty(PageNameGet(stack)))
        {
            // 切换到主页面
            PageChangeExit();
            isChangingPage = true; // 防止下面的勾选直接触发了 PageChangeActual
            // 必须按 Tag 找标题栏按钮，不能用 Children[(int)stack.page]：
            // Lunar 是后加进来的标签，Tag 是 13，插在 Children 的第 4 位，
            // 索引和枚举值早就不一一对应了，用索引会点亮错误的标签（或者直接越界）。
            var titleButton = PanTitleSelect.Children.OfType<MyRadioButton>()
                .FirstOrDefault(x => x.Tag is not null && ModBase.Val(x.Tag) == (double)stack.page);
            titleButton?.SetChecked(true, true, string.IsNullOrEmpty(PageNameGet(pageCurrent)));
            isChangingPage = false;
            switch (stack.page)
            {
                case PageType.Download:
                {
                    if (ModMain.frmDownloadLeft is null)
                        ModMain.frmDownloadLeft = new PageDownloadLeft();
                    foreach (var item in ModMain.frmDownloadLeft.PanItem.Children)
                        if (item is MyListItem listItem &&
                            ModBase.Val(listItem.Tag) == (double)subType)
                        {
                            listItem.SetChecked(true, true, stack == pageCurrent);
                            break;
                        }

                    break;
                }
                case PageType.Setup:
                {
                    if (ModMain.frmSetupLeft is null)
                        ModMain.frmSetupLeft = new PageSetupLeft();
                    foreach (var item in ModMain.frmSetupLeft.PanItem.Children)
                        if (item is MyListItem listItem &&
                            ModBase.Val(listItem.Tag) == (double)subType)
                        {
                            listItem.SetChecked(true, true, stack == pageCurrent);
                            break;
                        }

                    break;
                }
                case PageType.Bedrock:
                {
                    if (ModMain.frmBedrockLeft is null)
                        ModMain.frmBedrockLeft = new PageBedrockLeft();
                    foreach (var item in ModMain.frmBedrockLeft.PanItem.Children)
                        if (item is MyListItem listItem &&
                            ModBase.Val(listItem.Tag) == (double)subType)
                        {
                            listItem.SetChecked(true, true, stack == pageCurrent);
                            break;
                        }

                    break;
                }
                case PageType.Plugins:
                {
                    ModMain.frmPluginsLeft ??= new PagePluginsLeft();
                    ModMain.frmPluginsLeft.Select(subType, stack == pageCurrent);
                    break;
                }
                case PageType.Lunar:
                {
                    if (ModMain.frmLunarLeft is null)
                        ModMain.frmLunarLeft = new PageLunarLeft();
                    foreach (var item in ModMain.frmLunarLeft.PanItem.Children)
                        if (item is MyListItem listItem &&
                            ModBase.Val(listItem.Tag) == (double)subType)
                        {
                            listItem.SetChecked(true, true, stack == pageCurrent);
                            break;
                        }

                    break;
                }
            }

            PageChangeActual(stack, subType);
        }
        else
        {
            // 切换到次页面
            switch (stack.page)
            {
                case PageType.InstanceSetup:
                {
                    if (ModMain.frmInstanceLeft is null)
                        ModMain.frmInstanceLeft = new PageInstanceLeft();
                    foreach (var item in ModMain.frmInstanceLeft.PanItem.Children)
                        if (item is MyListItem listItem &&
                            ModBase.Val(listItem.Tag) == (double)subType)
                        {
                            listItem.SetChecked(true, true, stack == pageCurrent);
                            break;
                        }

                    break;
                }
                case PageType.VersionSaves:
                {
                    if (ModMain.frmInstanceSavesLeft is null)
                        ModMain.frmInstanceSavesLeft = new PageInstanceSavesLeft();
                    foreach (var item in ModMain.frmInstanceSavesLeft.PanItem.Children)
                        if (item is MyListItem listItem &&
                            ModBase.Val(listItem.Tag) == (double)subType)
                        {
                            listItem.SetChecked(true, true, stack == pageCurrent);
                            break;
                        }

                    break;
                }
            }

            PageChangeActual(stack, subType);
        }
    }


    /// <summary>
    ///     通过点击导航栏改变页面。
    /// </summary>
    private void BtnTitleSelect_Click(MyRadioButton sender, bool raiseByMouse)
    {
        if (isChangingPage)
            return;
        var pageType = (PageType)int.Parse(sender.Tag.ToString());
        PageChangeActual(pageType, PageSubType.Default);
        }


    private void BtnTitleInner_Click(object sender, EventArgs e)
    {
        PageBack();
    }


    /// <summary>
    ///     通过点击返回按钮或手动触发返回来改变页面。
    /// </summary>
    public void PageBack()
    {
        if (pageStack.Any())
            PageChangeActual(pageStack[0], PageSubType.Default);
        else
            PageChange(PageType.Launch);
    }


    // 实际处理页面切换
    /// <summary>
    ///     切换现有页面的实际方法。
    /// </summary>
    private void PageChangeActual(PageStackData stack, PageSubType subType)
    {
        if (!EnsureLunarCredentialConsent(stack)) return;
        if (pageCurrent == stack && (PageCurrentSub == subType || (int)subType == -1))
            return;
        ModAnimation.AniControlEnabled += 1;
        try
        {

            var pageName = PageNameGet(stack);
            if (string.IsNullOrEmpty(pageName))
            {
                // 即将切换到一个顶级页面
                PageChangeExit();
            }
            // 即将切换到一个子页面
            else if (pageStack.Any())
            {
                // 子页面 → 另一个子页面，更新
                ModAnimation.AniStart(
                    new[]
                    {
                    ModAnimation.AaOpacity(LabTitleInner, -LabTitleInner.Opacity, 130),
                    ModAnimation.AaCode(() => LabTitleInner.Text = pageName, after: true),
                    ModAnimation.AaOpacity(LabTitleInner, 1d, 150, 30)
                    }, "FrmMain Titlebar SubLayer");
                if (pageStack.Contains(stack))
                    // 返回到更上层的子页面
                    while (pageStack.Contains(stack))
                        pageStack.RemoveAt(0);
                else
                    // 进入更深层的子页面
                    pageStack.Insert(0, pageCurrent);
            }
            else
            {
                // 主页面 → 子页面，进入
                PanTitleInner.Visibility = Visibility.Visible;
                PanTitleMain.IsHitTestVisible = false;
                PanTitleInner.IsHitTestVisible = true;
                PageNameRefresh(stack);
                ModAnimation.AniStart(
                    new[]
                    {
                    ModAnimation.AaOpacity(PanTitleMain, -PanTitleMain.Opacity, 150),
                    ModAnimation.AaX(PanTitleMain, 12d - PanTitleMain.Margin.Left, 150,
                        ease: new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
                    ModAnimation.AaOpacity(PanTitleInner, 1d - PanTitleInner.Opacity, 150, 200),
                    ModAnimation.AaX(PanTitleInner, -PanTitleInner.Margin.Left, 350, 200,
                        new ModAnimation.AniEaseOutBack()),
                    ModAnimation.AaCode(() => PanTitleMain.Visibility = Visibility.Collapsed, after: true)
                    }, "FrmMain Titlebar FirstLayer");
                pageStack.Insert(0, pageCurrent);
            }



            pageLast = pageCurrent;
            pageCurrent = stack;
            PluginRuntimeManager.Notify(new PluginPageNavigationEvent(
                pageLast?.page.ToString(), pageCurrent?.page.ToString()));
            switch (stack.page)
            {
                case PageType.Launch: // 启动
                    {
                        PageChangeAnim(ModMain.frmLaunchLeft, ModMain.frmLaunchRight);
                        break;
                    }
                case PageType.Download: // 下载
                    {
                        ModMain.frmDownloadLeft ??= new PageDownloadLeft();
                        if (subType != PageSubType.Default)
                            ModMain.frmDownloadLeft.pageID = subType;
                        else
                            subType = ModMain.frmDownloadLeft.pageID;
                        // PageGet 方法会在未设置 SubType 时指定默认值，并建立相关页面的实例
                        PageChangeAnim(ModMain.frmDownloadLeft, (FrameworkElement)ModMain.frmDownloadLeft.PageGet(subType));
                        break;
                    }
                case PageType.Tools: // 联机
                    {
                        ModMain.frmToolsLeft ??= new PageToolsLeft();
                        subType = ModMain.frmToolsLeft.pageID;
                        PageChangeAnim(ModMain.frmToolsLeft, (FrameworkElement)ModMain.frmToolsLeft.PageGet(subType));
                        break;
                    }
                case PageType.Bedrock: // 基岩版
                    {
                        ModMain.frmBedrockLeft ??= new PageBedrockLeft();
                        if (subType != PageSubType.Default)
                            ModMain.frmBedrockLeft.pageID = subType;
                        else
                            subType = ModMain.frmBedrockLeft.pageID;
                        PageChangeAnim(ModMain.frmBedrockLeft,
                            (FrameworkElement)ModMain.frmBedrockLeft.PageGet(subType));
                        break;
                    }
                case PageType.Plugins: // 插件市场
                    {
                        ModMain.frmPluginsLeft ??= new PagePluginsLeft();
                        if (subType is PageSubType.PluginMarket or PageSubType.PluginInstalled)
                            ModMain.frmPluginsLeft.pageID = subType;
                        else
                            subType = ModMain.frmPluginsLeft.pageID;
                        PageChangeAnim(ModMain.frmPluginsLeft,
                            (FrameworkElement)ModMain.frmPluginsLeft.PageGet(subType));
                        break;
                    }
                case PageType.Lunar: // Lunar Client（实验特性）
                    {
                        ModMain.frmLunarLeft ??= new PageLunarLeft();
                        subType = ModMain.frmLunarLeft.pageID;
                        PageChangeAnim(ModMain.frmLunarLeft,
                            (FrameworkElement)ModMain.frmLunarLeft.PageGet(subType));
                        break;
                    }
                case PageType.Setup: // 设置
                    {
                        ModMain.frmSetupLeft ??= new PageSetupLeft();
                        subType = ModMain.frmSetupLeft.pageID;
                        PageChangeAnim(ModMain.frmSetupLeft, (FrameworkElement)ModMain.frmSetupLeft.PageGet(subType));
                        break;
                    }
                case PageType.GameLog: // 实时日志
                    {
                        if (ModMain.frmLogLeft is null)
                            ModMain.frmLogLeft = new PageLogLeft();
                        if (ModMain.frmLogLeft is null)
                            ModMain.frmLogRight = new PageLogRight();
                        PageChangeAnim(ModMain.frmLogLeft, ModMain.frmLogRight);
                        break;
                    }
                case PageType.InstanceSelect: // 实例选择
                    {
                        if (ModMain.frmSelectLeft is null)
                            ModMain.frmSelectLeft = new PageSelectLeft();
                        if (ModMain.frmSelectRight is null)
                            ModMain.frmSelectRight = new PageSelectRight();
                        PageChangeAnim(ModMain.frmSelectLeft, ModMain.frmSelectRight);
                        break;
                    }
                case PageType.TaskManager: // 任务管理
                    {
                        if (ModMain.frmSpeedLeft is null)
                            ModMain.frmSpeedLeft = new PageSpeedLeft();
                        if (ModMain.frmSpeedRight is null)
                            ModMain.frmSpeedRight = new PageSpeedRight();
                        PageChangeAnim(ModMain.frmSpeedLeft, ModMain.frmSpeedRight);
                        break;
                    }
                case PageType.InstanceSetup: // 实例设置
                    {
                        ModMain.frmInstanceLeft ??= new PageInstanceLeft();
                        subType = ModMain.frmInstanceLeft.pageID;
                        PageChangeAnim(ModMain.frmInstanceLeft, (FrameworkElement)ModMain.frmInstanceLeft.PageGet(subType));
                        break;
                    }
                case PageType.CompDetail: // Mod 信息
                    {
                        if (ModMain.frmDownloadCompDetail is null)
                            ModMain.frmDownloadCompDetail = new PageDownloadCompDetail();
                        PageChangeAnim(new MyPageLeft(), ModMain.frmDownloadCompDetail);
                        break;
                    }
                case PageType.HelpDetail: // 帮助详情
                    {
                        if (stack.helpPage is null)
                            throw new InvalidOperationException("帮助详情页面未初始化");
                        PageChangeAnim(new MyPageLeft(), stack.helpPage);
                        break;
                    }
                case PageType.VersionSaves: // 存档管理
                    {
                        if (ModMain.frmInstanceSavesLeft is null)
                            ModMain.frmInstanceSavesLeft = new PageInstanceSavesLeft();
                        PageInstanceSavesLeft.currentSave = stack.additional.Value.SavePath;
                        subType = ModMain.frmInstanceSavesLeft.pageID;
                        PageChangeAnim(ModMain.frmInstanceSavesLeft,
                            (FrameworkElement)ModMain.frmInstanceSavesLeft.PageGet(subType));
                        break;
                    }
            }



            BtnExtraDownload.ShowRefresh();
            BtnExtraApril.ShowRefresh();


            ModBase.Log("[Control] 切换主要页面：" + ModBase.GetStringFromEnum(stack) + ", " + (int)subType);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "切换主要页面失败（ID " + (int)pageCurrent.page + "）",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }
    }


    private void PageChangeAnim(FrameworkElement targetLeft, FrameworkElement targetRight)
    {
        ModAnimation.AniStop("FrmMain LeftChange");
        ModAnimation.AniStop("PageLeft PageChange"); // 停止左边栏变更导致的右页面切换动画，防止它与本动画一起触发多次 PageOnEnter
        ModAnimation.AniControlEnabled += 1;
        // 清除新页面关联性
        if (targetLeft.Parent is not null)
            targetLeft.SetValue(ContentPresenter.ContentProperty, null);
        if (targetRight is not null && targetRight.Parent is not null)
            targetRight.SetValue(ContentPresenter.ContentProperty, null);
        pageLeft = (MyPageLeft)targetLeft;
        pageRight = (MyPageRight)targetRight;
        // 触发页面通用动画
        ((MyPageLeft)PanMainLeft.Child).TriggerHideAnimation();
        ((MyPageRight)PanMainRight.Child).PageOnExit();
        ModAnimation.AniControlEnabled -= 1;

        // 左右两栏必须在同一个动画里换掉，而且要用局部变量捕获目标页：
        // 以前是左右各起一组动画，各自在 110ms 后换自己那一半，只要其中一组被
        // AniStop（PageLunarLeft 等切分页时会停 "FrmMain PageChangeRight"），
        // 或者 pageLeft/pageRight 字段在中途被改写，就会剩下
        // 「左栏还是旧页、右栏已经是新页」的撕裂界面 —— 游戏内点「添加账号」
        // 把界面从 Lunar 页切走时，用户看到的就是这个样子。
        var newLeft = (MyPageLeft)targetLeft;
        var newRight = (MyPageRight)targetRight;
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() =>
            {
                ModAnimation.AniControlEnabled += 1;
                try
                {
                    ((MyPageRight)PanMainRight.Child)?.PageOnForceExit();
                    // 把两栏一起换进容器
                    PanMainLeft.Child = newLeft;
                    newLeft.Opacity = 0d;
                    PanMainLeft.Background = null;
                    PanMainRight.Child = newRight;
                    newRight.Opacity = 0d;
                    PanMainRight.Background = null;
                }
                finally
                {
                    ModAnimation.AniControlEnabled -= 1;
                }

                ModBase.RunInUi(() =>
                {
                    PanMainLeft_Resize(PanMainLeft.ActualWidth);
                    BtnExtraBack.ShowRefresh();
                }, true);
            }, 110),
            ModAnimation.AaCode(() =>
            {
                // 延迟触发页面通用动画，以使得在 Loaded 事件中加载的控件得以处理
                newLeft.Opacity = 1d;
                newLeft.TriggerShowAnimation();
            }, 30, true),
            ModAnimation.AaCode(() =>
            {
                newRight.Opacity = 1d;
                newRight.PageOnEnter();
            }, 30, true)
        }, "FrmMain PageChangeLeft");
    }


    /// <summary>
    ///     退出子界面。
    /// </summary>
    private void PageChangeExit()
    {
        if (pageStack.Any())
        {
            // 子页面 → 主页面，退出
            PanTitleMain.Visibility = Visibility.Visible;
            PanTitleMain.IsHitTestVisible = true;
            PanTitleInner.IsHitTestVisible = false;
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaOpacity(PanTitleInner, -PanTitleInner.Opacity, 150),
                    ModAnimation.AaX(PanTitleInner, -18 - PanTitleInner.Margin.Left, 150,
                        ease: new ModAnimation.AniEaseInFluent()),
                    ModAnimation.AaOpacity(PanTitleMain, 1d - PanTitleMain.Opacity, 150, 200),
                    ModAnimation.AaX(PanTitleMain, -PanTitleMain.Margin.Left, 350, 200,
                        new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
                    ModAnimation.AaCode(() => PanTitleInner.Visibility = Visibility.Collapsed, after: true)
                }, "FrmMain Titlebar FirstLayer");
            pageStack.Clear();
        }
        // 主页面 → 主页面，无事发生
    }


    // 左边栏改变
    private void PanMainLeft_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
            return;
        PanMainLeft_Resize(e.NewSize.Width);
    }


    private void PanMainLeft_Resize(double newWidth)
    {
        var delta = newWidth - RectLeftBackground.Width;
        if (Math.Abs(delta) > 0.1d && ModAnimation.AniControlEnabled == 0)
        {
            if (PanMain.Opacity < 0.1d)
                PanMainLeft.IsHitTestVisible = false; // 避免左边栏指向背景未能完美覆盖左边栏
            if (newWidth > 0d)
                // 宽度足够，显示
                ModAnimation.AniStart(
                    new[]
                    {
                        ModAnimation.AaWidth(RectLeftBackground, newWidth - RectLeftBackground.Width, 180,
                            ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong)),
                        ModAnimation.AaOpacity(RectLeftShadow, 1d - RectLeftShadow.Opacity, 180),
                        ModAnimation.AaCode(() => PanMainLeft.IsHitTestVisible = true, 150)
                    }, "FrmMain LeftChange", true);
            else
                // 宽度不足，隐藏
                ModAnimation.AniStart(
                    new[]
                    {
                        ModAnimation.AaWidth(RectLeftBackground, -RectLeftBackground.Width, 180,
                            ease: new ModAnimation.AniEaseOutFluent()),
                        ModAnimation.AaOpacity(RectLeftShadow, -RectLeftShadow.Opacity, 180),
                        ModAnimation.AaCode(() => PanMainLeft.IsHitTestVisible = true, 150)
                    }, "FrmMain LeftChange", true);
        }
        else
        {
            RectLeftBackground.Width = newWidth;
            PanMainLeft.IsHitTestVisible = true;
            ModAnimation.AniStop("FrmMain LeftChange");
        }
    }




    // 在时钟中调用，使得即使鼠标在窗口外松开，也可以释放控件
    public void DragTick()
    {
        if (ModMain.dragControl is null)
            return;
        if (!(Mouse.LeftButton == MouseButtonState.Pressed)) DragStop();
    }


    // 在鼠标移动时调用，以改变 Slider 位置
    public void DragDoing()
    {
        if (ModMain.dragControl is null)
            return;
        if (Mouse.LeftButton == MouseButtonState.Pressed) 
        {
            ModMain.dragControl.DragDoing();
        }
        else
            DragStop();
    }


    private void PanBack_MouseMove(object sender, EventArgs e)
    {
        DragDoing();
    }

}
