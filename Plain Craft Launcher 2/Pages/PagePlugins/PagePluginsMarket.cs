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
    private bool _installationInProgress;

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
        if (_installationInProgress) return;
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
                var icon = new Image { Width = 42, Height = 42,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                if (!string.IsNullOrWhiteSpace(entry.Icon))
                {
                    var header = new StackPanel { Orientation = Orientation.Horizontal };
                    header.Children.Add(icon);
                    header.Children.Add(PluginPageSupport.Text($"{GetDisplayName(entry)}  ·  {entry.Author}  ·  {entry.Version}", 12));
                    stack.Children.Add(header);
                }
                else
                stack.Children.Add(PluginPageSupport.Text($"{GetDisplayName(entry)}  ·  {entry.Author}  ·  {entry.Version}", 12));
                stack.Children.Add(PluginPageSupport.Text(GetDescription(entry)));
                var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                switch (candidate.Kind)
                {
                    case PluginMarketUpdateKind.Current:
                        row.Children.Add(PluginPageSupport.Text(Lang.Text("Plugin.Market.Current"), 12));
                        break;
                    case PluginMarketUpdateKind.UpdateAvailable:
                        row.Children.Add(CreateInstallButton(entry, Lang.Text("Plugin.Market.Update")));
                        break;
                    case PluginMarketUpdateKind.DowngradeAvailable:
                        row.Children.Add(CreateInstallButton(entry, Lang.Text("Plugin.Market.Downgrade")));
                        break;
                    default:
                        row.Children.Add(CreateInstallButton(entry, Lang.Text("Plugin.Market.Install")));
                        break;
                }
                if (Uri.TryCreate(entry.Website, UriKind.Absolute, out var website) && website.Scheme == Uri.UriSchemeHttps)
                    row.Children.Add(PluginPageSupport.Button(Lang.Text("Download.Version.OpenWebsite"),
                        (_, _) => ModBase.OpenWebsite(website.AbsoluteUri)));
                stack.Children.Add(row);
                var card = PluginPageSupport.CreateCard(stack, GetDisplayName(entry));
                _content.Children.Add(card);
                if (!string.IsNullOrWhiteSpace(entry.Icon))
                    _ = PluginPageSupport.LoadRemoteIconIntoAsync(entry.Icon, icon);
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
        if (_installationInProgress) return;
        var dialog = new OpenFileDialog
        {
            Filter = $"PCL plugin package (*{PCL.Plugin.SDK.PluginPackage.Extension})|*{PCL.Plugin.SDK.PluginPackage.Extension}",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        var manager = PluginManagerService.Manager;
        if (manager is null) { PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Common.NoManager"), PCL.Plugin.SDK.PluginNotificationKind.Warning); return; }
        _installationInProgress = true;
        _status.Text = Lang.Text("Plugin.Install.Working");
        try
        {
            var check = await Task.Run(() => manager.InstallAsync(dialog.FileName,
                PluginPageSupport.ConfirmInstallationAsync));
            var note = check.IsSigned ? Lang.Text("Plugin.Install.SignedNote") + " " : "";
            PluginPageSupport.PromptRestartForPluginChanges(note + Lang.Text("Plugin.Install.Done"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Install.Failed", ex.Message), PCL.Plugin.SDK.PluginNotificationKind.Error);
        }
        finally
        {
            _installationInProgress = false;
            _status.Text = "";
        }
    }

    private MyButton CreateInstallButton(PluginMarketEntry entry, string label)
    {
        MyButton button = null!;
        button = PluginPageSupport.Button(label, async (_, _) => await InstallMarketAsync(entry, button), true);
        return button;
    }

    private static string GetDisplayName(PluginMarketEntry entry) =>
        entry.LocalizedNames.TryGetValue(LocalizationService.CurrentLanguage.Code, out var localized) && !string.IsNullOrWhiteSpace(localized)
            ? localized : entry.Name;

    private static string GetDescription(PluginMarketEntry entry) =>
        entry.LocalizedDescriptions.TryGetValue(LocalizationService.CurrentLanguage.Code, out var localized) && !string.IsNullOrWhiteSpace(localized)
            ? localized : entry.Description;

    private async Task InstallMarketAsync(PluginMarketEntry entry, MyButton button)
    {
        if (_installationInProgress) return;
        var manager = PluginManagerService.Manager;
        if (manager is null) { PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Common.NoManager"), PCL.Plugin.SDK.PluginNotificationKind.Warning); return; }
        _installationInProgress = true;
        button.IsEnabled = false;
        _status.Text = Lang.Text("Plugin.Install.Working");
        try
        {
            var check = await Task.Run(() => manager.InstallFromMarketAsync(entry,
                PluginPageSupport.ConfirmInstallationAsync));
            button.Text = Lang.Text("Plugin.Market.PendingRestart");
            PluginPageSupport.PromptRestartForPluginChanges(
                (check.IsSigned ? Lang.Text("Plugin.Install.SignedNote") + " " : "") + Lang.Text("Plugin.Install.Done"));
        }
        catch (OperationCanceledException) { button.IsEnabled = true; }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Install.Failed", ex.Message), PCL.Plugin.SDK.PluginNotificationKind.Error);
        }
        finally
        {
            _installationInProgress = false;
            _status.Text = "";
        }
    }
}
