using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Controls;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class ControlLifetimeRegressionTests
{
    [STATestMethod]
    public void WaterfallPanelReturnsFiniteSizeInsideHorizontalScrolling()
    {
        var panel = new WaterfallPanel { ColumnCount = 2, MaxItemWidth = 120 };
        panel.Children.Add(new Border { Height = 30 });
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.IsTrue(double.IsFinite(panel.DesiredSize.Width));
        Assert.AreEqual(245d, panel.DesiredSize.Width);
        Assert.AreEqual(30d, panel.DesiredSize.Height);
    }

    [STATestMethod]
    public void MotdAnimationStopsWhenTheControlUnloadsAndRestartsOnLoad()
    {
        var renderer = new MotdRenderer();
        var timer = (DispatcherTimer)typeof(MotdRenderer)
            .GetField("_obfuscationTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
        Assert.IsFalse(timer.IsEnabled);
        renderer.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.IsTrue(timer.IsEnabled);
        renderer.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.IsFalse(timer.IsEnabled);
    }
}
