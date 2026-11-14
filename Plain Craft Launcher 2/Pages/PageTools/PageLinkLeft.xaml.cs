using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageLinkLeft
{
    public FormMain.PageSubType pageID = FormMain.PageSubType.LinkHost;
    private bool _isUpdatingSelection;
    private Brush? _brushReady;
    private Brush? _brushIdle;

    public PageLinkLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        Loaded += (_, _) =>
        {
            Select(pageID, false);
            ModMain.frmToolsDirectLink?.PushSidebarStatus();
        };
    }

    private void PageCheck(object sender, ModBase.RouteEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (sender is MyListItem { Tag: not null } item)
            PageChange((FormMain.PageSubType)ModBase.Val(item.Tag));
    }

    public object PageGet(FormMain.PageSubType id)
    {
        ModMain.frmToolsDirectLink ??= new PageToolsDirectLink();
        var normalized = id == FormMain.PageSubType.LinkGuest
            ? FormMain.PageSubType.LinkGuest
            : FormMain.PageSubType.LinkHost;
        ModMain.frmToolsDirectLink.SetMode(normalized);
        return ModMain.frmToolsDirectLink;
    }

    public void Select(FormMain.PageSubType id, bool animate)
    {
        var item = id == FormMain.PageSubType.LinkGuest ? ItemGuest : ItemHost;
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

    public void UpdateSidebarStatus(bool ready, string statusText, string detailText)
    {
        if (!CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateSidebarStatus(ready, statusText, detailText));
            return;
        }

        _brushReady ??= (Brush)FindResource("ColorBrush3");
        _brushIdle ??= (Brush)FindResource("ColorBrushGray4");
        ShapeStatusDot.Fill = ready ? _brushReady : _brushIdle;
        LabStatus.Text = statusText;
        LabDetail.Text = detailText;
    }

    private void PageChange(FormMain.PageSubType id)
    {
        if (pageID == id) return;
        Select(id, false);
        if (ModMain.frmMain.pageCurrent.page == FormMain.PageType.Link)
        {
            pageID = id;
            ModMain.frmToolsDirectLink ??= new PageToolsDirectLink();
            ModMain.frmToolsDirectLink.SetMode(id);
            return;
        }
        ModMain.frmMain.PageChange(FormMain.PageType.Link, id);
    }

    private void BtnRefreshNet_Click(object sender, MouseButtonEventArgs e)
    {
        ModMain.frmToolsDirectLink ??= new PageToolsDirectLink();
        ModMain.frmToolsDirectLink.RefreshIpv6ByUser();
    }
}
