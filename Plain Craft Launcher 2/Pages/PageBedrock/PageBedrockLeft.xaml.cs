using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     基岩版板块的左栏：下载 / 启动切换、状态卡片，以及左下角的启动按钮。
///     <para>
///         骨架照 <see cref="PageToolsLeft" /> 写，区别是没有「隐藏子页面」的配置项，
///         所以 Loaded 时直接选中第一项。状态卡片与启动按钮的数据来自
///         <see cref="ModBedrockRuntime" />，右栏的版本列表读写的是同一份状态。
///     </para>
/// </summary>
public partial class PageBedrockLeft
{
    private bool _isLoad;

    /// <summary>状态圆点的两种画刷，缓存下来免得每次刷新都查一遍资源字典。</summary>
    private Brush? _brushReady;
    private Brush? _brushIdle;

    public PageBedrockLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        Loaded += PageBedrockLeft_Loaded;
        Unloaded += PageBedrockLeft_Unloaded;
    }

    private void PageBedrockLeft_Loaded(object sender, RoutedEventArgs e)
    {
        // 已经从其他子页面切过来时不要再抢一次选中状态
        if (_isLoad) return;
        _isLoad = true;
        ItemDownload.SetChecked(true, false, false);

        ModBedrockRuntime.StateChanged += RefreshStatus;
        RefreshStatus();
        ModBedrockRuntime.Refresh();
    }

    private void PageBedrockLeft_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoad = false;
        ModBedrockRuntime.StateChanged -= RefreshStatus;
    }

    /// <summary>把共享状态画到状态卡片与左下角启动按钮上。</summary>
    private void RefreshStatus()
    {
        if (!IsLoaded) return;

        LabStatus.Text = ModBedrockRuntime.StatusText;
        // 详情行始终占位：隐藏 / 显示会让状态卡片的高度反复变化，整块左栏跟着抖
        LabDetail.Text = ModBedrockRuntime.DetailText;

        var ready = ModBedrockRuntime.Current is not null;
        _brushReady ??= (Brush)FindResource("ColorBrush3");
        _brushIdle ??= (Brush)FindResource("ColorBrushGray4");
        ShapeStatusDot.Fill = ready ? _brushReady : _brushIdle;
        LabVersionText.Text = ModBedrockRuntime.Current?.DisplayName ?? "";
        BtnLaunch.IsEnabled = !ModBedrockRuntime.IsBusy;
    }

    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        ModBedrockRuntime.Launch();
    }

    #region 页面切换

    /// <summary>
    ///     当前页面的编号。
    /// </summary>
    public FormMain.PageSubType pageID = FormMain.PageSubType.BedrockDownload;

    /// <summary>
    ///     勾选事件改变页面。
    /// </summary>
    private void PageCheck(object senderRaw, ModBase.RouteEventArgs e)
    {
        var sender = (MyListItem)senderRaw;
        // 尚未初始化控件属性时 Tag 为空，会导致切到页面 0
        if (sender.Tag is not null)
            PageChange((FormMain.PageSubType)ModBase.Val(sender.Tag));
    }

    public object PageGet(FormMain.PageSubType? id = null)
    {
        var targetID = id ?? pageID;
        switch (targetID)
        {
            case FormMain.PageSubType.BedrockDownload:
            {
                ModMain.frmBedrockDownload ??= new PageBedrockDownload();
                return ModMain.frmBedrockDownload;
            }
            case FormMain.PageSubType.BedrockLaunch:
            {
                ModMain.frmBedrockLaunch ??= new PageBedrockLaunch();
                return ModMain.frmBedrockLaunch;
            }
            default:
            {
                throw new Exception("未知的基岩版子页面种类：" + (int)targetID);
            }
        }
    }

    /// <summary>
    ///     切换现有页面。
    /// </summary>
    public void PageChange(FormMain.PageSubType id)
    {
        if (pageID == id)
            return;
        ModAnimation.AniControlEnabled += 1;
        try
        {
            PageChangeRun((MyPageRight)PageGet(id));
            pageID = id;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"切换基岩版分页面失败（ID {(int)id}）",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Bedrock.Error.OperationFailed"));
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }
    }

    private static void PageChangeRun(MyPageRight target)
    {
        ModAnimation.AniStop("FrmMain PageChangeRight"); // 停止主页面的右页面切换动画，防止它与本动画一起触发多次 PageOnEnter
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
                // 延迟触发页面通用动画，以使得在 Loaded 事件中加载的控件得以处理
                ModMain.frmMain.pageRight.Opacity = 1d;
                ModMain.frmMain.pageRight.PageOnEnter();
            }, 30, true)
        }, "PageLeft PageChange");
    }

    #endregion
}
