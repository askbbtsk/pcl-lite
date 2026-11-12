using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class LiquidGlassWindowClipTests
{
    // Hidden native windows only: no foreground activation, screen access or visible UI.
    [TestMethod]
    public void NativeWindowExcludesClientMarginAndAllFourCornersAtEveryDpi()
    {
        OnSta(() => WithWindow(window =>
        {
            using var lease = new LiquidGlassWindowRegion(window);
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
            foreach (int width in new[] { 300, 900, 1400 })
            {
                var transform = new Matrix(scale, 0, 0, scale, 10 * scale, 10 * scale);
                lease.SetOutline(new Rect(0, 0, width, 250), 22, 22, transform);
                WithRegion(window, region =>
                {
                    Assert.IsFalse(PtInRegion(region, 0, 0), "The unused HWND margin used to protrude as a black block.");
                    Assert.IsFalse(PtInRegion(region, (int)(9 * scale), (int)(135 * scale)));
                    foreach (double x in new[] { 11 * scale, (width + 9) * scale })
                    foreach (double y in new[] { 11 * scale, 259 * scale })
                        Assert.IsFalse(PtInRegion(region, (int)x, (int)y));
                    Assert.IsTrue(PtInRegion(region, (int)((width / 2 + 10) * scale), (int)(135 * scale)));
                    // Resize hit testing at the midpoint of the visible edge remains reachable.
                    Assert.IsTrue(PtInRegion(region, (int)(10 * scale), (int)(135 * scale)));
                });
            }
        }));
    }

    [TestMethod]
    public void StartupRotationIsAppliedAndWindowChromeCannotReplaceOurOutline()
    {
        OnSta(() => WithWindow(window =>
        {
            using var lease = new LiquidGlassWindowRegion(window);
            var transform = Matrix.Identity;
            transform.Rotate(-4);
            transform.Translate(40, 70);
            lease.SetOutline(new Rect(0, 0, 300, 200), 22, 22, transform);
            WithRegion(window, region => Assert.IsFalse(PtInRegion(region, 20, 20)));
            IntPtr replacement = CreateRectRgn(0, 0, 1000, 1000);
            Assert.AreNotEqual(0, SetWindowRgn(window, replacement, false));
            lease.EnsureApplied();
            WithRegion(window, region =>
            {
                Assert.IsFalse(PtInRegion(region, 20, 20));
                Point center = transform.Transform(new Point(150, 100));
                Assert.IsTrue(PtInRegion(region, (int)center.X, (int)center.Y));
            });
        }));
    }

    [TestMethod]
    public void DisableRestoresOriginalRegionAndRepeatedEnableDoesNotLeakClipping()
    {
        OnSta(() => WithWindow(window =>
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                using (var lease = new LiquidGlassWindowRegion(window))
                    lease.SetOutline(new Rect(10, 10, 300, 200), 22, 22, Matrix.Identity);
                WithRegion(window, region => Assert.AreEqual(0, GetWindowRgn(window, region)));
            }
            IntPtr original = CreateRectRgn(2, 3, 600, 400);
            Assert.AreNotEqual(0, SetWindowRgn(window, original, false));
            using (var lease = new LiquidGlassWindowRegion(window))
                lease.SetOutline(new Rect(10, 10, 300, 200), 22, 22, Matrix.Identity);
            WithRegion(window, region =>
            {
                Assert.IsTrue(PtInRegion(region, 2, 3));
                Assert.IsFalse(PtInRegion(region, 1, 3));
                Assert.IsTrue(PtInRegion(region, 599, 399));
            });
        }));
    }

    [TestMethod]
    public void RestoresLatestWindowChromeRegionAfterResizeRatherThanStartupRegion()
    {
        OnSta(() => WithWindow(window =>
        {
            IntPtr original = CreateRectRgn(0, 0, 300, 200);
            Assert.AreNotEqual(0, SetWindowRgn(window, original, false));
            using (var lease = new LiquidGlassWindowRegion(window))
            {
                lease.SetOutline(new Rect(10, 10, 280, 180), 22, 22, Matrix.Identity);
                IntPtr resized = CreateRectRgn(0, 0, 900, 550);
                Assert.AreNotEqual(0, SetWindowRgn(window, resized, false));
                lease.SetOutline(new Rect(10, 10, 880, 530), 22, 22, Matrix.Identity);
                lease.EnsureApplied();
            }
            WithRegion(window, region => Assert.IsTrue(PtInRegion(region, 899, 549)));
        }));
    }

    private static void WithWindow(Action<IntPtr> test)
    {
        IntPtr window = CreateWindowEx(0, "STATIC", "PCL hidden region regression", 0x80000000,
            0, 0, 3000, 1000, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.AreNotEqual(IntPtr.Zero, window);
        try { test(window); }
        finally { DestroyWindow(window); }
    }
    private static void WithRegion(IntPtr window, Action<IntPtr> test)
    {
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        try { GetWindowRgn(window, region); test(region); }
        finally { DeleteObject(region); }
    }
    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
}
