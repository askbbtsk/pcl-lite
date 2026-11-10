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
