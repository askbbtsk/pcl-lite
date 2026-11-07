using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.UI.Navigation;

/// <summary>One top-bar tab and its measured outer width, including margins.</summary>
public readonly record struct TopBarNavigationItem(int Id, double Width);

/// <summary>Chooses which optional tabs must move into the overflow menu at the current width.</summary>
public static class TopBarNavigationLayout
{
    public const int LaunchTabId = 0;

    public static IReadOnlySet<int> SelectCollapsedTabs(
        IReadOnlyList<TopBarNavigationItem> tabs,
        IReadOnlySet<int> preferredCollapsedIds,
        double availableWidth,
        double overflowButtonWidth,
        int minimumVisibleTabCount = 5)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(preferredCollapsedIds);
        if (minimumVisibleTabCount < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumVisibleTabCount));
        if (!double.IsFinite(availableWidth))
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        if (!double.IsFinite(overflowButtonWidth) || overflowButtonWidth < 0d)
            throw new ArgumentOutOfRangeException(nameof(overflowButtonWidth));

        var ids = new HashSet<int>();
        foreach (var tab in tabs)
        {
            if (!ids.Add(tab.Id))
                throw new ArgumentException($"Duplicate top-bar tab id: {tab.Id}.", nameof(tabs));
            if (!double.IsFinite(tab.Width) || tab.Width < 0d)
                throw new ArgumentOutOfRangeException(nameof(tabs), "Tab widths must be finite and non-negative.");
        }

        if (tabs.Count <= minimumVisibleTabCount)
            return new HashSet<int>();

        var totalWidth = tabs.Sum(tab => tab.Width);
        if (totalWidth <= Math.Max(0d, availableWidth))
            return new HashSet<int>();

        var candidates = tabs
            .Where(tab => tab.Id != LaunchTabId && preferredCollapsedIds.Contains(tab.Id))
            .Reverse()
            .Concat(tabs.Where(tab => tab.Id != LaunchTabId && !preferredCollapsedIds.Contains(tab.Id)).Reverse());
        var collapsed = new HashSet<int>();
        foreach (var tab in candidates)
        {
            collapsed.Add(tab.Id);
            totalWidth -= tab.Width;
            if (totalWidth + overflowButtonWidth <= Math.Max(0d, availableWidth))
                break;
        }

        return collapsed;
    }
}
