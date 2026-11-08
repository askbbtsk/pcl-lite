using System.Windows;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PagePluginsLeft
{
    public FormMain.PageSubType pageID = FormMain.PageSubType.PluginMarket;
    private bool _isUpdatingSelection;

    public PagePluginsLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        Loaded += (_, _) => Select(pageID, false);
    }

    private void PageCheck(object sender, ModBase.RouteEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (sender is MyListItem { Tag: not null } item)
            PageChange((FormMain.PageSubType)ModBase.Val(item.Tag));
    }

    public object PageGet(FormMain.PageSubType id) => id switch
    {
        FormMain.PageSubType.PluginMarket => ModMain.frmPluginsMarket ??= new PagePluginsMarket(),
        FormMain.PageSubType.PluginInstalled => ModMain.frmPluginsInstalled ??= new PagePluginsInstalled(),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown plugin page.")
    };

    public void Select(FormMain.PageSubType id, bool animate)
    {
        var item = id == FormMain.PageSubType.PluginInstalled ? ItemInstalled : ItemMarket;
        if (item.Checked) return;
        _isUpdatingSelection = true;
        try
        {
            item.SetChecked(true, animate, false);
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void PageChange(FormMain.PageSubType id)
    {
        if (pageID == id) return;
        Select(id, false);
        // 让 FormMain 在 PageChangeActual 中基于旧 pageID 判断是否需要切换，
        // 再由它记录新 ID。提前赋值会使同一主页面的子页面切换被误判成重复请求。
        ModMain.frmMain.PageChange(FormMain.PageType.Plugins, id);
    }
}
