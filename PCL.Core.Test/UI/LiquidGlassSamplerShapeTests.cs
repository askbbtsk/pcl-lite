using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
[DoNotParallelize]
public sealed class LiquidGlassSamplerShapeTests
{
    [STATestMethod]
    public void NativeSamplerHidesAllFourCornersAtDifferentScalesAndAfterResize()
    {
        IntPtr host = CreateWindowEx(0, "STATIC", "Sampler shape test", 0x80000000,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.AreNotEqual(IntPtr.Zero, host);
        try
        {
            var capture = typeof(LiquidGlassScreenshotCapturePlatform).GetNestedType("Capture", BindingFlags.NonPublic)!;
            var apply = capture.GetMethod("SetHelperShape", BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
            foreach (int size in new[] { 160, 400, 900 })
            {
                int width = (int)(size * scale), height = (int)(100 * scale);
                var data = new TahoeCaptureRegion(0, 0, width, height, 0, 0, 1)
                { RadiusX = 22 * scale, RadiusY = 22 * scale };
                apply.Invoke(null, new object[] { host, data });
                IntPtr region = CreateRectRgn(0, 0, 0, 0);
                try
                {
                    Assert.AreEqual(3, GetWindowRgn(host, region), "Expected native rounded region.");
                    Assert.IsFalse(PtInRegion(region, 1, 1));
                    Assert.IsFalse(PtInRegion(region, width - 2, 1));
                    Assert.IsFalse(PtInRegion(region, 1, height - 2));
                    Assert.IsFalse(PtInRegion(region, width - 2, height - 2));
                    Assert.IsTrue(PtInRegion(region, width / 2, height / 2));
                    Assert.IsFalse(PtInRegion(region, 0, height / 2), "Inset keeps the helper inside the antialiased WPF edge.");
                }
                finally { DeleteObject(region); }
            }
            // A rectangular caller must not inherit the previous rounded region.
            apply.Invoke(null, new object[] { host, new TahoeCaptureRegion(0, 0, 200, 100, 0, 0, 2) });
            IntPtr rectangular = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert.AreEqual(2, GetWindowRgn(host, rectangular));
                Assert.IsTrue(PtInRegion(rectangular, 0, 0));
                Assert.IsTrue(PtInRegion(rectangular, 198, 98));
            }
            finally { DeleteObject(rectangular); }
        }
        finally { DestroyWindow(host); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint styleEx, string name, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
}
