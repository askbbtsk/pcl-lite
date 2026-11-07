using System.Windows;
using System.Windows.Controls;
using PCL.Core.App.Localization;
using PCL.Core.Plugins;

namespace PCL;

public sealed class PagePluginsInstalled : MyPageRight
{
    private readonly TextBlock _status;
    private readonly StackPanel _content;
    private readonly WrapPanel _pluginPages;
    private readonly ContentControl _pluginPageContent;

    public PagePluginsInstalled()
    {
        var root = PluginPageSupport.CreateRoot(Lang.Text("Plugin.Installed.Title"), out _status, out _content);
        _pluginPages = new WrapPanel();
        _pluginPageContent = new ContentControl { Margin = new Thickness(0, 10, 0, 0) };
        root.Children.Add(PluginPageSupport.Text(Lang.Text("Plugin.Installed.Pages"), 18, true));
        root.Children.Add(_pluginPages);
        root.Children.Add(PluginPageSupport.CreateCard(_pluginPageContent));
        var scroll = new MyScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root
        };
        Child = scroll;
        PanScroll = scroll;
        Loaded += (_, _) => Refresh();
        PluginPageRegistry.Changed += OnPluginPagesChanged;
        Unloaded += (_, _) => PluginPageRegistry.Changed -= OnPluginPagesChanged;
    }

    public void Refresh()
    {
        _content.Children.Clear();
        _pluginPages.Children.Clear();
        _pluginPageContent.Content = null;
        var manager = PluginManagerService.Manager;
        if (manager is null)
        {
            _status.Text = Lang.Text("Plugin.Common.NoManager");
            return;
        }
        var plugins = manager.GetInstalled();
        _status.Text = plugins.Count == 0 ? Lang.Text("Plugin.Installed.Empty") : "";
        foreach (var plugin in plugins)
        {
            var card = new StackPanel();
            card.Children.Add(PluginPageSupport.Text($"{plugin.Manifest.Author}  ·  {plugin.Manifest.Version}"));
            card.Children.Add(PluginPageSupport.Text(plugin.Error is null
                ? (plugin.Running ? Lang.Text("Plugin.Installed.Running") : Lang.Text("Plugin.Installed.Disabled"))
                : Lang.Text("Plugin.Installed.Error", plugin.Error)));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            actions.Children.Add(PluginPageSupport.Button(plugin.Enabled ? Lang.Text("Plugin.Installed.Disable") : Lang.Text("Plugin.Installed.Enable"),
                async (_, _) => await SetEnabledAsync(plugin.Manifest.Id, !plugin.Enabled), true));
            actions.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Installed.Uninstall"),
                async (_, _) => await UninstallAsync(plugin.Manifest.Id, plugin.Manifest.Name)));
            card.Children.Add(actions);
            _content.Children.Add(PluginPageSupport.CreateCard(card, plugin.Manifest.Name));
        }
        RefreshPluginPages();
    }

    private void RefreshPluginPages()
    {
        _pluginPages.Children.Clear();
        foreach (var page in PluginPageRegistry.GetAll())
        {
            var button = PluginPageSupport.Button($"{page.Title}  ·  {page.PluginId}", (_, _) =>
            {
                try { _pluginPageContent.Content = page.CreateView(); }
                catch (Exception ex)
                {
                    PCL.Core.Logging.LogWrapper.Error(ex, "Plugin", $"无法显示插件页 {page.PluginId}/{page.PageId}");
                    _pluginPageContent.Content = PluginPageSupport.Text(ex.Message);
                }
            });
            button.HorizontalAlignment = HorizontalAlignment.Left;
            _pluginPages.Children.Add(button);
        }
    }

    private void OnPluginPagesChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshPluginPages();
        else Dispatcher.BeginInvoke(RefreshPluginPages);
    }

    private async Task SetEnabledAsync(string id, bool enabled)
    {
        var manager = PluginManagerService.Manager;
        if (manager is null) return;
        try
        {
            await manager.SetEnabledAsync(id, enabled);
            PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Installed.Restart"), PCL.Plugin.SDK.PluginNotificationKind.Info);
            Refresh();
        }
        catch (Exception ex) { PluginPageSupport.ShowPluginNotice(ex.Message, PCL.Plugin.SDK.PluginNotificationKind.Error); }
    }

    private async Task UninstallAsync(string id, string name)
    {
        if (ModMain.MyMsgBox(Lang.Text("Plugin.Installed.UninstallConfirm", name), Lang.Text("Plugin.Installed.Uninstall"),
                Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
        var manager = PluginManagerService.Manager;
        if (manager is null) return;
        try
        {
            await manager.UninstallAsync(id);
            PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Installed.Restart"), PCL.Plugin.SDK.PluginNotificationKind.Info);
            Refresh();
        }
        catch (Exception ex) { PluginPageSupport.ShowPluginNotice(ex.Message, PCL.Plugin.SDK.PluginNotificationKind.Error); }
    }
}
