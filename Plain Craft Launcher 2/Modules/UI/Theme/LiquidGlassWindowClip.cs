using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PCL;

/// <summary>
/// WPF's visual Clip does not clip an opaque HWND's unused client margin. Keep the
/// native window inside that same visual outline, including startup transforms.
/// </summary>
internal sealed class LiquidGlassWindowClip : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _viewport;
    private readonly RectangleGeometry _clip;
    private readonly Action<Exception> _failed;
    private LiquidGlassWindowRegion? _region;
    private (Rect Bounds, double X, double Y, Matrix Transform)? _previous;
    private bool _enabled, _disposed, _faulted;

    public LiquidGlassWindowClip(Window window, FrameworkElement viewport, RectangleGeometry clip,
        Action<Exception> failed)
    {
        _window = window;
        _viewport = viewport;
        _clip = clip;
        _failed = failed;
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled) _faulted = false;
        if (_disposed || _enabled == enabled || (enabled && _faulted)) return;
        _enabled = enabled;
        if (enabled)
        {
            CompositionTarget.Rendering += OnRendering;
            Update();
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            try { _region?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { _failed(error); }
            finally { _region = null; _previous = null; }
        }
    }

    private void OnRendering(object? sender, EventArgs args) => Update();

    private void Update()
    {
        if (!_enabled || !_window.IsVisible || _viewport.ActualWidth <= 0 || _viewport.ActualHeight <= 0) return;
        try
        {
            var handle = new WindowInteropHelper(_window).Handle;
            var source = HwndSource.FromHwnd(handle);
            if (source?.RootVisual is not Visual root || source.CompositionTarget is null) return;
            var visualTransform = _viewport.TransformToAncestor(root);
            Point origin = visualTransform.Transform(new Point());
            Point x = visualTransform.Transform(new Point(1, 0));
            Point y = visualTransform.Transform(new Point(0, 1));
            var matrix = new Matrix(x.X - origin.X, x.Y - origin.Y, y.X - origin.X, y.Y - origin.Y,
                origin.X, origin.Y);
            matrix.Append(source.CompositionTarget.TransformToDevice);
            var clientOrigin = new NativePoint();
            if (!ClientToScreen(handle, ref clientOrigin) || !GetWindowRect(handle, out var windowBounds))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            matrix.Translate(clientOrigin.X - windowBounds.Left, clientOrigin.Y - windowBounds.Top);
            var bounds = new Rect(0, 0, _viewport.ActualWidth, _viewport.ActualHeight);
            var next = (bounds, _clip.RadiusX, _clip.RadiusY, matrix);
            _region ??= new LiquidGlassWindowRegion(handle);
            // WindowChrome can replace the HRGN after a move/resize. Compare the
            // native region even when the visual geometry has not changed.
            if (_previous != next)
            {
                _region.SetOutline(bounds, _clip.RadiusX, _clip.RadiusY, matrix);
                _previous = next;
            }
            else _region.EnsureApplied();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            SetEnabled(false);
            _faulted = true;
            _failed(error);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetEnabled(false);
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
}

/// <summary>Owns the original and desired regions; Windows owns only applied copies.</summary>
internal sealed class LiquidGlassWindowRegion : IDisposable
{
    private readonly IntPtr _window;
    private IntPtr _original, _desired, _probe;
    private bool _hadOriginal;
    private bool _disposed;

    public LiquidGlassWindowRegion(IntPtr window)
    {
        _window = window;
        _original = NewRegion();
        try { _probe = NewRegion(); }
        catch { DeleteObject(_original); throw; }
        _hadOriginal = GetWindowRgn(window, _original) != 0;
    }

    public void SetOutline(Rect bounds, double radiusX, double radiusY, Matrix transform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var outline = new RectangleGeometry(bounds, radiusX, radiusY) { Transform = new MatrixTransform(transform) };
        var flattened = outline.GetFlattenedPathGeometry(.1, ToleranceType.Absolute);
        var points = new List<NativePoint>();
        foreach (PathFigure figure in flattened.Figures)
        {
            points.Add(Convert(figure.StartPoint));
            foreach (PathSegment segment in figure.Segments)
                if (segment is PolyLineSegment lines)
                    foreach (Point point in lines.Points) points.Add(Convert(point));
                else if (segment is LineSegment line) points.Add(Convert(line.Point));
        }
        IntPtr region = CreatePolygonRgn(points.ToArray(), points.Count, 2);
        if (region == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { CaptureExternalRegion(); }
        catch { DeleteObject(region); throw; }
        if (_desired != IntPtr.Zero) DeleteObject(_desired);
        _desired = region;
        ApplyCopy(_desired);
    }

    public void EnsureApplied()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_desired == IntPtr.Zero) return;
        if (!CaptureExternalRegion()) return;
        ApplyCopy(_desired);
    }

    private bool CaptureExternalRegion()
    {
        if (_desired == IntPtr.Zero) return true;
        int kind = GetWindowRgn(_window, _probe);
        if (kind != 0 && EqualRgn(_desired, _probe)) return false;
        // WindowChrome may have recalculated its normal region after resizing
        // or changing DPI. Restore that newest region, not a stale startup size.
        _hadOriginal = kind != 0;
        if (_hadOriginal && CombineRgn(_original, _probe, IntPtr.Zero, 5) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return true;
    }
    private void ApplyCopy(IntPtr region)
    {
        IntPtr copy = NewRegion();
        if (CombineRgn(copy, region, IntPtr.Zero, 5) != 0 && SetWindowRgn(_window, copy, true) != 0) return;
        int error = Marshal.GetLastWin32Error();
        DeleteObject(copy);
        throw new Win32Exception(error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (IsWindow(_window))
            {
                if (_hadOriginal) ApplyCopy(_original);
                else if (SetWindowRgn(_window, IntPtr.Zero, true) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            _disposed = true;
            DeleteObject(_original);
            DeleteObject(_desired);
            DeleteObject(_probe);
            _original = _desired = _probe = IntPtr.Zero;
        }
    }

    private static NativePoint Convert(Point point) => new()
    { X = checked((int)Math.Round(point.X)), Y = checked((int)Math.Round(point.Y)) };
    private static IntPtr NewRegion()
    {
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        return region != IntPtr.Zero ? region : throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreatePolygonRgn(NativePoint[] points, int count, int fillMode);
    [DllImport("gdi32.dll")] private static extern bool EqualRgn(IntPtr first, IntPtr second);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
}
