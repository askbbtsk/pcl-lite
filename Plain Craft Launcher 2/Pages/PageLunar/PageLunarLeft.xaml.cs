using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PCL;

/// <summary>
///     Lunar Client 板块的左栏。
///     <para>
///         骨架与 <see cref="PageBedrockLeft" /> 一致：状态卡片 + 左下角启动按钮，
///         数据来自 <see cref="ModLunar" />。目前只有一个子页面，所以不需要真正切换。
///     </para>
///     <para>
///         左下角按钮在游戏运行时会变成「强制停止」——同一个位置、同一个控件，
///         只换文案和动作，免得用户在两颗按钮之间找。
///     </para>
/// </summary>
public partial class PageLunarLeft
{
    private bool _isLoad;
    private Brush? _brushReady;
    private Brush? _brushIdle;

    public PageLunarLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        Loaded += PageLunarLeft_Loaded;
        Unloaded += PageLunarLeft_Unloaded;
    }

    private void PageLunarLeft_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoad) return;
        _isLoad = true;
        ItemLaunch.SetChecked(true, false, false);

        ModLunar.StateChanged += RefreshStatus;
        RefreshStatus();
        ModLunar.Refresh();
    }

    private void PageLunarLeft_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoad = false;
        ModLunar.StateChanged -= RefreshStatus;
    }

    private void RefreshStatus()
    {
        if (!IsLoaded) return;

        var running = ModLunar.IsRunning;

        LabStatus.Text = ModLunar.StatusText;
        LabDetail.Text = ModLunar.DetailText;

        var ready = ModLunar.IsInstalled;
        _brushReady ??= (Brush)FindResource("ColorBrush3");
        _brushIdle ??= (Brush)FindResource("ColorBrushGray4");
        ShapeStatusDot.Fill = ready ? _brushReady : _brushIdle;

        LabVersionText.Text = ready ? ModLunar.SelectedSubVersion : "";

        // 账号：Lunar 只认自己账户库里的激活账号，这里如实显示，
        // 免得用户以为在用 PCL 主页面的登录
        var account = ModLunar.SelectedAccount;
        if (account is not null)
        {
            LabAccountName.Text = account.DisplayName;
            LabAccountHint.Text = $"Lunar 账户库里的账号（{account.Type}），进游戏用的就是它。";
        }
        else
        {
            LabAccountName.Text = "未找到 Lunar 账号";
            LabAccountHint.Text = "先在 Lunar 启动器里登录一次；也可以用 PCL 主页面的登录凭据兜底。";
        }

        RefreshButton(running);
    }

    private void RefreshButton(bool running)
    {
        BtnLaunch.Text = running ? "强制停止" : "启动 Lunar";
        BtnLaunch.IsEnabled = running || !ModLunar.IsBusy;
        BtnLaunch.ColorType = running ? MyButton.ColorState.Red : MyButton.ColorState.Highlight;
    }

    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        // 运行中 → 强制停止；否则走启动
        if (ModLunar.IsRunning)
        {
            ModLunar.Stop();
            RefreshButton(false);
            return;
        }

        if (ModLunar.IsBusy) return;

        if (!ModLunar.IsInstalled)
        {
            ModMain.MyMsgBox(
                $"没有找到 {ModLunar.LunarRoot}。\n请先安装 Lunar Client 并至少启动一次，让它把物料下载下来。",
                "未检测到 Lunar Client");
            return;
        }

        // 游戏目录由 ModLunar 自己决定（跟随主页面选中实例的隔离目录），这里不再传
        ModLunar.Launch();
    }

    #region 页面切换

    public FormMain.PageSubType pageID = FormMain.PageSubType.LunarLaunch;

    private void PageCheck(object senderRaw, ModBase.RouteEventArgs e)
    {
        var sender = (MyListItem)senderRaw;
        if (sender.Tag is not null)
            PageChange((FormMain.PageSubType)ModBase.Val(sender.Tag));
    }

    public object PageGet(FormMain.PageSubType? id = null)
    {
        ModMain.frmLunarLaunch ??= new PageLunarLaunch();
        return ModMain.frmLunarLaunch;
    }

    public void PageChange(FormMain.PageSubType id)
    {
        if (pageID == id) return;
        ModAnimation.AniControlEnabled += 1;
        try
        {
            PageChangeRun((MyPageRight)PageGet(id));
            pageID = id;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"切换 Lunar 分页面失败（ID {(int)id}）", ModBase.LogLevel.Feedback,
                userSummary: "切换页面失败");
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }
    }

    private static void PageChangeRun(MyPageRight target)
    {
        ModAnimation.AniStop("FrmMain PageChangeRight");
        if (target.Parent is not null)
            target.SetValue(System.Windows.Controls.ContentPresenter.ContentProperty, null);
        ModMain.frmMain.pageRight = target;
        ((MyPageRight)ModMain.frmMain.PanMainRight.Child).PageOnExit();
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() =>
            {
                ((MyPageRight)ModMain.frmMain.PanMainRight.Child).PageOnForceExit();
                ModMain.frmMain.PanMainRight.Child = ModMain.frmMain.pageRight;
                ModMain.frmMain.pageRight.Opacity = 0d;
            }, 130),
            ModAnimation.AaCode(() =>
            {
                ModMain.frmMain.pageRight.Opacity = 1d;
                ModMain.frmMain.pageRight.PageOnEnter();
            }, 30, true)
        }, "PageLeft PageChange");
    }

    #endregion
}
