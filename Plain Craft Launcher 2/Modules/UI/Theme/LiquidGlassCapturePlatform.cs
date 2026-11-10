using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using PCL.Core.App;
using PCL.Core.UI;
using Rect = System.Windows.Rect;

namespace PCL;

/// <summary>Windows Graphics Capture and D3D11, isolated from the WPF lifecycle.</summary>
internal sealed class TahoeCapturePlatform(Action<string, Exception?> log) : ITahoeCapturePlatform
{
    public bool IsSupported => GraphicsCaptureSession.IsSupported();
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
            monitors.Add(new TahoeCaptureMonitor(monitor, new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            return true;
        }, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return monitors;
    }

    public ITahoeMonitorCapture CreateMonitor(IntPtr monitor, Action<TahoeCaptureFrame, Action> present, Action<Exception> failed) =>
        new MonitorCapture(monitor, present, failed, log);

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
        private readonly object _gate = new();
        private readonly Action<TahoeCaptureFrame, Action> _present;
        private readonly Action<Exception> _failed;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDirect3DDevice? _winrtDevice;
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _session;
        private ID3D11Texture2D? _staging;
        private TahoeCaptureRegion? _region;
        private byte[]? _pixels, _scratch;
        private Windows.Graphics.SizeInt32 _frameSize;
        private int _pending, _lastVersion;
        private long _lastFrame;
        private int _closing;
        private readonly Action<string, Exception?> _log;

        public MonitorCapture(IntPtr monitor, Action<TahoeCaptureFrame, Action> present, Action<Exception> failed, Action<string, Exception?> log)
        {
            _present = present;
            _failed = failed;
            _log = log;
            try
            {
                D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1], out _device, out _context).CheckError();
                using var dxgi = _device.QueryInterface<IDXGIDevice>();
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr nativeDevice));
                try { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(nativeDevice); }
                finally { Marshal.Release(nativeDevice); }
                _item = CreateItem(monitor);
                _frameSize = _item.Size;
                _item.Closed += ItemClosed;
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
                _session = _pool.CreateCaptureSession(_item);
                _session.IsCursorCaptureEnabled = false;
                _pool.FrameArrived += FrameArrived;
                _session.StartCapture();
            }
            catch { Dispose(); throw; }
        }

        public void SetRegion(TahoeCaptureRegion region) => System.Threading.Volatile.Write(ref _region, region);
        private void ItemClosed(GraphicsCaptureItem sender, object args) => ReportFailure(new InvalidOperationException("Capture display was disconnected."));

        private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            lock (_gate)
            {
                if (System.Threading.Volatile.Read(ref _closing) != 0) return;
                try
                {
                    using var frame = sender.TryGetNextFrame();
                    if (frame == null || System.Threading.Volatile.Read(ref _region) is not { } region || System.Threading.Volatile.Read(ref _pending) != 0) return;
                    if (frame.ContentSize.Width != _frameSize.Width || frame.ContentSize.Height != _frameSize.Height)
                    {
                        _frameSize = frame.ContentSize;
                        frame.Dispose();
                        sender.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _frameSize);
                        return;
                    }
                    long now = Stopwatch.GetTimestamp();
                    // Apply the user's cap even during dragging/resizing; geometry changes do not bypass it.
                    if (!LiquidGlassPerformance.ShouldRender(now - _lastFrame, Stopwatch.Frequency,
                            LiquidGlassPerformance.NormalizeFrameRate(Config.Preference.TahoeGlass.MaximumFrameRate))) return;
                    _lastFrame = now;
                    using ID3D11Texture2D texture = SurfaceTexture(frame.Surface);
                    var desc = texture.Description;
                    if (region.X < 0 || region.Y < 0 || region.X + region.Width > desc.Width || region.Y + region.Height > desc.Height) return;
                    if (_staging == null || _staging.Description.Width != region.Width || _staging.Description.Height != region.Height)
                    {
                        _staging?.Dispose();
                        _staging = _device!.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)region.Width, Height = (uint)region.Height, MipLevels = 1, ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
                        });
                        _pixels = null;
                        _scratch = null;
                    }
                    _context!.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0,
                        new Box(region.X, region.Y, 0, region.X + region.Width, region.Y + region.Height, 1));
                    _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
                    int stride = region.Width * 4;
                    byte[] pixels = _scratch ?? new byte[stride * region.Height];
                    try
                    {
                        if (mapped.RowPitch == stride)
                            Marshal.Copy(mapped.DataPointer, pixels, 0, pixels.Length);
                        else
                            for (int y = 0; y < region.Height; y++)
                                Marshal.Copy(mapped.DataPointer + checked((int)mapped.RowPitch * y), pixels, y * stride, stride);
                    }
                    finally { _context.Unmap(_staging, 0); }
                    var dirty = TahoeCaptureResources.DirtyRows(pixels, _pixels, region.Width, region.Height, region.Version != _lastVersion);
                    if (dirty.Height == 0)
                    {
                        _scratch = pixels;
                        return;
                    }
                    _scratch = _pixels;
                    _pixels = pixels;
                    _lastVersion = region.Version;
                    System.Threading.Interlocked.Exchange(ref _pending, 1);
                    if (System.Threading.Volatile.Read(ref _closing) == 0)
                        _present(new TahoeCaptureFrame(pixels, region, dirty.Y, dirty.Height),
                            () => System.Threading.Interlocked.Exchange(ref _pending, 0));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { ReportFailure(ex); }
            }
        }

        private void ReportFailure(Exception error)
        {
            if (System.Threading.Volatile.Read(ref _closing) == 0) _failed(error);
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _closing, 1) != 0) return;
            // UI callers only retire this session. A worker drains the GPU callback
            // before detaching native objects, so moving/hiding a window never waits.
            System.Threading.ThreadPool.QueueUserWorkItem(_ => ReleaseNative());
        }

        private void ReleaseNative()
        {
            IDisposable?[] resources;
            lock (_gate)
            {
                // Keep Close outside the gate: WGC may wait for its event callback.
                try { if (_pool != null) _pool.FrameArrived -= FrameArrived; }
                catch (Exception error) when (error is not OutOfMemoryException) { _log("[Tahoe] Detaching frame observer failed", error); }
                try { if (_item != null) _item.Closed -= ItemClosed; }
                catch (Exception error) when (error is not OutOfMemoryException) { _log("[Tahoe] Detaching display observer failed", error); }
                resources = [_session, _pool, _staging, _context, _device, _winrtDevice];
                _session = null; _pool = null; _staging = null; _item = null;
                _context = null; _device = null; _winrtDevice = null; _pixels = _scratch = null;
            }
            Exception? releaseError = TahoeCaptureResources.ReleaseAll(resources);
            if (releaseError != null) _log("[Tahoe] Releasing desktop capture resources failed", releaseError);
        }

        private static GraphicsCaptureItem CreateItem(IntPtr monitor)
        {
            const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out IntPtr text));
            IntPtr factory = IntPtr.Zero, item = IntPtr.Zero;
            try
            {
                Guid factoryId = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref factoryId, out factory));
                var create = Marshal.GetDelegateForFunctionPointer<CreateMonitor>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 4 * IntPtr.Size));
                Guid itemId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
                Marshal.ThrowExceptionForHR(create(factory, monitor, ref itemId, out item));
                return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
            }
            finally
            {
                if (item != IntPtr.Zero) Marshal.Release(item);
                if (factory != IntPtr.Zero) Marshal.Release(factory);
                WindowsDeleteString(text);
            }
        }

        private static ID3D11Texture2D SurfaceTexture(IDirect3DSurface surface)
        {
            IntPtr inspectable = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            IntPtr access = IntPtr.Zero;
            try
            {
                Guid accessId = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in accessId, out access));
                var get = Marshal.GetDelegateForFunctionPointer<GetInterface>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(access), 3 * IntPtr.Size));
                Guid textureId = typeof(ID3D11Texture2D).GUID;
                Marshal.ThrowExceptionForHR(get(access, ref textureId, out IntPtr texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                if (access != IntPtr.Zero) Marshal.Release(access);
                Marshal.Release(inspectable);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateMonitor(IntPtr self, IntPtr monitor, ref Guid iid, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(IntPtr self, ref Guid iid, out IntPtr value);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr text);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr text);
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr text, ref Guid iid, out IntPtr factory);
        [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr inspectable);
    }
}
