using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
[DoNotParallelize]
public sealed class TahoeGlassSurfaceTests
{
    [STATestMethod]
    public void FrostUsesSmoothGaussianInsteadOfSeparatedShaderSamplesAndReleasesItsSource()
    {
        WithWindow((window, host, backdrop, surface) =>
        {
            surface.BlurRadius = 0;
            surface.RefractionStrength = 0;
            surface.TintOpacity = 0;
            surface.HighlightStrength = 0;
            var tile = new DrawingGroup();
            tile.Children.Add(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            tile.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(4, 0, 4, 8))));
            backdrop.Background = new DrawingBrush(tile) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 8, 8), Stretch = Stretch.Fill };
            surface.ForceCompatibility = true;
            double Contrast(int frost, bool disableBlur = false)
            {
                surface.FrostRadius = frost;
                surface.RefreshSampling();
                window.UpdateLayout();
                DrainDispatcher();
                // Negative control: the metric must detect removing the actual
                // compatibility blur, rather than merely accepting a tinted image.
                var material = ReadField<Rectangle>(surface, "_surface");
                var effect = material.Effect;
                if (disableBlur) material.Effect = null;
                var image = new System.Windows.Media.Imaging.RenderTargetBitmap(180, 60, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                var origin = surface.TransformToAncestor(host).Transform(new Point());
                var visual = new DrawingVisual();
                // Crop in host coordinates. Sampling the surface directly uses
                // descendant bounds (shadow + blur padding), stretching a different
                // area for each frost radius and aliasing the stripe fixture.
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(new VisualBrush(host)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(origin, surface.RenderSize),
                        Stretch = Stretch.Fill
                    }, null, new Rect(0, 0, 180, 60));
                image.Render(visual);
                if (disableBlur) material.Effect = effect;
                var pixels = new byte[180 * 60 * 4]; image.CopyPixels(pixels, 180 * 4, 0);
                double sum = 0;
                for (int x = 30; x < 149; x++)
                    sum += Math.Abs(pixels[(30 * 180 + x) * 4] - pixels[(30 * 180 + x + 1) * 4]);
                return sum / 119;
            }
            double clear = Contrast(0), frosted = Contrast(8);
            Assert.IsTrue(clear > 20, $"The clear material must retain the stripe detail: {clear}, frosted: {frosted}, state: {surface.RenderStatus}");
            Assert.IsTrue(frosted < clear * .35, $"Frost must smooth the source: {clear} -> {frosted}");
            double unblurred = Contrast(8, disableBlur: true);
            Assert.IsTrue(unblurred > clear * .65,
                $"Removing the blur must restore stripe detail: clear={clear}, frosted={frosted}, bypass={unblurred}");
            surface.ForceCompatibility = false;
            surface.RefreshSampling();
            DrainDispatcher();
            if (surface.IsRefractionActive)
            {
                Assert.IsNotNull(ReadField<System.Windows.Shapes.Rectangle?>(surface, "_frostSource"));
                surface.FrostRadius = 0;
                surface.RefreshSampling();
                Assert.IsNull(ReadField<System.Windows.Shapes.Rectangle?>(surface, "_frostSource"));
                surface.FrostRadius = 8;
                surface.RefreshSampling();
            }
            surface.BackdropSource = null;
            Assert.IsNull(ReadField<VisualBrush?>(surface, "_frostInput"));
            Assert.IsNull(ReadField<System.Windows.Shapes.Rectangle?>(surface, "_frostSource"));
        });
    }

    [STATestMethod]
    public void OffscreenGlassStopsWritingMaterialAndAllowsDispatcherIdle()
    {
        WithWindow((window, _, _, surface) =>
        {
            // Scroll viewers keep out-of-viewport children loaded and IsVisible=true.
            surface.RenderTransform = new TranslateTransform(2000, 0);
            window.UpdateLayout();
            surface.RefreshSampling();
            Assert.AreEqual("Static", surface.RenderStatus);
            Rectangle rectangle = ReadField<Rectangle>(surface, "_surface");
            Brush material = rectangle.Fill;
            for (int i = 0; i < 50; i++) surface.RefreshSampling();
            Assert.AreSame(material, rectangle.Fill, "An unchanged empty crop must not allocate a new brush on every layout notification.");
            DrainDispatcher();
            Assert.AreSame(material, rectangle.Fill, "Rendering a static fallback must not invalidate itself.");

            surface.RenderTransform = Transform.Identity;
            DrainDispatcher();
            Assert.AreNotEqual("Static", surface.RenderStatus, "Returning into the source viewport must resume backdrop sampling.");
        });
    }

    [STATestMethod]
    public void ShaderSamplerCanBeReleasedWithoutAnInvalidSolidColorBrush()
    {
        var effect = new TahoeGlassEffect { Backdrop = new VisualBrush(new Grid()) };
        effect.ClearValue(TahoeGlassEffect.BackdropProperty);
        Assert.AreSame(DependencyProperty.UnsetValue, effect.ReadLocalValue(TahoeGlassEffect.BackdropProperty));
    }

    [STATestMethod]
    public void HidingUnloadingAndRemovingBackdropReleaseAllObserversAndSampler()
    {
        WithWindow((window, host, backdrop, surface) =>
        {
            for (int iteration = 0; iteration < 4; iteration++)
            {
                EnsureShaderExists(surface);
                surface.Visibility = Visibility.Collapsed;
                DrainDispatcher();
                AssertReleased(surface);

                surface.Visibility = Visibility.Visible;
                DrainDispatcher();
                Assert.IsTrue(ReadField<bool>(surface, "_tracking"));
                Assert.IsTrue(surface.IsLoaded);

                EnsureShaderExists(surface);
                surface.BackdropSource = null;
                DrainDispatcher();
                Assert.IsFalse(surface.IsRefractionActive);
                Assert.AreEqual("Static", surface.RenderStatus);
                AssertSamplerReleased(surface);

                surface.BackdropSource = backdrop;
                DrainDispatcher();
                EnsureShaderExists(surface);
                host.Children.Remove(surface);
                DrainDispatcher();
                AssertReleased(surface);

                host.Children.Add(surface);
                DrainDispatcher();
                Assert.IsTrue(ReadField<bool>(surface, "_tracking"));
            }

            EnsureShaderExists(surface);
            window.Hide();
            DrainDispatcher();
            AssertReleased(surface);
            window.Show();
            DrainDispatcher();
            Assert.IsTrue(ReadField<bool>(surface, "_tracking"));
        });
    }

    [STATestMethod]
    public void CompatibilityAndInvalidSourcesAreReversibleWithoutSamplerExceptions()
    {
        WithWindow((_, host, backdrop, surface) =>
        {
            for (int iteration = 0; iteration < 3; iteration++)
            {
                EnsureShaderExists(surface);
                surface.ForceCompatibility = true;
                DrainDispatcher();
                Assert.IsFalse(surface.IsRefractionActive);
                Assert.AreEqual("Compatibility", surface.RenderStatus);
                AssertSamplerReleased(surface);

                surface.ForceCompatibility = false;
                DrainDispatcher();
                EnsureShaderExists(surface);
                surface.BackdropSource = host; // Ancestor sampling would recursively paint this glass.
                DrainDispatcher();
                Assert.IsFalse(surface.IsRefractionActive);
                Assert.AreEqual("Static", surface.RenderStatus);
                AssertSamplerReleased(surface);

                surface.BackdropSource = surface;
                DrainDispatcher();
                Assert.AreEqual("Static", surface.RenderStatus);
                surface.BackdropSource = backdrop;
                DrainDispatcher();
                Assert.IsTrue(surface.IsLoaded);
            }
        });
    }

    private static void WithWindow(Action<Window, Grid, Grid, TahoeGlassSurface> action)
    {
        var host = new Grid();
        var backdrop = new Grid { Background = new LinearGradientBrush(Colors.SteelBlue, Colors.Coral, 30) };
        var surface = new TahoeGlassSurface
        {
            Width = 180, Height = 60, BackdropSource = backdrop,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        host.Children.Add(backdrop);
        host.Children.Add(surface);
        var window = new Window
        {
            Width = 400, Height = 240, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Content = host
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            DrainDispatcher();
            Assert.IsTrue(surface.IsLoaded);
            Assert.IsTrue(surface.IsVisible);
            action(window, host, backdrop, surface);
        }
        finally
        {
            window.Close();
            DrainDispatcher();
        }
    }

    private static void EnsureShaderExists(TahoeGlassSurface surface)
    {
        // Exercise the sampler-release regression even on a software-only CI machine.
        // Loading bytecode and clearing a sampler do not require GPU rendering support.
        FieldInfo field = typeof(TahoeGlassSurface).GetField("_shader", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (field.GetValue(surface) == null) field.SetValue(surface, new TahoeGlassEffect());
    }

    private static T ReadField<T>(TahoeGlassSurface surface, string name) =>
        (T)typeof(TahoeGlassSurface).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(surface)!;

    private static void AssertSamplerReleased(TahoeGlassSurface surface)
    {
        TahoeGlassEffect? shader = ReadField<TahoeGlassEffect?>(surface, "_shader");
        if (shader != null)
            Assert.AreSame(DependencyProperty.UnsetValue, shader.ReadLocalValue(TahoeGlassEffect.BackdropProperty));
    }

    private static void AssertReleased(TahoeGlassSurface surface)
    {
        Assert.IsFalse(surface.IsRefractionActive);
        Assert.IsFalse(ReadField<bool>(surface, "_tracking"));
        Assert.AreEqual(0, ReadField<ICollection>(surface, "_observedElements").Count);
        Assert.AreEqual(0, ReadField<ICollection>(surface, "_observedParents").Count);
        Assert.AreEqual(0, ReadField<HashSet<Transform>>(surface, "_observedTransforms").Count);
        Assert.IsNull(ReadField<FrameworkElement?>(surface, "_subscribedSource"));
        Assert.IsNull(ReadField<VisualBrush?>(surface, "_backdrop"));
        Assert.IsNull(ReadField<VisualBrush?>(surface, "_compatibilityBackdrop"));
        AssertSamplerReleased(surface);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        bool reachedIdle = false;
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(2) };
        timeout.Tick += (_, _) => frame.Continue = false;
        timeout.Start();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            reachedIdle = true;
            frame.Continue = false;
        }));
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Assert.IsTrue(reachedIdle, "The dispatcher did not become idle; a material may be invalidating layout continuously.");
    }
}
