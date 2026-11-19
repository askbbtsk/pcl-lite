using System.Windows;
using PCL.Core.Plugins;

namespace PCL;

/// <summary>Adapts a registered plugin main page to the launcher's standard page lifecycle.</summary>
internal sealed class PluginMainPageHost : MyPageRight
{
    private readonly PluginPage _page;

    public PluginMainPageHost(PluginPage page)
    {
        _page = page;
        Child = page.GetOrCreateView();
        PageEnter += () => _ = _page.NotifyShownAsync();
        PageExit += () => _ = _page.NotifyHiddenAsync();
    }
}
