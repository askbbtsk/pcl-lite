using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.ResourceProject;

namespace PCL;

public partial class MyLocalCompItem
{
    internal ModLoaderCompatibilityContext? LoaderCompatibilityContext { get; set; }
    private string? _loaderCompatibilityTooltip;

    private void RefreshLoaderCompatibility()
    {
        // Always rebuild the baseline tags so toggling off cannot leave an old marker behind.
        var tags = Entry.IsFolder
            ? new List<string> { Lang.Text("Instance.Resource.Item.FolderTag") }
            : (Entry.Comp?.Tags ?? Entry.Tags).ToList();
        if (_loaderCompatibilityTooltip is not null && Equals(ToolTip, _loaderCompatibilityTooltip))
            ClearValue(ToolTipProperty);
        _loaderCompatibilityTooltip = null;
        try
        {
            var context = LoaderCompatibilityContext;
            if (!Entry.IsFolder && Config.Launch.MarkIncompatibleMods && context is not null
                && !string.IsNullOrWhiteSpace(Entry.ModId) && context.IsCurrent(Entry)
                && ModLoaderCompatibility.Evaluate(Entry.DeclaredLoaders, context.Loaders, context.HasBridge)
                    == ModLoaderCompatibilityResult.DifferentLoader)
            {
                tags.Add(Lang.Text("Instance.Resource.Item.LoaderMismatch"));
                _loaderCompatibilityTooltip = Lang.Text("Instance.Resource.Item.LoaderMismatch.Detail",
                    string.Join(" / ", Entry.DeclaredLoaders), string.Join(" / ", context.Loaders));
                ToolTip = _loaderCompatibilityTooltip;
                ToolTipService.SetShowDuration(this, 30000);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "可选模组加载器标注失败", ModBase.LogLevel.Developer);
        }
        Tags = tags;
    }
}
