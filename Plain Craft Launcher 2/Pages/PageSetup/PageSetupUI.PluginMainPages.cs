using System.Linq;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.Plugins;

namespace PCL;

public partial class PageSetupUI
{
    public void RefreshPluginMainPages()
    {
        if (PanPluginMainPages is null || PanPluginMainPageChecks is null) return;
        var pages = PluginPageRegistry.GetMainPages();
        PanPluginMainPages.Visibility = pages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LabPluginMainPages.Visibility = pages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PanPluginMainPageChecks.Children.Clear();
        foreach (var page in pages)
        {
            var key = page.MainPageKey;
            var check = new MyCheckBox
            {
                Text = page.PluginName + " — " + page.Title,
                Checked = PluginMainPagePreferences.IsHidden(key),
                Tag = key,
                Height = 24,
                Margin = new Thickness(0, 0, 18, 4),
                ToolTip = Lang.Text("Setup.Ui.FeatureHide.PluginPages.ToolTip")
            };
            check.Change += (_, user) =>
            {
                if (!user) return;
                var hidden = check.Checked == true;
                PluginMainPagePreferences.SetHidden(key, hidden);
                if (hidden && !PageSetupUI.HiddenForceShow &&
                    string.Equals(ModMain.frmMain?.pageCurrent.PluginMainPageKey,
                        key, StringComparison.Ordinal))
                    ModMain.frmMain.PageChange(FormMain.PageType.Launch);
                ModMain.frmMain?.RefreshNavigationOverflow();
            };
            PanPluginMainPageChecks.Children.Add(check);
        }
    }
}
