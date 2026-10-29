using System;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageToolsLeft
{
    private bool isLoad;
    private bool isPageSwitched; // 如果在 Loaded 前切换到其他页面，会导致触发 Loaded 时再次切换一次

    public PageToolsLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        Loaded += PageLinkLeft_Loaded;
        Unloaded += PageOtherLeft_Unloaded;
    }

    private void PageLinkLeft_Loaded(object sender, RoutedEventArgs e)
    {
        var isHiddenPage = false;
        var hide = Config.Preference.Hide;

        if (ItemTest.Checked && hide.ToolsTest) isHiddenPage = true;
        if (PageSetupUI.HiddenForceShow)
            isHiddenPage = false;
        // 若页面错误，或尚未加载，则继续
        if (isLoad && !isHiddenPage)
            return;
        isLoad = true;
        // 刷新子页面隐藏情况
        PageSetupUI.HiddenRefresh();
        // 选择第一个未被禁用的子页面
        if (isPageSwitched)
            return;
        ItemTest.SetChecked(true, false, false);
    }

    private void PageOtherLeft_Unloaded(object sender, RoutedEventArgs e)
    {
        isPageSwitched = false;
    }

    #region 页面切换

    /// <summary>
    ///     当前页面的编号。
    /// </summary>
    public FormMain.PageSubType pageID = FormMain.PageSubType.ToolsTest;

    /// <summary>
    ///     勾选事件改变页面。
    /// </summary>
    private void PageCheck(object senderRaw, ModBase.RouteEventArgs e)
    {
        var sender = (MyListItem)senderRaw;
        // 尚未初始化控件属性时，sender.Tag 为 Nothing，会导致切换到页面 0
        // 若使用 IsLoaded，则会导致模拟点击不被执行（模拟点击切换页面时，控件的 IsLoaded 为 False）
        if (sender.Tag is not null)
            PageChange((FormMain.PageSubType)ModBase.Val(sender.Tag));
    }

    public object PageGet(FormMain.PageSubType? id = null)
    {
        var targetID = id ?? pageID;
        switch (targetID)
        {
            case FormMain.PageSubType.ToolsTest:
            {
                if (ModMain.frmToolsTest is null)
                    ModMain.frmToolsTest = new PageToolsTest();
                return ModMain.frmToolsTest;
            }
            case FormMain.PageSubType.ToolsImport:
            {
                if (ModMain.frmToolsImport is null)
                    ModMain.frmToolsImport = new PageToolsImport();
                return ModMain.frmToolsImport;
            }
            default:
            {
                throw new Exception("未知的更多子页面种类：" + (int)id);
            }
        }
    }

    /// <summary>
    ///     从别处跳到某个子页面（百宝箱卡片上的按钮就靠这个），左栏的勾选跟着走。
    ///     <para>
    ///         先设勾选再显式切一次页：勾选本身会触发 <c>Check</c> → <c>PageCheck</c> → <c>PageChange</c>，
    ///         已经切过去时后面这次调用会因为 <c>pageID</c> 相同直接返回，不会切两遍。
    ///     </para>
    /// </summary>
    public void SwitchTo(FormMain.PageSubType id)
    {
        if (id == FormMain.PageSubType.ToolsImport)
            ItemImport.SetChecked(true, false, true);
        else
            ItemTest.SetChecked(true, false, true);

        PageChange(id);
    }

    /// <summary>
    ///     切换现有页面。
    /// </summary>
    public void PageChange(FormMain.PageSubType id)
    {
        if (pageID == id)
            return;
        ModAnimation.AniControlEnabled += 1;
        isPageSwitched = true;
        try
        {
            PageChangeRun((MyPageRight)PageGet(id));
            pageID = id;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"切换分页面失败（ID {(int)id}）",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Tools.Error.OperationFailed"));
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
            target.SetValue(ContentPresenter.ContentProperty, null);
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
