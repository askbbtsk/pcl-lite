using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;
using PCL.Core.UI.Theme;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class AppleLiquidGlassTests
{
    [TestMethod]
    public void PaletteHarmonizesEveryBuiltInPresetWithReadableContrast()
    {
        foreach (var preset in PclThemePresets.All)
        {
            foreach (bool dark in new[] { false, true })
            {
                var p = LiquidGlassMaterial.ComputePalette(
                    preset.Args.Hue, preset.Args.Saturation, preset.Args.LightAdjust, dark);

                Color primaryBg = CompositeOver(p.PrimaryButtonFill, dark ? p.WindowTint : Colors.White);
                double ctaContrast = ContrastRatio(p.PrimaryButtonText, primaryBg);
                Assert.IsTrue(ctaContrast >= 4.4,
                    $"Preset '{preset.Name}' (dark={dark}) CTA contrast: {ctaContrast:F2}");

                double navContrast = ContrastRatio(p.ActionText, CompositeOver(p.NavCheckedFill, p.SidebarTint));
                Assert.IsTrue(navContrast >= 4.5,
                    $"Preset '{preset.Name}' (dark={dark}) nav contrast: {navContrast:F2}");

                double cardContrast = ContrastRatio(p.Text, CompositeOver(p.CardSurface, p.WindowTint));
                Assert.IsTrue(cardContrast >= 7.0,
                    $"Preset '{preset.Name}' (dark={dark}) card contrast: {cardContrast:F2}");
            }
        }

        var pink = LiquidGlassMaterial.ComputePalette(345, 70, 15, false);
        var green = LiquidGlassMaterial.ComputePalette(130, 40, 0, false);
        Assert.IsTrue(pink.Accent.R > pink.Accent.B && pink.Accent.R > pink.Accent.G);
        Assert.IsTrue(green.Accent.G > green.Accent.R && green.Accent.G > green.Accent.B);

        var obsidianDark = LiquidGlassMaterial.ComputePalette(225, 10, -20, true);
        Assert.IsTrue(Math.Abs(obsidianDark.CardSurface.R - obsidianDark.CardSurface.B) <= 8);

        var hc = LiquidGlassMaterial.ComputePalette(210, 80, 0, true, highContrast: true);
        Assert.AreEqual(SystemColors.WindowColor, hc.WindowTint);
        Assert.AreEqual(SystemColors.HotTrackColor, hc.Accent);
        Assert.AreEqual(SystemColors.HighlightTextColor, hc.PrimaryButtonText);
    }

    [TestMethod]
    public void WashOpacitySeparatesDesktopAndWallpaperWithoutBreakingLegacyContract()
    {
        Assert.AreEqual(0.18, LiquidGlassMaterial.SurfaceWashOpacity(false, false, 0.18, 0.28), 1e-6);
        Assert.AreEqual(0.28, LiquidGlassMaterial.SurfaceWashOpacity(false, true, 0.18, 0.28), 1e-6);
        Assert.AreEqual(0.32, LiquidGlassMaterial.SurfaceWashOpacity(true, true, 0.20, 0.32), 1e-6);
        Assert.AreEqual(1.0, LiquidGlassMaterial.SurfaceWashOpacity(false, true, 0.1, 1.5), 1e-6);
        Assert.AreEqual(0.0, LiquidGlassMaterial.SurfaceWashOpacity(false, false, -0.2, 0.2), 1e-6);

        Assert.AreEqual(0.65, LiquidGlassMaterial.Opacity(false, true, 0.10), 1e-6);
        Assert.AreEqual(0.55, LiquidGlassMaterial.Opacity(true, true, 0.10), 1e-6);
    }

    [STATestMethod]
    public void NavigationPillBorderUsesSubtleTopLitHairlineAndHighContrastFallback()
    {
        Assert.AreSame(Brushes.Transparent, LiquidGlassMaterial.NavigationPillBorder(false, false, false));
        Assert.AreSame(SystemColors.HighlightTextBrush, LiquidGlassMaterial.NavigationPillBorder(false, true, true));

        var light = LiquidGlassMaterial.NavigationPillBorder(false, true, false) as LinearGradientBrush;
        Assert.IsNotNull(light);
        Assert.IsTrue(light.IsFrozen);
        Assert.IsTrue(light.GradientStops[0].Color.A > light.GradientStops[1].Color.A);

        var dark = LiquidGlassMaterial.NavigationPillBorder(true, true, false) as LinearGradientBrush;
        Assert.IsNotNull(dark);
        Assert.IsTrue(dark.IsFrozen);
        Assert.IsTrue(dark.GradientStops[0].Color.A < light.GradientStops[0].Color.A);
    }

    [STATestMethod]
    public void DualSamplerAndShaderConstantsBindAndReleaseCleanly()
    {
        var backdrop = new Grid { Background = new LinearGradientBrush(Colors.SteelBlue, Colors.Coral, 30) };
        var surface = new TahoeGlassSurface
        {
            Width = 220, Height = 90,
            BackdropSource = backdrop,
            FrostRadius = 10,
            TintColor = Color.FromRgb(240, 246, 252),
            TintOpacity = 0.22,
            DiffuseStrength = 0.17,
            EdgeSharpness = 0.90,
            IsDarkMode = false
        };
        var host = new Grid();
        host.Children.Add(backdrop);
        host.Children.Add(surface);

        var window = new Window
        {
            Width = 260, Height = 130, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Content = host
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            DrainDispatcher();

            var shader = ReadPrivate<TahoeGlassEffect?>(surface, "_shader");
            if (surface.IsRefractionActive && shader != null)
            {
                Assert.AreNotSame(DependencyProperty.UnsetValue, shader.ReadLocalValue(TahoeGlassEffect.FrostedBackdropProperty));
                Assert.AreEqual(0.17, shader.Material.X, 1e-6);
                Assert.AreEqual(0.90, shader.Material.Y, 1e-6);
                Assert.AreEqual(1.0, shader.Material.Z, 1e-6);
                Assert.AreEqual(0.0, shader.Material.W, 1e-6);
                Assert.AreEqual(0.22, shader.Tint.W, 1e-6);

                surface.FrostRadius = 0;
                surface.RefreshSampling();
                Assert.IsNull(ReadPrivate<Rectangle?>(surface, "_frostSource"));
                Assert.AreEqual(0.0, shader.Material.Z, 1e-6);

                surface.ForceCompatibility = true;
                surface.RefreshSampling();
                Assert.AreSame(DependencyProperty.UnsetValue, shader.ReadLocalValue(TahoeGlassEffect.BackdropProperty));
                Assert.AreSame(DependencyProperty.UnsetValue, shader.ReadLocalValue(TahoeGlassEffect.FrostedBackdropProperty));

                surface.ForceCompatibility = false;
                surface.FrostRadius = 10;
                surface.RefreshSampling();
            }

            surface.BackdropSource = null;
            Assert.IsNull(ReadPrivate<VisualBrush?>(surface, "_frostInput"));
            Assert.IsNull(ReadPrivate<VisualBrush?>(surface, "_frostOutput"));
            Assert.IsNull(ReadPrivate<Rectangle?>(surface, "_frostSource"));
            if (shader != null)
            {
                Assert.AreSame(DependencyProperty.UnsetValue, shader.ReadLocalValue(TahoeGlassEffect.BackdropProperty));
                Assert.AreSame(DependencyProperty.UnsetValue, shader.ReadLocalValue(TahoeGlassEffect.FrostedBackdropProperty));
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static Color CompositeOver(Color fg, Color bg)
    {
        double a = fg.A / 255d;
        return Color.FromRgb(
            (byte)Math.Round(fg.R * a + bg.R * (1 - a)),
            (byte)Math.Round(fg.G * a + bg.G * (1 - a)),
            (byte)Math.Round(fg.B * a + bg.B * (1 - a)));
    }

    private static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte channel)
        {
            double s = channel / 255d;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static T ReadPrivate<T>(TahoeGlassSurface surface, string name) =>
        (T)typeof(TahoeGlassSurface).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(surface)!;

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
