using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace PCL;

/// <summary>
/// A native magnifier samples behind the launcher without changing
/// global capture affinity. The existing glass renderer consumes the same BGRA frames.
/// </summary>
internal sealed class LiquidGlassScreenshotCapturePlatform(Func<int> frameRate) : ITahoeCapturePlatform
{
    private IntPtr _window;
    public bool IsSupported => Environment.Is64BitProcess && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
    public event EventHandler DisplaysChanged
    {
        add => SystemEvents.DisplaySettingsChanged += value;
        remove => SystemEvents.DisplaySettingsChanged -= value;
    }
    public bool TryExcludeWindow(IntPtr window, out uint originalAffinity)
    {
        _window = window;
        // Exclusion is local to the magnifier. Never call SetWindowDisplayAffinity.
        return GetWindowDisplayAffinity(window, out originalAffinity) && originalAffinity == 0;
    }
    public bool RestoreWindow(IntPtr window, uint affinity) => true; // No global affinity was changed.
    public IReadOnlyList<TahoeCaptureMonitor> GetMonitors() =>
        [new(new IntPtr(1), new Rect(GetSystemMetrics(76), GetSystemMetrics(77),
            GetSystemMetrics(78), GetSystemMetrics(79)))];
    public ITahoeMonitorCapture CreateMonitor(IntPtr monitor, Action<TahoeCaptureFrame, Action> present, Action<Exception> failed) =>
        new Capture(_window, GetSystemMetrics(76), GetSystemMetrics(77), frameRate, present, failed);

    private sealed class Capture : ITahoeMonitorCapture
    {
        private static readonly object LifetimeGate = new();
        private static Task _previousRetirement = Task.CompletedTask;
        private readonly IntPtr _window;
        private readonly int _originX, _originY;
        private readonly Func<int> _frameRate;
        private readonly Action<TahoeCaptureFrame, Action> _present;
        private readonly Action<Exception> _failed;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _predecessor;
        private TahoeCaptureRegion? _region;
        private int _closing, _pending;
        public Task Completion => _completion.Task;

        public Capture(IntPtr window, int x, int y, Func<int> frameRate,
            Action<TahoeCaptureFrame, Action> present, Action<Exception> failed)
        {
            _window = window; _originX = x; _originY = y;
            _frameRate = frameRate; _present = present; _failed = failed;
            lock (LifetimeGate)
            {
                _predecessor = _previousRetirement;
                _previousRetirement = _completion.Task;
            }
            var thread = new Thread(Run) { IsBackground = true, Name = "LiquidGlass/DesktopSampler" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        public void SetRegion(TahoeCaptureRegion region) => Volatile.Write(ref _region, region);
        public void Dispose() => Interlocked.Exchange(ref _closing, 1);

        private void Run()
        {
            IntPtr host = IntPtr.Zero, magnifier = IntPtr.Zero, dc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero, bits = IntPtr.Zero;
            bool initialized = false;
            try
            {
                // Serialize initialization/retirement on workers, never on the UI thread.
                _predecessor.GetAwaiter().GetResult();
                if (Volatile.Read(ref _closing) != 0) return;
                initialized = MagInitialize();
                if (!initialized) throw new Win32Exception();
                host = CreateWindowEx(0x08000080, "STATIC", "PCL desktop sampler", 0x82000000,
                    0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (host == IntPtr.Zero) throw new Win32Exception();
                magnifier = CreateWindowEx(0, "Magnifier", "", 0x50000000,
                    0, 0, 1, 1, host, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (magnifier == IntPtr.Zero) throw new Win32Exception();
                if (!MagSetWindowFilterList(magnifier, 0, 2, [_window, host])) throw new Win32Exception();
                var transform = new Matrix { M00 = 1, M11 = 1, M22 = 1 };
                if (!MagSetWindowTransform(magnifier, ref transform)) throw new Win32Exception();
                dc = CreateCompatibleDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) throw new Win32Exception();
                TahoeCaptureRegion? current = null;
                byte[]? previous = null, scratch = null;
                long last = 0;
                int warmup = 0;
                while (Volatile.Read(ref _closing) == 0 && IsWindow(_window))
                {
                    while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
                    {
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                    }
                    var region = Volatile.Read(ref _region);
                    if (region is null || !IsWindowVisible(_window) || IsIconic(_window))
                    {
                        ShowWindow(host, 0);
                        Thread.Sleep(16);
                        continue;
                    }
                    long now = Stopwatch.GetTimestamp();
                    int fps = _frameRate();
                    if (fps != 0 && now - last < Stopwatch.Frequency / (double)Math.Clamp(fps, 30, 240))
                    { Thread.Sleep(1); continue; }
                    last = now;
                    int x = checked(_originX + region.X), y = checked(_originY + region.Y);
                    if (current != region)
                    {
                        if (bitmap != IntPtr.Zero)
                        {
                            SelectObject(dc, oldBitmap); DeleteObject(bitmap);
                            bitmap = bits = IntPtr.Zero;
                        }
                        var info = new BitmapInfo
                        {
                            Size = 40, Width = region.Width, Height = -region.Height,
                            Planes = 1, BitCount = 32
                        };
                        bitmap = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
                        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception();
                        oldBitmap = SelectObject(dc, bitmap);
                        if (oldBitmap == IntPtr.Zero || oldBitmap == new IntPtr(-1)) throw new Win32Exception();
                        if (!SetWindowPos(magnifier, IntPtr.Zero, 0, 0, region.Width, region.Height, 0x0014))
                            throw new Win32Exception();
                        current = region; previous = scratch = null; warmup = 0;
                    }
                    // The helper must remain composited, immediately behind the launcher.
                    // A fully offscreen helper stops updating on this Windows build.
                    if (!SetWindowPos(host, _window, x, y, region.Width, region.Height, 0x0050))
                        throw new Win32Exception();
                    if (!MagSetWindowSource(magnifier, new RectNative
                        { Left = x, Top = y, Right = x + region.Width, Bottom = y + region.Height }))
                        throw new Win32Exception();
                    // Give the compositor time to honor the initial exclusion filter.
                    if (++warmup < 3 || Volatile.Read(ref _pending) != 0) continue;
                    if (!PrintWindow(host, dc, 2)) throw new Win32Exception();
                    byte[] pixels = scratch ?? new byte[checked(region.Width * region.Height * 4)];
                    Marshal.Copy(bits, pixels, 0, pixels.Length);
                    for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
                    var dirty = TahoeCaptureResources.DirtyRows(pixels, previous, region.Width, region.Height, false);
                    if (dirty.Height == 0) { scratch = pixels; continue; }
                    scratch = previous; previous = pixels;
                    Interlocked.Exchange(ref _pending, 1);
                    _present(new TahoeCaptureFrame(pixels, region, dirty.Y, dirty.Height),
                        () => Interlocked.Exchange(ref _pending, 0));
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (Volatile.Read(ref _closing) == 0) _failed(error);
            }
            finally
            {
                if (host != IntPtr.Zero) ShowWindow(host, 0);
                if (bitmap != IntPtr.Zero) { SelectObject(dc, oldBitmap); DeleteObject(bitmap); }
                if (dc != IntPtr.Zero) DeleteDC(dc);
                if (magnifier != IntPtr.Zero) DestroyWindow(magnifier);
                if (host != IntPtr.Zero) DestroyWindow(host);
                if (initialized) MagUninitialize();
                _completion.TrySetResult();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Matrix { public float M00,M01,M02,M10,M11,M12,M20,M21,M22; }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Hwnd; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width,Height; public ushort Planes,BitCount; public uint Compression,ImageSize; public int XPels,YPels; public uint ColorsUsed,ColorsImportant,Color; }
    [DllImport("Magnification.dll", SetLastError=true)] private static extern bool MagInitialize();
    [DllImport("Magnification.dll")] private static extern bool MagUninitialize();
    [DllImport("Magnification.dll", SetLastError=true)] private static extern bool MagSetWindowFilterList(IntPtr hwnd,uint mode,int count,[In] IntPtr[] windows);
    [DllImport("Magnification.dll", SetLastError=true)] private static extern bool MagSetWindowTransform(IntPtr hwnd,ref Matrix matrix);
    [DllImport("Magnification.dll", SetLastError=true)] private static extern bool MagSetWindowSource(IntPtr hwnd,RectNative rect);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateWindowEx(uint exStyle,string cls,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool GetWindowDisplayAffinity(IntPtr hwnd,out uint affinity);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool PeekMessage(out Message message,IntPtr hwnd,uint minimum,uint maximum,uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
