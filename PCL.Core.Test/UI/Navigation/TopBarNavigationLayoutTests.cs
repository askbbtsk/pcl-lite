using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Navigation;

namespace PCL.Core.Test.UI.Navigation;

[TestClass]
public sealed class TopBarNavigationLayoutTests
{
    private static readonly TopBarNavigationItem[] SevenTabs =
    [
        new(0, 80), new(1, 90), new(2, 100), new(13, 85),
        new(4, 90), new(3, 90), new(14, 90)
    ];

    [TestMethod]
    public void AllTabsRemainVisibleWhenMeasuredWidthFits()
    {
        var collapsed = TopBarNavigationLayout.SelectCollapsedTabs(SevenTabs,
            new HashSet<int> { 3, 14 }, availableWidth: 640, overflowButtonWidth: 32);

        Assert.AreEqual(0, collapsed.Count);
    }

    [TestMethod]
    public void PreferredTabsCollapseOnlyWhenTheCurrentWidthRequiresIt()
    {
        var collapsed = TopBarNavigationLayout.SelectCollapsedTabs(SevenTabs,
            new HashSet<int> { 3, 14 }, availableWidth: 540, overflowButtonWidth: 32);

        Assert.IsTrue(collapsed.Count > 0);
        CollectionAssert.AreEquivalent(new[] { 3, 14 }, collapsed.ToArray());
        Assert.IsFalse(collapsed.Contains(TopBarNavigationLayout.LaunchTabId));
        var visibleWidth = SevenTabs.Where(tab => !collapsed.Contains(tab.Id)).Sum(tab => tab.Width) + 32;
        Assert.IsTrue(visibleWidth <= 540);
    }

    [TestMethod]
    public void FiveOrFewerTabsAreNeverAutomaticallyCollapsed()
    {
        var collapsed = TopBarNavigationLayout.SelectCollapsedTabs(SevenTabs.Take(5).ToArray(),
            new HashSet<int> { 1, 2, 3 }, availableWidth: 1, overflowButtonWidth: 32);

        Assert.AreEqual(0, collapsed.Count);
    }

    [TestMethod]
    public void NarrowWidthCollapsesOptionalTabsButKeepsLaunch()
    {
        var collapsed = TopBarNavigationLayout.SelectCollapsedTabs(SevenTabs,
            new HashSet<int>(), availableWidth: 120, overflowButtonWidth: 32);

        CollectionAssert.AreEquivalent(new[] { 1, 2, 13, 4, 3, 14 }, collapsed.ToArray());
    }

}
