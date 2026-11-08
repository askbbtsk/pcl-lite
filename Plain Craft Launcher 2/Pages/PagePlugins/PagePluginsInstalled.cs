using System.Windows;
using System.Windows.Controls;
using System.IO;
using PCL.Core.App.Localization;
using PCL.Core.Plugins;

namespace PCL;

public sealed class PagePluginsInstalled : MyPageRight
{
    private readonly TextBlock _status;
    private readonly StackPanel _content;
    private readonly WrapPanel _pluginPages;
    private readonly ContentControl _pluginPageContent;
    private PluginPage? _activePluginPage;

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
        Loaded += (_, _) =>
        {
            PluginPageRegistry.Changed += OnPluginPagesChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            PluginPageRegistry.Changed -= OnPluginPagesChanged;
            _ = HideActivePluginPageAsync(clearContent: true);
        };
    }

    public void Refresh()
    {
        _content.Children.Clear();
        _pluginPages.Children.Clear();
        if (_activePluginPage is not null) _ = HideActivePluginPageAsync(clearContent: true);
        else _pluginPageContent.Content = null;
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
            var icon = PluginPageSupport.PluginIcon(
                plugin.Manifest.Icon is null ? null : Path.Combine(plugin.Directory, plugin.Manifest.Icon), false);
            if (icon is not null)
            {
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(icon);
                header.Children.Add(PluginPageSupport.Text($"{GetDisplayName(plugin.Manifest)}  ·  {plugin.Manifest.Author}  ·  {plugin.Manifest.Version}", 12));
                card.Children.Add(header);
            }
            else
            card.Children.Add(PluginPageSupport.Text($"{GetDisplayName(plugin.Manifest)}  ·  {plugin.Manifest.Author}  ·  {plugin.Manifest.Version}"));
            var sourceText = plugin.InstallSourceType switch
            {
                "market" => Lang.Text("Plugin.Installed.Source.Market"),
                "local" => Lang.Text("Plugin.Installed.Source.Local"),
                _ => Lang.Text("Plugin.Installed.Source.Unknown")
            };
            card.Children.Add(PluginPageSupport.Text(Lang.Text("Plugin.Installed.Provenance", sourceText,
                plugin.InstalledAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? Lang.Text("Plugin.Install.UnknownTime"),
                plugin.InstalledPackageSha256 ?? Lang.Text("Plugin.Install.UnknownHash")), 12));
            card.Children.Add(PluginPageSupport.Text(Lang.Text("Plugin.Installed.SigningInfo",
                plugin.Signer ?? Lang.Text("Plugin.Installed.NoSignature"),
                plugin.SignerThumbprint ?? "—",
                plugin.SignedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ??
                Lang.Text(plugin.Signer is null ? "Plugin.Installed.NoSignatureTime" : "Plugin.Install.UnknownTime")), 12));
            card.Children.Add(PluginPageSupport.Text(plugin.Error is null
                ? (plugin.Running ? Lang.Text("Plugin.Installed.Running") : Lang.Text("Plugin.Installed.Disabled"))
                : Lang.Text("Plugin.Installed.Error", plugin.Error)));
            if (plugin.ExperimentalFeatures.Count > 0)
            {
                card.Children.Add(PluginPageSupport.Text(Lang.Text("Plugin.Installed.ExperimentalFeatures"), 14, true));
                foreach (var feature in plugin.ExperimentalFeatures)
                {
                    var featurePanel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
                    var toggle = new MyCheckBox { Text = feature.Manifest.Name, Margin = new Thickness(0, 2, 0, 2) };
                    toggle.SetChecked(feature.Enabled, false);
                    toggle.Change += async (_, user) =>
                    {
                        if (!user) return;
                        await SetExperimentalFeatureEnabledAsync(plugin.Manifest.Id, feature.Manifest.Id,
                            toggle.Checked == true);
                    };
                    featurePanel.Children.Add(toggle);
                    if (!string.IsNullOrWhiteSpace(feature.Manifest.Description))
                        featurePanel.Children.Add(PluginPageSupport.Text(feature.Manifest.Description, 12));
                    if (feature.Error is not null)
                        featurePanel.Children.Add(PluginPageSupport.Text(
                            Lang.Text("Plugin.Installed.ExperimentalFeatureError", feature.Error), 12));
                    card.Children.Add(featurePanel);
                }
            }
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            actions.Children.Add(PluginPageSupport.Button(plugin.Enabled ? Lang.Text("Plugin.Installed.Disable") : Lang.Text("Plugin.Installed.Enable"),
                async (_, _) => await SetEnabledAsync(plugin.Manifest.Id, !plugin.Enabled), true));
            actions.Children.Add(PluginPageSupport.Button(Lang.Text("Plugin.Installed.Uninstall"),
                async (_, _) => await UninstallAsync(plugin.Manifest.Id, plugin.Manifest.Name)));
            card.Children.Add(actions);
            _content.Children.Add(PluginPageSupport.CreateCard(card, GetDisplayName(plugin.Manifest)));
        }
        RefreshPluginPages();
    }

    private static string GetDisplayName(PCL.Plugin.SDK.PluginManifest manifest) =>
        manifest.LocalizedNames.TryGetValue(LocalizationService.CurrentLanguage.Code, out var localized) && !string.IsNullOrWhiteSpace(localized)
            ? localized : manifest.Name;

    private void RefreshPluginPages()
    {
        _pluginPages.Children.Clear();
        var pages = PluginPageRegistry.GetAll();
        if (_activePluginPage is not null && !pages.Contains(_activePluginPage))
            _ = HideActivePluginPageAsync(clearContent: true);
        foreach (var page in pages)
        {
            var button = PluginPageSupport.Button($"{page.Title}  ·  {page.PluginId}",
                async (_, _) => await ShowPluginPageAsync(page));
            button.HorizontalAlignment = HorizontalAlignment.Left;
            _pluginPages.Children.Add(button);
        }
    }

    private async Task ShowPluginPageAsync(PluginPage page)
    {
        if (ReferenceEquals(_activePluginPage, page)) return;
        await HideActivePluginPageAsync(clearContent: true);
        try
        {
            _pluginPageContent.Content = page.GetOrCreateView();
            _activePluginPage = page;
            await page.NotifyShownAsync();
        }
        catch (Exception ex)
        {
            PCL.Core.Logging.LogWrapper.Error(ex, "Plugin", $"无法显示插件页 {page.PluginId}/{page.PageId}");
            _pluginPageContent.Content = PluginPageSupport.Text(ex.Message);
        }
    }

    private async Task HideActivePluginPageAsync(bool clearContent)
    {
        var page = _activePluginPage;
        _activePluginPage = null;
        if (clearContent) _pluginPageContent.Content = null;
        if (page is not null) await page.NotifyHiddenAsync();
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
            await Task.Run(() => manager.SetEnabledAsync(id, enabled));
            Refresh();
            PluginPageSupport.PromptRestartForPluginChanges(Lang.Text("Plugin.Installed.Restart"));
        }
        catch (Exception ex) { PluginPageSupport.ShowPluginNotice(ex.Message, PCL.Plugin.SDK.PluginNotificationKind.Error); }
    }

    private async Task SetExperimentalFeatureEnabledAsync(string pluginId, string featureId, bool enabled)
    {
        var manager = PluginManagerService.Manager;
        if (manager is null) return;
        try
        {
            await Task.Run(() => manager.SetExperimentalFeatureEnabledAsync(pluginId, featureId, enabled));
            Refresh();
            PluginPageSupport.PromptRestartForPluginChanges(Lang.Text("Plugin.Installed.Restart"));
        }
        catch (Exception ex)
        {
            PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Install.Failed", ex.Message),
                PCL.Plugin.SDK.PluginNotificationKind.Error);
            Refresh();
        }
    }

    private async Task UninstallAsync(string id, string name)
    {
        if (ModMain.MyMsgBox(Lang.Text("Plugin.Installed.UninstallConfirm", name), Lang.Text("Plugin.Installed.Uninstall"),
                Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
        var manager = PluginManagerService.Manager;
        if (manager is null) return;
        try
        {
            await Task.Run(() => manager.UninstallAsync(id));
            Refresh();
            PluginPageSupport.PromptRestartForPluginChanges(Lang.Text("Plugin.Installed.Restart"));
        }
        catch (Exception ex) { PluginPageSupport.ShowPluginNotice(ex.Message, PCL.Plugin.SDK.PluginNotificationKind.Error); }
    }
}
