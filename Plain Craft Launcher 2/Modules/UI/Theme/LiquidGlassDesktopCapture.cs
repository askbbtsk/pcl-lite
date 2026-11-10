using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PCL;

/// <summary>
/// Coordinates desktop capture on the UI thread. Native sessions publish immutable
/// regions and retire asynchronously; no UI operation waits for a GPU readback.
/// </summary>
internal sealed class TahoeDesktopCapture : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _viewport;
    private readonly Image _target;
    private readonly RotateTransform _rotation;
    private readonly TranslateTransform _translation;
    private readonly ITahoeCapturePlatform _platform;
    private readonly Action<string, Exception?> _log;
    private readonly Dictionary<IntPtr, Session> _sessions = [];
    private readonly HashSet<IntPtr> _expectedMonitors = [];
    private readonly DispatcherTimer _retry;
    private readonly DispatcherTimer _affinityRetry;
    private int _affinityAttempts;
    private IntPtr _handle;
    private uint _originalAffinity;
    private WriteableBitmap? _bitmap;
    private Rect _bounds = Rect.Empty;
    private int _version, _attempts;
    private bool _enabled, _excluded, _disposed, _topologyDirty = true;
    private DispatcherOperation? _geometryUpdate;

    public bool IsActive => HasFrame && _sessions.Count != 0;
    public bool HasFrame { get; private set; }
    public TahoeCaptureState State { get; private set; } = TahoeCaptureState.Stopped;
    public string Status => State == TahoeCaptureState.Live ? "LiveDesktopRefraction" : State.ToString();
    public long PresentedFrames { get; private set; }
    public long UploadedBytes { get; private set; }
    public event EventHandler? StateChanged;

    public TahoeDesktopCapture(Window window, FrameworkElement viewport, Image target,
        RotateTransform rotation, TranslateTransform translation, ITahoeCapturePlatform platform,
        Action<string, Exception?> log, TimeSpan? retryDelay = null)
    {
        _window = window;
        _viewport = viewport;
        _target = target;
        _rotation = rotation;
        _translation = translation;
        _platform = platform;
        _log = log;
        _retry = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = retryDelay ?? TimeSpan.FromMilliseconds(250)
        };
        _retry.Tick += Retry;
        _affinityRetry = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        { Interval = retryDelay ?? TimeSpan.FromMilliseconds(250) };
        _affinityRetry.Tick += RetryAffinity;
        window.Closed += WindowClosed;
    }

    public void Apply(bool enabled)
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed) return;
        if (!enabled || SystemParameters.HighContrast) { Stop(); return; }
        if (_enabled) return;
        _enabled = true;
        _attempts = 0;
        _window.LocationChanged += GeometryChanged;
        _window.SizeChanged += GeometryChanged;
        _viewport.SizeChanged += GeometryChanged;
        _window.StateChanged += GeometryChanged;
        _window.DpiChanged += GeometryChanged;
        _window.SourceInitialized += GeometryChanged;
        _rotation.Changed += GeometryChanged;
        _translation.Changed += GeometryChanged;
        _window.IsVisibleChanged += VisibilityChanged;
        _platform.DisplaysChanged += DisplaysChanged;
        SetState(TahoeCaptureState.Starting);
        UpdateGeometry();
    }

    private void GeometryChanged(object? sender, EventArgs e) => QueueGeometry();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueGeometry();

    private void QueueGeometry()
    {
        if (!_enabled || _disposed || _window.Dispatcher.HasShutdownStarted) return;
        if (_geometryUpdate?.Status == DispatcherOperationStatus.Pending) return;
        _geometryUpdate = _window.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _geometryUpdate = null;
            UpdateGeometry();
        });
    }

    private void DisplaysChanged(object? sender, EventArgs args)
    {
        if (_window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (!_enabled || _disposed) return;
            _topologyDirty = true;
            _attempts = 0;
            RetireAll();
            ClearImage();
            RestoreAffinity();
            ScheduleRecovery();
        });
    }

    private void Retry(object? sender, EventArgs args)
    {
        _retry.Stop();
        if (!_enabled || _disposed) return;
        _topologyDirty = true;
        UpdateGeometry();
    }

    private void UpdateGeometry()
    {
        if (!_enabled || _disposed) return;
        if (!_window.IsVisible || _window.WindowState == WindowState.Minimized)
        {
            _retry.Stop();
            RetireAll();
            _bounds = Rect.Empty;
            ClearImage();
            RestoreAffinity();
            SetState(TahoeCaptureState.Suspended);
            return;
        }
        if (Math.Abs(_rotation.Angle) > .01 || Math.Abs(_translation.Y) > .1 ||
            !_viewport.IsLoaded || _viewport.ActualWidth < 1 || _viewport.ActualHeight < 1) return;
        try
        {
            if (!_platform.IsSupported) { SetState(TahoeCaptureState.Unsupported); return; }
            _handle = new WindowInteropHelper(_window).Handle;
            if (_handle == IntPtr.Zero) return;
            Point origin = _viewport.PointToScreen(new Point());
            Point end = _viewport.PointToScreen(new Point(_viewport.ActualWidth, _viewport.ActualHeight));
            var bounds = new Rect(Math.Floor(origin.X), Math.Floor(origin.Y),
                Math.Max(1, Math.Ceiling(end.X) - Math.Floor(origin.X)), Math.Max(1, Math.Ceiling(end.Y) - Math.Floor(origin.Y)));
            if (bounds == _bounds && !_topologyDirty && _sessions.Count != 0) return;
            if (_retry.IsEnabled) return;
            if (State == TahoeCaptureState.Failed && bounds == _bounds && !_topologyDirty) return;
            if (bounds != _bounds) _attempts = 0;
            _bounds = bounds;
            _topologyDirty = false;
            int version = ++_version;
            int width = checked((int)bounds.Width), height = checked((int)bounds.Height);
            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                _bitmap = NewBitmap(width, height);

            var monitors = _platform.GetMonitors();
            var needed = new HashSet<IntPtr>();
            _expectedMonitors.Clear();
            foreach (TahoeCaptureMonitor monitor in monitors)
            {
                Rect intersection = Rect.Intersect(monitor.Bounds, bounds);
                if (!intersection.IsEmpty && intersection.Width >= 1 && intersection.Height >= 1)
                    _expectedMonitors.Add(monitor.Handle);
            }
            foreach (TahoeCaptureMonitor monitor in monitors)
            {
                Rect area = Rect.Intersect(bounds, monitor.Bounds);
                if (area.IsEmpty || area.Width < 1 || area.Height < 1) continue;
                needed.Add(monitor.Handle);
                if (!_excluded)
                {
                    if (!_platform.TryExcludeWindow(_handle, out _originalAffinity))
                        throw new InvalidOperationException("The launcher cannot be excluded from recursive capture.");
                    _excluded = true;
                }
                if (!_sessions.TryGetValue(monitor.Handle, out Session? session))
                {
                    session = new Session(monitor.Handle);
                    _sessions.Add(monitor.Handle, session);
                    try
                    {
                        session.Capture = _platform.CreateMonitor(monitor.Handle,
                            (frame, completed) => Present(session, frame, completed), error => Failed(session, error));
                    }
                    catch
                    {
                        _sessions.Remove(monitor.Handle);
                        throw;
                    }
                }
                session.Capture!.SetRegion(new TahoeCaptureRegion((int)(area.X - monitor.Bounds.X), (int)(area.Y - monitor.Bounds.Y),
                    (int)area.Width, (int)area.Height, (int)(area.X - bounds.X), (int)(area.Y - bounds.Y), version));
            }
            foreach (IntPtr monitor in _sessions.Keys.Where(key => !needed.Contains(key)).ToArray())
            {
                Session retired = _sessions[monitor];
                _sessions.Remove(monitor);
                Release(retired.Capture);
            }
            if (_sessions.Count == 0)
            {
                ClearImage();
                RestoreAffinity();
                SetState(TahoeCaptureState.Suspended);
            }
            else
            {
                _affinityRetry.Stop(); // Capture intentionally excludes this live session.
                if (!HasFrame) SetState(TahoeCaptureState.Starting);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RetireAll();
            ClearImage();
            RestoreAffinity();
            _log("[Tahoe] Desktop capture initialization failed", error);
            ScheduleRecovery();
        }
    }

    private static WriteableBitmap NewBitmap(int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        // Gaps between monitors must not expose black or previous desktop pixels.
        var pixels = new int[checked(width * height)];
        Array.Fill(pixels, 0x00808080);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, checked(width * 4), 0);
        return bitmap;
    }

    private bool IsCurrent(Session session) => _enabled && !_disposed &&
        _sessions.TryGetValue(session.Monitor, out Session? current) && ReferenceEquals(session, current);

    private void Present(Session session, TahoeCaptureFrame frame, Action completed)
    {
        if (_window.Dispatcher.HasShutdownStarted) { completed(); return; }
        var operation = _window.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                TahoeCaptureRegion region = frame.Region;
                if (!IsCurrent(session) || region.Version != _version || _bitmap == null) return;
                int stride = checked(region.Width * 4);
                _bitmap.WritePixels(new Int32Rect(region.DestinationX, region.DestinationY + frame.DirtyY, region.Width, frame.DirtyHeight),
                    frame.Pixels, stride, checked(frame.DirtyY * stride));
                _target.Source = _bitmap;
                UploadedBytes += (long)stride * frame.DirtyHeight;
                PresentedFrames++;
                session.HasFrame = true;
                bool recovered = _expectedMonitors.SetEquals(_sessions.Keys) && _sessions.Values.All(value => value.HasFrame);
                if (recovered && !_retry.IsEnabled) _attempts = 0;
                bool firstFrame = !HasFrame;
                if (firstFrame)
                {
                    HasFrame = true;
                    _log("[Tahoe] DesktopCapture=Live; Shader=EighthPower; f=0.05", null);
                }
                if (recovered && !_retry.IsEnabled || State is TahoeCaptureState.Starting or TahoeCaptureState.Live)
                    SetState(TahoeCaptureState.Live, firstFrame);
                else if (firstFrame) StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Failed(session, error); }
            finally { completed(); }
        });
        operation.Aborted += (_, _) => completed();
    }

    private void Failed(Session session, Exception error)
    {
        if (_window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            // Session identity prevents late errors from stopping its replacement.
            if (!IsCurrent(session)) return;
            _sessions.Remove(session.Monitor);
            Release(session.Capture);
            ++_version;
            ClearImage();
            if (_sessions.Count == 0) RestoreAffinity();
            _log("[Tahoe] Desktop capture session failed; scheduling recovery", error);
            ScheduleRecovery();
        });
    }

    private void ScheduleRecovery()
    {
        if (!_enabled || _disposed || _retry.IsEnabled) return;
        if (++_attempts > 3)
        {
            RetireAll();
            ClearImage();
            RestoreAffinity();
            _topologyDirty = false;
            SetState(TahoeCaptureState.Failed);
            return;
        }
        SetState(TahoeCaptureState.Recovering);
        _retry.Start();
    }

    private void RetireAll()
    {
        ++_version;
        var retired = _sessions.Values.ToArray();
        _sessions.Clear();
        foreach (Session session in retired) Release(session.Capture);
        _topologyDirty = true;
    }

    private void Release(IDisposable? resource)
    {
        Exception? error = TahoeCaptureResources.ReleaseAll(resource);
        if (error != null) _log("[Tahoe] Desktop capture cleanup failed", error);
    }

    private void RestoreAffinity()
    {
        if (!_excluded) return;
        try
        {
            if (_platform.RestoreWindow(_handle, _originalAffinity))
            {
                _excluded = false;
                _affinityAttempts = 0;
                _affinityRetry.Stop();
                if (_disposed) DetachAffinityRetry();
                return;
            }
            else _log("[Tahoe] Restoring capture visibility failed", null);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { _log("[Tahoe] Restoring capture visibility failed", error); }
        if (_affinityAttempts < 3 && !_window.Dispatcher.HasShutdownStarted) _affinityRetry.Start();
        else if (_disposed) DetachAffinityRetry();
    }

    private void RetryAffinity(object? sender, EventArgs args)
    {
        _affinityRetry.Stop();
        if (_enabled && _sessions.Count > 0) return;
        ++_affinityAttempts;
        RestoreAffinity();
    }

    private void WindowClosed(object? sender, EventArgs args)
    {
        // A destroyed HWND cannot remain excluded and must not keep a dispatcher retry alive.
        _excluded = false;
        DetachAffinityRetry();
    }

    private void DetachAffinityRetry()
    {
        _affinityRetry.Stop();
        _affinityRetry.Tick -= RetryAffinity;
        _window.Closed -= WindowClosed;
    }

    private void ClearImage()
    {
        _target.Source = null;
        _bitmap = null;
        HasFrame = false;
    }

    private void SetState(TahoeCaptureState state, bool forceNotification = false)
    {
        if (State == state && !forceNotification) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Stop()
    {
        _retry.Stop();
        _geometryUpdate?.Abort();
        _geometryUpdate = null;
        if (_enabled)
        {
            _enabled = false;
            _window.LocationChanged -= GeometryChanged;
            _window.SizeChanged -= GeometryChanged;
            _viewport.SizeChanged -= GeometryChanged;
            _window.StateChanged -= GeometryChanged;
            _window.DpiChanged -= GeometryChanged;
            _window.SourceInitialized -= GeometryChanged;
            _rotation.Changed -= GeometryChanged;
            _translation.Changed -= GeometryChanged;
            _window.IsVisibleChanged -= VisibilityChanged;
            _platform.DisplaysChanged -= DisplaysChanged;
        }
        RetireAll();
        _bounds = Rect.Empty;
        RestoreAffinity();
        ClearImage();
        PresentedFrames = UploadedBytes = 0;
        SetState(TahoeCaptureState.Stopped);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _retry.Tick -= Retry;
        if (!_excluded) DetachAffinityRetry();
    }

    private sealed class Session(IntPtr monitor)
    {
        public IntPtr Monitor { get; } = monitor;
        public ITahoeMonitorCapture? Capture { get; set; }
        public bool HasFrame { get; set; }
    }
}
