using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
[DoNotParallelize]
public sealed class LiquidGlassTextRestoreTests
{
    public sealed class Appearance : DependencyObject
    {
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.Register(
            nameof(IsEnabled), typeof(bool), typeof(Appearance), new PropertyMetadata(false));
        public bool IsEnabled { get => (bool)GetValue(IsEnabledProperty); set => SetValue(IsEnabledProperty, value); }
    }

    [STATestMethod]
    public void SidebarHeadingsPathsAndSubtitlesStayReadableAfterGlassIsTurnedOff()
    {
        using var stream = typeof(LiquidGlassTextRestoreTests).Assembly.GetManifestResourceStream("LiquidGlassStylesSource");
        Assert.IsNotNull(stream);
        var source = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var key in new[] { "TahoeSidebarHeadingStyle", "TahoeSidebarPathStyle", "TahoeLaunchSubtitleStyle",
                     "TahoeSettingsCaptionStyle" })
        {
            var styleNode = new XElement(source.Root!.Elements(presentation + "Style")
                .Single(element => (string?)element.Attribute(xaml + "Key") == key));
            // Test the actual shipped setters/triggers; supply an isolated view model instead of the
            // launcher's singleton, whose startup and network services do not belong in a style test.
            foreach (var trigger in styleNode.Descendants(presentation + "DataTrigger"))
                trigger.SetAttributeValue("Binding", "{Binding IsEnabled}");
            var style = (Style)XamlReader.Parse(styleNode.ToString());
            var appearance = new Appearance();
            var text = new TextBlock { Text = "状态 / 物料目录", DataContext = appearance, Style = style };
            var window = new Window { Width = 200, Height = 100, Content = text, ShowInTaskbar = false,
                ShowActivated = false, Left = -10000, Top = -10000 };
            var normal = Color.FromRgb(224, 228, 235);
            var glass = Color.FromRgb(185, 190, 200);
            window.Resources["ColorBrushGray1"] = new SolidColorBrush(normal);
            window.Resources["TahoeSecondaryText"] = new SolidColorBrush(glass);
            window.Resources["TahoeActionText"] = new SolidColorBrush(glass);
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.AreEqual(normal, ((SolidColorBrush)text.Foreground).Color, key);
                appearance.IsEnabled = true;
                window.UpdateLayout();
                Assert.AreEqual(glass, ((SolidColorBrush)text.Foreground).Color, key);
                appearance.IsEnabled = false;
                window.UpdateLayout();
                Assert.AreEqual(normal, ((SolidColorBrush)text.Foreground).Color, key + " must not fall back to black.");
                var light = Color.FromRgb(50, 55, 65);
                window.Resources["ColorBrushGray1"] = new SolidColorBrush(light);
                window.UpdateLayout();
                Assert.AreEqual(light, ((SolidColorBrush)text.Foreground).Color, key + " must follow later theme changes.");
            }
            finally { window.Close(); }
        }
    }
}
