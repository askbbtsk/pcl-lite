using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Plugins;

namespace PCL;

public sealed class PagePluginsMarket : MyPageRight
{
    private readonly TextBlock _status;
    private readonly StackPanel _content;

    public PagePluginsMarket()
    {
        var root = PluginPageSupport.CreateRoot(Lang.Text("Plugin.Market.Title"), out _status, out _content);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        actions.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Market.Refresh"), async (_, _) => await RefreshAsync()));
        actions.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Install.Local"), async (_, _) => await InstallLocalAsync(), true));
        root.Children.Insert(1, PluginPageSupport.CreateCard(new TextBlock
        {
            Text = Lang.Text("Plugin.Market.Description"), TextWrapping = TextWrapping.Wrap, LineHeight = 20, Opacity = 0.9
        }));
        root.Children.Insert(2, actions);
        var scroll = new MyScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root
        };
        Child = scroll;
        PanScroll = scroll;
        Loaded += async (_, _) => await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _content.Children.Clear();
        var manager = PluginManagerService.Manager;
        if (manager is null)
        {
            _status.Text = Lang.Text("Plugin.Common.NoManager");
            return;
        }
        _status.Text = "";
        try
        {
            var index = await manager.GetMarketAsync(new Uri(ProjectLinks.PluginCatalog));
            var candidates = manager.GetMarketUpdateCandidates(index.Plugins);
            if (index.Plugins.Count == 0)
            {
                _status.Text = Lang.Text("Plugin.Market.Empty");
                return;
            }
            foreach (var candidate in candidates)
            {
                var entry = candidate.Entry;
                var stack = new StackPanel();
                stack.Children.Add(PluginPageSupport.Text($"{entry.Author}  ·  {entry.Version}", 12));
                stack.Children.Add(PluginPageSupport.Text(entry.Description));
                var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                switch (candidate.Kind)
                {
                    case PluginMarketUpdateKind.Current:
                        row.Children.Add(PluginPageSupport.Text(Lang.Text("Plugin.Market.Current"), 12));
                        break;
                    case PluginMarketUpdateKind.UpdateAvailable:
                        row.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Market.Update"),
                            async (_, _) => await InstallMarketAsync(entry), true));
                        break;
                    case PluginMarketUpdateKind.DowngradeAvailable:
                        row.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Market.Downgrade"),
                            async (_, _) => await InstallMarketAsync(entry), true));
                        break;
                    default:
                        row.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Market.Install"),
                            async (_, _) => await InstallMarketAsync(entry), true));
                        break;
                }
                if (Uri.TryCreate(entry.Website, UriKind.Absolute, out var website) && website.Scheme == Uri.UriSchemeHttps)
                    row.Children.Add(PluginPageSupport.Button(Lang.Text("Download.Version.OpenWebsite"),
                        (_, _) => ModBase.OpenWebsite(website.AbsoluteUri)));
                stack.Children.Add(row);
                _content.Children.Add(PluginPageSupport.CreateCard(stack, entry.Name));
            }
            _status.Text = "";
        }
        catch (Exception ex)
        {
            _status.Text = Lang.Text("Plugin.Market.LoadFailed", ex.Message);
        }
    }

    private async Task InstallLocalAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = $"PCL plugin package (*{PCL.Plugin.SDK.PluginPackage.Extension})|*{PCL.Plugin.SDK.PluginPackage.Extension}",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        var manager = PluginManagerService.Manager;
        if (manager is null) { PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Common.NoManager"), PCL.Plugin.SDK.PluginNotificationKind.Warning); return; }
        try
        {
            var check = await manager.InstallAsync(dialog.FileName, PluginPageSupport.ConfirmInstallationAsync);
            var note = check.IsSigned ? Lang.Text("Plugin.Install.SignedNote") + " " : "";
            PluginPageSupport.ShowPluginNotice(note + Lang.Text("Plugin.Install.Done"), PCL.Plugin.SDK.PluginNotificationKind.Success);
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Install.Failed", ex.Message), PCL.Plugin.SDK.PluginNotificationKind.Error);
        }
    }

    private async Task InstallMarketAsync(PluginMarketEntry entry)
    {
        var manager = PluginManagerService.Manager;
        if (manager is null) { PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Common.NoManager"), PCL.Plugin.SDK.PluginNotificationKind.Warning); return; }
        try
        {
            var check = await manager.InstallFromMarketAsync(entry, PluginPageSupport.ConfirmInstallationAsync);
            PluginPageSupport.ShowPluginNotice((check.IsSigned ? Lang.Text("Plugin.Install.SignedNote") + " " : "") +
                Lang.Text("Plugin.Install.Done"), PCL.Plugin.SDK.PluginNotificationKind.Success);
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Install.Failed", ex.Message), PCL.Plugin.SDK.PluginNotificationKind.Error);
        }
    }
}
