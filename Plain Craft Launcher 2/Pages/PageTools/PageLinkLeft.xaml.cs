namespace PCL;

public partial class PageLinkLeft
{
    public FormMain.PageSubType pageID = FormMain.PageSubType.LinkHost;
    private bool _isUpdatingSelection;

    public PageLinkLeft()
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
            item.SetChecked(true, user: false, anime: animate);
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
        if (ModMain.frmMain.pageCurrent.page == FormMain.PageType.Link)
        {
            pageID = id;
            ModMain.frmToolsDirectLink ??= new PageToolsDirectLink();
            ModMain.frmToolsDirectLink.SetMode(id);
            return;
        }
        ModMain.frmMain.PageChange(FormMain.PageType.Link, id);
    }
}
