using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
[DoNotParallelize]
public sealed class LiquidGlassBackgroundTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument WindowSource()
    {
        using var source = typeof(LiquidGlassBackgroundTests).Assembly.GetManifestResourceStream("LauncherWindowSource");
        Assert.IsNotNull(source);
        return XDocument.Load(source);
    }

    [TestMethod]
    public void ShippedWindowHasOnlyOneThemeBackgroundOwner()
    {
        var source = WindowSource();
        XElement Named(string name) => source.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);
        var form = Named("PanForm");
        Assert.IsNull(form.Attribute("Background"), "An opaque parent would hide the background slider's result.");
        Assert.IsFalse(form.Elements(Presentation + "Grid.Background").Any());
        var background = Named("LauncherBackgroundLayer");
        Assert.IsNull(background.Attribute("Background"), "ThemeManager owns the brush; do not bind back to an opaque parent.");
        Assert.AreEqual(Named("TahoeDesktopImage").Parent, background.Parent);
        Assert.AreEqual(Named("TahoeWallpaperHost").Parent, background.Parent);
        Assert.IsFalse(Named("PanTitle").Ancestors().Contains(background));
    }

    [STATestMethod]
    public void ActualBackdropFadesThemeAndWallpaperWhileDesktopGlassAndBadgeStayOpaque()
    {
        foreach (bool wallpaperVisible in new[] { false, true })
        {
            // Load the real shipped backdrop, excluding only its application event handler.
            var source = WindowSource();
            var node = new XElement(source.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "TahoeBackdrop"));
            foreach (var media in node.Descendants(Presentation + "MediaElement")) media.Attribute("MediaEnded")?.Remove();
            var backdrop = (Grid)XamlReader.Parse(node.ToString());
            var background = (Border)backdrop.FindName("LauncherBackgroundLayer");
            var wallpaper = (Border)backdrop.FindName("TahoeWallpaperHost");
            var desktop = (Image)backdrop.FindName("TahoeDesktopImage");
            // Cover the full test area; the shipped row split is unrelated to opacity.
            Grid.SetRow(wallpaper, 0);
            Grid.SetRowSpan(wallpaper, 2);
            background.Background = Brushes.White;
            wallpaper.Background = Brushes.Red;
            wallpaper.Visibility = wallpaperVisible ? Visibility.Visible : Visibility.Collapsed;
            desktop.Visibility = Visibility.Visible;
            desktop.Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 255, 0, 0, 255 }, 4);
            var root = new Grid { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top };
            root.Children.Add(backdrop);
            var glass = new Border { Width = 8, Height = 8, Background = Brushes.Green,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var badge = new Border { Width = 8, Height = 8, Background = Brushes.Yellow,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            root.Children.Add(glass);
            root.Children.Add(badge);
            var window = new Window { Content = root, Width = 200, Height = 200, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, Background = Brushes.Transparent };
            try
            {
                window.Show();
                window.UpdateLayout();
                foreach (int value in new[] { 600, 300, 0, 600 })
                {
                    LiquidGlassComposition.Apply(true, value, window, background, wallpaper, desktop);
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var pixels = new byte[64 * 64 * 4];
                    bitmap.CopyPixels(pixels, 64 * 4, 0);
                    int expectedBlue = value == 0 ? 255 : value == 600 ? (wallpaperVisible ? 0 : 255) : (wallpaperVisible ? 127 : 255);
                    int expectedRed = value == 0 ? 0 : value == 600 ? 255 : (wallpaperVisible ? 191 : 128);
                    Assert.AreEqual(expectedBlue, (int)pixels[(20 * 64 + 5) * 4], 2, $"Wallpaper={wallpaperVisible}, value={value}");
                    Assert.AreEqual(expectedRed, (int)pixels[(20 * 64 + 5) * 4 + 2], 2);
                    Assert.AreEqual(1d, window.Opacity);
                    Assert.AreEqual(1d, desktop.Opacity);
                    Assert.AreEqual(1d, glass.Opacity);
                    Assert.AreEqual(1d, badge.Opacity);
                    Assert.AreEqual((byte)128, pixels[(3 * 64 + 60) * 4 + 1], "Glass foreground must not fade.");
                    Assert.AreEqual((byte)255, pixels[(60 * 64 + 60) * 4 + 2], "Badge foreground must not fade.");
                }
                LiquidGlassComposition.Apply(false, 300, window, background, wallpaper, desktop);
                Assert.AreEqual(.7d, window.Opacity, 1e-10);
                Assert.AreEqual(1d, background.Opacity);
                Assert.AreEqual(1d, wallpaper.Opacity);
            }
            finally { window.Close(); }
        }
    }
}
