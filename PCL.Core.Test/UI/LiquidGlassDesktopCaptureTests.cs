using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
[DoNotParallelize]
public sealed class TahoeDesktopCaptureTests
{
    [STATestMethod]
    public void LiveMoveKeepsSessionBitmapAndNewFramesInsteadOfFreezingTheLastImage()
    {
        WithCapture((capture, platform, window, image) =>
        {
            platform.SupportsLiveMove = true;
            capture.Apply(true);
            var session = platform.Sessions.Single();
            session.Present(11);
            PumpUntil(() => capture.HasFrame);
            var bitmap = image.Source;
            capture.SetInteractiveMove(true);
            Assert.IsTrue(session.Moving);
            Assert.IsFalse(session.Disposed);
            Assert.AreEqual(TahoeCaptureState.Live, capture.State);
            for (byte value = 20; value < 25; value++)
            {
                int version = session.Region!.Version;
                window.Left += 2;
                PumpFor(TimeSpan.FromMilliseconds(8));
                Assert.AreEqual(version, session.Region.Version, "Position-only moves must not reject pending frame versions.");
                session.Present(value);
                int expected = value - 18;
                PumpUntil(() => session.Completed >= expected);
                Assert.AreSame(bitmap, image.Source, "Moving must not recreate a same-sized bitmap.");
                var pixels = new byte[4];
                ((WriteableBitmap)image.Source!).CopyPixels(new Int32Rect(0, 0, 1, 1), pixels, 4, 0);
                Assert.AreEqual(value, pixels[0], "Fresh frames must remain visible while moving.");
            }
            capture.SetInteractiveMove(false);
            Assert.IsFalse(session.Moving);
            Assert.AreEqual(1, platform.Creations);
        });
    }

    [STATestMethod]
    public void SamplerReceivesTheViewportClipRadiiInDevicePixels()
    {
        WithCapture((capture, platform, window, _) =>
        {
            var viewport = (Grid)window.Content;
            var clip = new RectangleGeometry(new Rect(viewport.RenderSize), 6, 6);
            viewport.Clip = clip;
            capture.Apply(true);
            var dpi = VisualTreeHelper.GetDpi(viewport);
            // FormMain changes its clip after starting capture when enabling glass.
            // No resize or move is required to update the helper's native shape.
            clip.RadiusX = 22;
            clip.RadiusY = 17;
            PumpUntil(() => Math.Abs(platform.Sessions.Single().Region!.RadiusX - 22 * dpi.DpiScaleX) < 0.001);
            var region = platform.Sessions.Single().Region!;
            Assert.AreEqual(22 * dpi.DpiScaleX, region.RadiusX, 0.001);
            Assert.AreEqual(17 * dpi.DpiScaleY, region.RadiusY, 0.001);
            capture.Apply(false);
            clip.RadiusX = 30;
            PumpFor(TimeSpan.FromMilliseconds(10));
            Assert.AreEqual(TahoeCaptureState.Stopped, capture.State);
            Assert.AreEqual(1, platform.Creations, "A retired clip observer must not restart capture.");
        });
    }

    [STATestMethod]
    public void SamplerReadsTheParentClipUsedByTheMainWindowAndDefaultsToRectangular()
    {
        WithCapture((capture, platform, window, _) =>
        {
            var viewport = (Grid)window.Content;
            window.Content = null;
            var parent = new Border { Child = viewport,
                Clip = new RectangleGeometry(new Rect(viewport.RenderSize), 22, 22) };
            window.Content = parent;
            window.UpdateLayout();
            PumpFor(TimeSpan.FromMilliseconds(10));
            capture.Apply(true);
            var dpi = VisualTreeHelper.GetDpi(viewport);
            Assert.AreEqual(22 * dpi.DpiScaleX, platform.Sessions.Single().Region!.RadiusX, 0.001);
            capture.Apply(false);
            parent.Clip = null;
            capture.Apply(true);
            Assert.AreEqual(0d, platform.Sessions.Last().Region!.RadiusX);
            Assert.AreEqual(0d, platform.Sessions.Last().Region!.RadiusY);
        });
    }

    [STATestMethod]
    public void RetiredErrorsAndFramesCannotAffectReplacementSession()
    {
        WithCapture((capture, platform, _, image) =>
        {
            capture.Apply(true);
            FakeSession retired = platform.Sessions.Single();
            retired.Fail();
            retired.Present();
            capture.Apply(false);
            capture.Apply(true);
            FakeSession replacement = platform.Sessions.Last();
            replacement.Present();
            PumpUntil(() => capture.HasFrame);
            Assert.AreEqual(TahoeCaptureState.Live, capture.State);
            Assert.IsFalse(replacement.Disposed);
            Assert.IsNotNull(image.Source);
            Assert.AreEqual(1, retired.Completed);
            Assert.AreEqual(1, replacement.Completed);
        });
    }

    [STATestMethod]
    public void DisplayChangeRebuildsSurvivingMonitorWithoutMovingWindow()
    {
        WithCapture((capture, platform, _, _) =>
        {
            platform.Monitors.Add(new TahoeCaptureMonitor(new IntPtr(2), platform.Monitors[0].Bounds));
            capture.Apply(true);
            Assert.AreEqual(2, platform.Sessions.Count);
            platform.Sessions[0].Fail();
            platform.Monitors.RemoveAt(0);
            platform.RaiseDisplayChange();
            PumpUntil(() => platform.Sessions.Count == 3);
            FakeSession survivor = platform.Sessions.Last();
            survivor.Present();
            PumpUntil(() => capture.State == TahoeCaptureState.Live);
            Assert.AreEqual(new IntPtr(2), survivor.Monitor);
            Assert.IsTrue(platform.Sessions.Take(2).All(session => session.Disposed));
        });
    }

    [STATestMethod]
    public void InitializationRetriesAreBoundedAndDisplayChangeCanRecover()
    {
        WithCapture((capture, platform, _, image) =>
        {
            platform.FailCreation = true;
            capture.Apply(true);
            PumpUntil(() => capture.State == TahoeCaptureState.Failed);
            Assert.AreEqual(4, platform.Creations, "One initial attempt and at most three retries.");
            Assert.IsFalse(capture.HasFrame);
            Assert.IsNull(image.Source);
            PumpFor(TimeSpan.FromMilliseconds(40));
            Assert.AreEqual(4, platform.Creations, "An exhausted service must not spin.");
            platform.FailCreation = false;
            platform.RaiseDisplayChange();
            PumpUntil(() => platform.Sessions.Count == 1);
            platform.Sessions[0].Present();
            PumpUntil(() => capture.HasFrame);
            Assert.AreEqual(TahoeCaptureState.Live, capture.State);
        });
    }

    [STATestMethod]
    public void CleanupFailureDoesNotSkipOtherSessionsAffinityOrImage()
    {
        WithCapture((capture, platform, _, image) =>
        {
            platform.Monitors.Add(new TahoeCaptureMonitor(new IntPtr(2), platform.Monitors[0].Bounds));
            capture.Apply(true);
            platform.Sessions[0].Present();
            PumpUntil(() => capture.HasFrame);
            platform.Sessions[0].ThrowOnDispose = true;
            capture.Apply(false);
            Assert.IsTrue(platform.Sessions.All(session => session.Disposed));
            Assert.AreEqual(0u, platform.Affinity);
            Assert.AreEqual(0, platform.DisplaySubscribers);
            Assert.IsNull(image.Source);
            Assert.IsFalse(capture.IsActive);
            Assert.AreEqual(TahoeCaptureState.Stopped, capture.State);
        });
    }

    [STATestMethod]
    public void HealthyMonitorFramesDoNotResetBrokenMonitorRetryBudget()
    {
        WithCapture((capture, platform, _, _) =>
        {
            platform.Monitors.Add(new TahoeCaptureMonitor(new IntPtr(2), platform.Monitors[0].Bounds));
            capture.Apply(true);
            FakeSession healthy = platform.Sessions[0];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                FakeSession broken = platform.Sessions.Last();
                healthy.Present();
                PumpUntil(() => healthy.Completed > attempt);
                broken.Fail();
                if (attempt < 3) PumpUntil(() => platform.Sessions.Last() != broken);
                else PumpUntil(() => capture.State == TahoeCaptureState.Failed);
            }
            Assert.AreEqual(5, platform.Creations, "Only the broken monitor should use the bounded recovery budget.");
            Assert.IsTrue(healthy.Disposed, "Exhausted recovery must retire even healthy stale-version sessions.");
            Assert.AreEqual(0u, platform.Affinity, "Failed live capture cannot leave the launcher invisible in screenshots.");
            Assert.IsFalse(capture.HasFrame);
        });
    }

    [STATestMethod]
    public void HideShowAndRepeatedTogglesDoNotLeaveSubscriptions()
    {
        WithCapture((capture, platform, window, _) =>
        {
            for (int iteration = 0; iteration < 3; iteration++)
            {
                capture.Apply(true);
                Assert.AreEqual(1, platform.DisplaySubscribers);
                platform.Sessions.Last().Present();
                PumpUntil(() => capture.HasFrame);
                window.Hide();
                PumpUntil(() => capture.State == TahoeCaptureState.Suspended);
                Assert.AreEqual(0u, platform.Affinity);
                window.Show();
                PumpUntil(() => !platform.Sessions.Last().Disposed);
                platform.Sessions.Last().Present();
                PumpUntil(() => capture.HasFrame);
                capture.Apply(false);
                Assert.AreEqual(0, platform.DisplaySubscribers);
            }
        });
    }

    [STATestMethod]
    public void InteractiveMoveRetiresLiveSamplingAndRejectsLateFramesUntilExit()
    {
        WithCapture((capture, platform, _, image) =>
        {
            capture.Apply(true);
            var previous = platform.Sessions.Last();
            previous.Present();
            PumpUntil(() => capture.HasFrame);
            capture.SetInteractiveMove(true);
            Assert.AreEqual(TahoeCaptureState.Suspended, capture.State);
            Assert.IsTrue(previous.Disposed);
            Assert.IsFalse(capture.HasFrame);
            Assert.IsNull(image.Source);
            int created = platform.Creations;
            capture.SetInteractiveMove(true);
            previous.Present();
            PumpFor(TimeSpan.FromMilliseconds(20));
            Assert.AreEqual(created, platform.Creations);
            Assert.IsFalse(capture.HasFrame);
            capture.SetInteractiveMove(false);
            PumpUntil(() => platform.Creations > created);
            platform.Sessions.Last().Present();
            PumpUntil(() => capture.State == TahoeCaptureState.Live);
            Assert.IsTrue(capture.HasFrame);
        });
    }

    [STATestMethod]
    public void DisablingGlassDuringDragCannotRestartCaptureOnMoveExit()
    {
        WithCapture((capture, platform, _, _) =>
        {
            capture.Apply(true);
            capture.SetInteractiveMove(true);
            capture.Apply(false);
            int created = platform.Creations;
            capture.SetInteractiveMove(false);
            PumpFor(TimeSpan.FromMilliseconds(30));
            Assert.AreEqual(created, platform.Creations);
            Assert.AreEqual(TahoeCaptureState.Stopped, capture.State);
            Assert.AreEqual(0u, platform.Affinity);
        });
    }

    [STATestMethod]
    public void UnsupportedCaptureDoesNotExcludeWindowOrCreateSession()
    {
        WithCapture((capture, platform, _, _) =>
        {
            platform.IsSupported = false;
            capture.Apply(true);
            Assert.AreEqual(TahoeCaptureState.Unsupported, capture.State);
            Assert.AreEqual(0, platform.Creations);
            Assert.AreEqual(0u, platform.Affinity);
            Assert.IsFalse(capture.HasFrame);
        });
    }

    [STATestMethod]
    public void DirtyRowsUploadToCorrectDestinationAndPreserveOtherPixels()
    {
        WithCapture((capture, platform, _, image) =>
        {
            capture.Apply(true);
            FakeSession session = platform.Sessions.Single();
            byte[] first = session.Present(fill: 17);
            PumpUntil(() => capture.HasFrame);
            int stride = session.Region!.Width * 4;
            byte[] next = (byte[])first.Clone();
            Array.Fill(next, (byte)93, stride * 2, stride);
            session.Present(next, 2, 1);
            PumpUntil(() => capture.PresentedFrames == 2);
            var bitmap = (BitmapSource)image.Source;
            byte[] actual = new byte[next.Length];
            bitmap.CopyPixels(actual, stride, 0);
            CollectionAssert.AreEqual(next, actual);
            Assert.AreEqual((long)first.Length + stride, capture.UploadedBytes);
        });
    }

    [TestMethod]
    public void DirtyRowsSkipUnchangedFramesAndForceFullGeometryUpdates()
    {
        byte[] previous = new byte[4 * 8 * 10];
        byte[] next = (byte[])previous.Clone();
        Assert.AreEqual((0, 0), TahoeCaptureResources.DirtyRows(next, previous, 8, 10, false));
        next[4 * 8 * 3 + 1] = 255;
        next[4 * 8 * 5 + 1] = 255;
        Assert.AreEqual((3, 3), TahoeCaptureResources.DirtyRows(next, previous, 8, 10, false));
        Assert.AreEqual((0, 10), TahoeCaptureResources.DirtyRows(next, previous, 8, 10, true));
    }

    [STATestMethod]
    public void ReducedSamplesChangeImageResolutionWithoutChangingViewportAndRestoreNativeQuality()
    {
        WithCapture((capture, platform, _, image) =>
        {
            capture.Apply(true);
            var session = platform.Sessions.Single();
            var region = session.Region!;
            var size = PCL.Core.UI.LiquidGlassPerformance.SamplingSize(region.Width, region.Height, 50);
            var pixels = session.PresentReduced(size.Width, size.Height, 31);
            PumpUntil(() => capture.PresentedFrames == 1);
            var bitmap = (BitmapSource)image.Source;
            Assert.AreEqual(size.Width, bitmap.PixelWidth);
            Assert.AreEqual(size.Height, bitmap.PixelHeight);
            Assert.AreEqual(region, session.Region, "Quality must not change physical capture coordinates.");
            Assert.AreEqual(pixels.Length, capture.UploadedBytes);
            session.PresentReduced(size.Width, size.Height, 63);
            PumpUntil(() => capture.PresentedFrames == 2);
            Assert.AreSame(bitmap, image.Source, "Same quality must reuse its image.");
            session.Present(81);
            PumpUntil(() => capture.PresentedFrames == 3);
            Assert.AreEqual(region.Width, ((BitmapSource)image.Source).PixelWidth);
            Assert.AreEqual(region.Height, ((BitmapSource)image.Source).PixelHeight);
            Assert.AreEqual(1, platform.Creations, "Quality changes must not restart capture.");
        });
    }

    [TestMethod]
    public void EveryNativeResourceIsReleasedEvenWhenMultipleCloseCallsFail()
    {
        var first = new FakeResource(true);
        var second = new FakeResource(false);
        var third = new FakeResource(true);
        var error = TahoeCaptureResources.ReleaseAll(first, null, second, third);
        Assert.IsTrue(first.Disposed && second.Disposed && third.Disposed);
        Assert.AreEqual(2, ((AggregateException)error!).InnerExceptions.Count);
    }

    [STATestMethod]
    public void DisablingCaptureRetriesTransientAffinityFailureAndRestoresScreenshots()
    {
        WithCapture((capture, platform, _, _) =>
        {
            capture.Apply(true);
            Assert.AreEqual(0x11u, platform.Affinity);
            platform.RestoreFailures = 1;
            capture.Apply(false);
            PumpUntil(() => platform.Affinity == 0);
            Assert.AreEqual(2, platform.RestoreCalls);
        });
    }

    [STATestMethod]
    public void DisposeRetainsBoundedRestoreWorkUntilWindowCaptureVisibilityIsRestored()
    {
        WithCapture((capture, platform, _, _) =>
        {
            capture.Apply(true);
            platform.RestoreFailures = 1;
            capture.Dispose();
            PumpUntil(() => platform.Affinity == 0);
            Assert.AreEqual(2, platform.RestoreCalls);
        });
    }

    [STATestMethod]
    public void PermanentAffinityFailureDoesNotSpinForever()
    {
        WithCapture((capture, platform, _, _) =>
        {
            capture.Apply(true);
            platform.RestoreFailures = int.MaxValue;
            capture.Dispose();
            PumpUntil(() => platform.RestoreCalls == 4);
            PumpFor(TimeSpan.FromMilliseconds(40));
            Assert.AreEqual(4, platform.RestoreCalls);
        });
    }

    [STATestMethod]
    public void ScreenshotFriendlyModeNeverStartsCaptureOrSetsExclusion()
    {
        WithCapture((capture, platform, _, _) =>
        {
            capture.Apply(false);
            Assert.AreEqual(0, platform.Creations);
            Assert.AreEqual(0u, platform.Affinity);
            Assert.AreEqual(TahoeCaptureState.Stopped, capture.State);
        });
    }

    private static void WithCapture(Action<TahoeDesktopCapture, FakePlatform, Window, Image> action)
    {
        var image = new Image();
        var viewport = new Grid();
        viewport.Children.Add(image);
        var window = new Window
        {
            Width = 160, Height = 100, Left = -10000, Top = -10000, Content = viewport,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
        };
        var platform = new FakePlatform();
        using var capture = new TahoeDesktopCapture(window, viewport, image, new RotateTransform(),
            new TranslateTransform(), platform, (_, _) => { }, TimeSpan.FromMilliseconds(2));
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpFor(TimeSpan.FromMilliseconds(10));
            // WPF may constrain a deliberately offscreen test window to a real
            // monitor. Derive the fake display from its actual screen position.
            Point origin = viewport.PointToScreen(new Point());
            platform.Monitors[0] = new TahoeCaptureMonitor(new IntPtr(1), new Rect(origin.X - 100, origin.Y - 100, 2000, 2000));
            action(capture, platform, window, image);
        }
        finally
        {
            capture.Apply(false);
            window.Close();
            PumpFor(TimeSpan.FromMilliseconds(10));
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(3)) PumpFor(TimeSpan.FromMilliseconds(4));
        Assert.IsTrue(condition(), "The dispatcher did not reach the expected capture state.");
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private sealed class FakePlatform : ITahoeCapturePlatform
    {
        public bool IsSupported { get; set; } = true;
        public bool SupportsLiveMove { get; set; }
        public bool FailCreation { get; set; }
        public int Creations { get; private set; }
        public uint Affinity { get; private set; }
        public int DisplaySubscribers => DisplaysChanged?.GetInvocationList().Length ?? 0;
        public event EventHandler? DisplaysChanged;
        public List<FakeSession> Sessions { get; } = [];
        public List<TahoeCaptureMonitor> Monitors { get; } = [new(new IntPtr(1), new Rect(-10100, -10100, 2000, 2000))];
        public bool TryExcludeWindow(IntPtr window, out uint originalAffinity)
        {
            originalAffinity = Affinity;
            Affinity = 0x11;
            return true;
        }
        public int RestoreFailures { get; set; }
        public int RestoreCalls { get; private set; }
        public bool RestoreWindow(IntPtr window, uint affinity)
        {
            RestoreCalls++;
            if (RestoreFailures-- > 0) return false;
            Affinity = affinity;
            return true;
        }
        public IReadOnlyList<TahoeCaptureMonitor> GetMonitors() => Monitors;
        public void RaiseDisplayChange() => DisplaysChanged?.Invoke(this, EventArgs.Empty);
        public ITahoeMonitorCapture CreateMonitor(IntPtr monitor, Action<TahoeCaptureFrame, Action> present, Action<Exception> failed)
        {
            Creations++;
            if (FailCreation) throw new InvalidOperationException("Injected initialization failure.");
            var session = new FakeSession(monitor, present, failed);
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeSession(IntPtr monitor, Action<TahoeCaptureFrame, Action> present, Action<Exception> failed) : ITahoeMonitorCapture
    {
        public bool Moving { get; private set; }
        public void SetInteractiveMove(bool moving) => Moving = moving;
        public IntPtr Monitor { get; } = monitor;
        public TahoeCaptureRegion? Region { get; private set; }
        public bool Disposed { get; private set; }
        public bool ThrowOnDispose { get; set; }
        public int Completed { get; private set; }
        public void SetRegion(TahoeCaptureRegion region) => Region = region;
        public void Fail() => failed(new InvalidOperationException("Injected retired-session failure."));
        public byte[] Present(byte fill = 33)
        {
            byte[] pixels = new byte[Region!.Width * Region.Height * 4];
            Array.Fill(pixels, fill);
            Present(pixels, 0, Region.Height);
            return pixels;
        }
        public void Present(byte[] pixels, int dirtyY, int dirtyHeight) =>
            present(new TahoeCaptureFrame(pixels, Region!, dirtyY, dirtyHeight), () => Completed++);
        public byte[] PresentReduced(int width, int height, byte fill)
        {
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, fill);
            present(new TahoeCaptureFrame(pixels, Region!, 0, height)
                { SampleWidth = width, SampleHeight = height }, () => Completed++);
            return pixels;
        }
        public void Dispose()
        {
            Disposed = true;
            if (ThrowOnDispose) throw new InvalidOperationException("Injected Close failure.");
        }
    }

    private sealed class FakeResource(bool throws) : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose()
        {
            Disposed = true;
            if (throws) throw new InvalidOperationException("Injected native Close failure.");
        }
    }
}
