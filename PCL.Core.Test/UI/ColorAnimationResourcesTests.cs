using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Animation;

namespace PCL.Core.Test.UI;

[TestClass]
[DoNotParallelize]
public sealed class ColorAnimationResourcesTests
{
    [STATestMethod]
    public void AnimationUsesPluginLocalThenPageThenWindowResourceLikeDynamicResource()
    {
        var window = new Window();
        var page = new Grid();
        var pluginText = new TextBlock();
        window.Content = page;
        page.Children.Add(pluginText);
        window.Resources["animation-test-color"] = Brushes.White;
        page.Resources["animation-test-color"] = Brushes.Cyan;
        pluginText.Resources["animation-test-color"] = Brushes.Red;
        Assert.AreSame(Brushes.Red, ColorAnimationResources.Resolve(pluginText, "animation-test-color"));
        pluginText.Resources.Remove("animation-test-color");
        Assert.AreSame(Brushes.Cyan, ColorAnimationResources.Resolve(pluginText, "animation-test-color"));
        page.Resources.Remove("animation-test-color");
        Assert.AreSame(Brushes.White, ColorAnimationResources.Resolve(pluginText, "animation-test-color"));
        window.Resources["animation-test-color"] = Brushes.Black;
        Assert.AreSame(Brushes.Black, ColorAnimationResources.Resolve(pluginText, "animation-test-color"));
        Assert.Throws<ResourceReferenceKeyNotFoundException>(() => ColorAnimationResources.Resolve(pluginText, "missing-animation-key"));
    }
}
