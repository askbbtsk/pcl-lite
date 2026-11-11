using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PCL.Core.App;
using PCL.Core.UI;

namespace PCL;

/// <summary>DXGI Desktop Duplication; retains the existing WPF lifecycle compatibility entry.</summary>
internal sealed class TahoeCapturePlatform(Action<string, Exception?> log, Func<int>? frameRate = null) : ITahoeCapturePlatform
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
    public event EventHandler DisplaysChanged
    {
        add => SystemEvents.DisplaySettingsChanged += value;
        remove => SystemEvents.DisplaySettingsChanged -= value;
    }

    public bool TryExcludeWindow(IntPtr window, out uint originalAffinity) =>
        GetWindowDisplayAffinity(window, out originalAffinity) && SetWindowDisplayAffinity(window, 0x11);
    public bool RestoreWindow(IntPtr window, uint affinity) => SetWindowDisplayAffinity(window, affinity);

    public IReadOnlyList<TahoeCaptureMonitor> GetMonitors()
    {
        var monitors = new List<TahoeCaptureMonitor>();
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeRect rect, IntPtr data) =>
        {
            monitors.Add(new(monitor, new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            return true;
        }, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return monitors;
    }

    public ITahoeMonitorCapture CreateMonitor(IntPtr monitor, Action<TahoeCaptureFrame, Action> present, Action<Exception> failed) =>
        new MonitorCapture(monitor, present, failed, log,
            frameRate ?? (() => Config.Preference.TahoeGlass.MaximumFrameRate));

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    private sealed class MonitorCapture : ITahoeMonitorCapture
    {
        private readonly Action<TahoeCaptureFrame, Action> _present;
        private readonly Action<Exception> _failed;
        private readonly Action<string, Exception?> _log;
        private readonly Func<int> _frameRate;
        private LiquidGlassD3D11.Resource? _device, _context, _duplication, _desktop, _staging;
        private TahoeCaptureRegion? _region;
        private byte[]? _pixels, _scratch;
        private int _pending, _closing, _lastVersion = -1;
        private uint _rotation;
        private int _sourceWidth, _sourceHeight, _stagingWidth, _stagingHeight;
        private long _lastFrame;
        public Task Completion { get; }

        public MonitorCapture(IntPtr monitor, Action<TahoeCaptureFrame, Action> present, Action<Exception> failed,
            Action<string, Exception?> log, Func<int> frameRate)
        {
            _present = present;
            _failed = failed;
            _log = log;
            _frameRate = frameRate;
            try
            {
                Initialize(monitor);
                Completion = Task.Factory.StartNew(CaptureLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch { ReleaseNative(); throw; }
        }

        private void Initialize(IntPtr monitor)
        {
            using var factory = LiquidGlassDxgiDuplication.Factory();
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                using var adapter = LiquidGlassDxgiDuplication.Adapter(factory, adapterIndex);
                if (adapter is null) break;
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    using var output = LiquidGlassDxgiDuplication.Output(adapter, outputIndex);
                    if (output is null) break;
                    var info = LiquidGlassDxgiDuplication.DescribeOutput(output);
                    if (info.Monitor != monitor || info.Attached == 0) continue;
                    LiquidGlassD3D11.CreateDevice(out _device, out _context, adapter: adapter.Pointer);
                    _duplication = LiquidGlassDxgiDuplication.Duplicate(output, _device);
                    var desc = LiquidGlassDxgiDuplication.DescribeDuplication(_duplication);
                    if (desc.Format != 87) throw new NotSupportedException("Desktop duplication requires BGRA8 output.");
                    _rotation = desc.Rotation;
                    return;
                }
            }
            throw new InvalidOperationException("No DXGI output matches the requested monitor.");
        }

        public void SetRegion(TahoeCaptureRegion region) => Volatile.Write(ref _region, region);
        public void Dispose()
        {
            Interlocked.Exchange(ref _closing, 1);
        }

        private void CaptureLoop()
        {
            try
            {
                while (Volatile.Read(ref _closing) == 0)
                {
                    var region = Volatile.Read(ref _region);
                    if (region is null || Volatile.Read(ref _pending) != 0) { Thread.Sleep(8); continue; }
                    long now = Stopwatch.GetTimestamp();
                    if (!LiquidGlassPerformance.ShouldRender(now - _lastFrame, Stopwatch.Frequency,
                        LiquidGlassPerformance.NormalizeFrameRate(_frameRate())))
                    { Thread.Sleep(1); continue; }
                    bool acquired = LiquidGlassDxgiDuplication.Acquire(_duplication!, out var texture);
                    if (acquired)
                    {
                        try
                        {
                            using (texture)
                            {
                                var description = LiquidGlassD3D11.Describe(texture!);
                                int width = checked((int)description.Width), height = checked((int)description.Height);
                                if (_desktop is null || width != _sourceWidth || height != _sourceHeight)
                                {
                                    _desktop?.Dispose();
                                    _desktop = LiquidGlassD3D11.CreateStagingTexture(_device!, width, height, cpuReadable: false);
                                    _sourceWidth = width; _sourceHeight = height;
                                }
                                // Frame textures are invalid after ReleaseFrame; cache a GPU copy for stationary desktops.
                                LiquidGlassD3D11.CopyRegion(_context!, _desktop, texture!, 0, 0, width, height);
                            }
                        }
                        finally { LiquidGlassDxgiDuplication.ReleaseFrame(_duplication!); }
                    }
                    if (Volatile.Read(ref _closing) != 0 || _desktop is null || !acquired && region.Version == _lastVersion) continue;
                    _lastFrame = now;
                    PresentRegion(region);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (Volatile.Read(ref _closing) == 0) _failed(error);
            }
            finally { ReleaseNative(); }
        }

        private void PresentRegion(TahoeCaptureRegion region)
        {
            var crop = DesktopDuplicationPixels.SourceCrop(region.X, region.Y, region.Width, region.Height,
                _sourceWidth, _sourceHeight, _rotation);
            if (_staging is null || _stagingWidth != crop.Width || _stagingHeight != crop.Height)
            {
                _staging?.Dispose();
                _staging = LiquidGlassD3D11.CreateStagingTexture(_device!, crop.Width, crop.Height);
                _stagingWidth = crop.Width; _stagingHeight = crop.Height;
                _pixels = _scratch = null;
            }
            LiquidGlassD3D11.CopyRegion(_context!, _staging, _desktop!, crop.X, crop.Y, crop.Width, crop.Height);
            byte[] pixels = _scratch ?? new byte[checked(region.Width * region.Height * 4)];
            var mapped = LiquidGlassD3D11.MapRead(_context!, _staging);
            try { DesktopDuplicationPixels.Read(mapped.Data, checked((int)mapped.RowPitch), pixels, region.Width, region.Height, _rotation); }
            finally { LiquidGlassD3D11.Unmap(_context!, _staging); }
            var dirty = TahoeCaptureResources.DirtyRows(pixels, _pixels, region.Width, region.Height, region.Version != _lastVersion);
            _lastVersion = region.Version;
            if (dirty.Height == 0) { _scratch = pixels; return; }
            _scratch = _pixels;
            _pixels = pixels;
            Interlocked.Exchange(ref _pending, 1);
            if (Volatile.Read(ref _closing) == 0)
                _present(new TahoeCaptureFrame(pixels, region, dirty.Y, dirty.Height), () => Interlocked.Exchange(ref _pending, 0));
        }

        private void ReleaseNative()
        {
            var error = TahoeCaptureResources.ReleaseAll(_staging, _desktop, _duplication, _context, _device);
            _staging = _desktop = _duplication = _context = _device = null;
            _pixels = _scratch = null;
            if (error != null) _log("[LiquidGlass/DXGI] Releasing desktop capture resources failed", error);
        }
    }
}
