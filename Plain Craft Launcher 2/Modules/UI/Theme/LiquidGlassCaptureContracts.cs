using System;
using System.Collections.Generic;
using System.Windows;

namespace PCL;

internal enum TahoeCaptureState { Stopped, Starting, Live, Recovering, Suspended, Unsupported, Failed }

internal sealed record TahoeCaptureMonitor(IntPtr Handle, Rect Bounds);
internal sealed record TahoeCaptureRegion(int X, int Y, int Width, int Height,
    int DestinationX, int DestinationY, int Version);
internal sealed record TahoeCaptureFrame(byte[] Pixels, TahoeCaptureRegion Region, int DirtyY, int DirtyHeight);

internal interface ITahoeMonitorCapture : IDisposable
{
    // UI retirement stays nonblocking. Diagnostics/tests can await actual native ownership release.
    System.Threading.Tasks.Task Completion => System.Threading.Tasks.Task.CompletedTask;
    // Called on the UI thread. Publishing a region must never wait for GPU work.
    void SetRegion(TahoeCaptureRegion region);
}

internal interface ITahoeCapturePlatform
{
    bool IsSupported { get; }
    bool TryExcludeWindow(IntPtr window, out uint originalAffinity);
    bool RestoreWindow(IntPtr window, uint affinity);
    IReadOnlyList<TahoeCaptureMonitor> GetMonitors();
    ITahoeMonitorCapture CreateMonitor(IntPtr monitor, Action<TahoeCaptureFrame, Action> present, Action<Exception> failed);
    event EventHandler DisplaysChanged;
}

internal static class TahoeCaptureResources
{
    /// <summary>Attempt every release, even when a native Close operation fails.</summary>
    public static Exception? ReleaseAll(params IDisposable?[] resources)
    {
        List<Exception>? errors = null;
        foreach (IDisposable? resource in resources)
        {
            try { resource?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                (errors ??= []).Add(error);
            }
        }
        return errors == null ? null : new AggregateException(errors);
    }

    /// <summary>Find a dirty row band without allocating another image buffer.</summary>
    public static (int Y, int Height) DirtyRows(byte[] pixels, byte[]? previous, int width, int height, bool force)
    {
        int stride = checked(width * 4);
        if (force || previous == null || previous.Length != pixels.Length) return (0, height);
        if (pixels.AsSpan().SequenceEqual(previous)) return (0, 0);
        int first = 0, last = height - 1;
        while (first < last && pixels.AsSpan(first * stride, stride).SequenceEqual(previous.AsSpan(first * stride, stride))) first++;
        while (last > first && pixels.AsSpan(last * stride, stride).SequenceEqual(previous.AsSpan(last * stride, stride))) last--;
        return (first, last - first + 1);
    }
}
