using System.Linq;
using System.Windows;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.Plugins;

namespace PCL;

public partial class FormMain
{
    private readonly HashSet<string> _pluginMainPageAuthorizationAttempted = new(StringComparer.Ordinal);

    private PluginPage[] GetVisiblePluginMainPages() => PluginPageRegistry.GetMainPages()
        .Where(page => PluginMainPagePreferences.IsAuthorized(page.MainPageKey) &&
                       (PageSetupUI.HiddenForceShow || !PluginMainPagePreferences.IsHidden(page.MainPageKey)))
        .ToArray();

    private void InitializePluginMainPages()
    {
        PluginPageRegistry.Changed += PluginMainPages_Changed;
        Loaded += (_, _) => Dispatcher.BeginInvoke(PromptForPluginMainPages, DispatcherPriority.Loaded);
        Closed += (_, _) => PluginPageRegistry.Changed -= PluginMainPages_Changed;
    }

    private void PluginMainPages_Changed()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (IsLoaded)
            {
                var activeKey = pageCurrent.PluginMainPageKey;
                if (activeKey is not null && !GetVisiblePluginMainPages()
                        .Any(page => string.Equals(page.MainPageKey, activeKey, StringComparison.Ordinal)))
                    PageChange(PageType.Launch);

                RefreshNavigationOverflow();
                ModMain.frmSetupUI?.RefreshPluginMainPages();
                PrunePluginMainPageHosts();
                PromptForPluginMainPages();
            }
        }, DispatcherPriority.Loaded);
    }

    private void PromptForPluginMainPages()
    {
        if (_pluginAuthorizationPromptActive || !IsLoaded || Dispatcher.HasShutdownStarted) return;

        var pending = PluginPageRegistry.GetMainPages()
            .Where(page => !PluginMainPagePreferences.IsAuthorized(page.MainPageKey) &&
                           _pluginMainPageAuthorizationAttempted.Add(page.MainPageKey))
            .GroupBy(page => page.PluginId, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in pending)
        {
            var pages = group.ToArray();
            _pluginAuthorizationPromptActive = true;
            int result;
            try
            {
                result = TimedConfirmation.Show(
                    Lang.Text("Plugin.MainPage.Consent.Content", pages[0].PluginName, pages.Length),
                    Lang.Text("Plugin.MainPage.Consent.Title"),
                    Lang.Text("Common.Action.Confirm"),
                    Lang.Text("Common.Action.Cancel"),
                    seconds: 3);
            }
            finally
            {
                _pluginAuthorizationPromptActive = false;
            }

            if (result == 1)
                PluginMainPagePreferences.Authorize(pages.Select(page => page.MainPageKey));
        }

        RefreshNavigationOverflow();
        ModMain.frmSetupUI?.RefreshPluginMainPages();
    }

    private void PrunePluginMainPageHosts()
    {
        if (_pluginMainPageHosts is null) return;
        var keys = PluginPageRegistry.GetMainPages()
            .Select(page => page.MainPageKey).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _pluginMainPageHosts.Keys.Where(key => !keys.Contains(key)).ToArray())
            _pluginMainPageHosts.Remove(key);
    }
}
