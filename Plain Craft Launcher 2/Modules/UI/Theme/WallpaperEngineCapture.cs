using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using D3D = PCL.LiquidGlassD3D11;

namespace PCL;

/// <summary>
/// Captures only the dedicated WE pop-out owned by this launcher.
/// Uses the OS ABI directly: no SDK projection or additional WinRT runtime is shipped.
/// </summary>
internal sealed unsafe class WallpaperEngineCapture : IDisposable
{
    internal sealed record Frame(byte[] Pixels, int Width, int Height, double SourceAgeMilliseconds);
    private readonly IntPtr _source;
    private readonly Action<Frame, Action> _present;
    private readonly Action<Exception> _failed;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closing, _pending;
    public Task Completion => _completion.Task;

    public WallpaperEngineCapture(IntPtr source, Action<Frame, Action> present, Action<Exception> failed)
    {
        _source = source; _present = present; _failed = failed;
        var thread = new Thread(Run) { IsBackground = true, Name = "WallpaperEngine/Capture" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Dispose() => Interlocked.Exchange(ref _closing, 1);
    private static IntPtr* Table(IntPtr value) => *(IntPtr**)value;
    private static D3D.Resource Query(IntPtr value, Guid id)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(value, in id, out var result));
        return new(result);
    }
    private static D3D.Resource Factory(string name, Guid id)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, (uint)name.Length, out var text));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, in id, out var result));
            return new(result);
        }
        finally { WindowsDeleteString(text); }
    }
    private static void Close(D3D.Resource? value)
    {
        if (value == null) return;
        try
        {
            using var closable = Query(value.Pointer, new("30d5a829-7fa4-4026-83bb-d75bae4ea99e"));
            var close = (delegate* unmanaged[Stdcall]<IntPtr, int>)Table(closable.Pointer)[6];
            _ = close(closable.Pointer);
        }
        finally { value.Dispose(); }
    }

    private void RequireBorderlessAccess()
    {
        using var factory = Factory("Windows.Graphics.Capture.GraphicsCaptureAccess",
            new("743ed370-06ec-5040-a58a-901f0f757095"));
        IntPtr native = default;
        var request = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Table(factory.Pointer)[6];
        Marshal.ThrowExceptionForHR(request(factory.Pointer, 0, &native));
        using var operation = new D3D.Resource(native);
        using var info = Query(native, new("00000036-0000-0000-c000-000000000046"));
        var status = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Table(info.Pointer)[7];
        int state = 0;
        while (Volatile.Read(ref _closing) == 0)
        {
            Marshal.ThrowExceptionForHR(status(info.Pointer, &state));
            if (state != 0) break;
            Thread.Sleep(20);
        }
        if (Volatile.Read(ref _closing) != 0)
        {
            var cancel = (delegate* unmanaged[Stdcall]<IntPtr, int>)Table(info.Pointer)[9];
            _ = cancel(info.Pointer);
            throw new OperationCanceledException();
        }
        int access = -1;
        var result = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Table(native)[8];
        Marshal.ThrowExceptionForHR(result(native, &access));
        if (access != 4) throw new UnauthorizedAccessException("Borderless wallpaper capture access was not granted.");
    }

    private void Run()
    {
        D3D.Resource? pool = null, session = null, staging = null;
        int initialized = int.MinValue;
        try
        {
            initialized = RoInitialize(1);
            Marshal.ThrowExceptionForHR(initialized);
            RequireBorderlessAccess();
            if (Volatile.Read(ref _closing) != 0) return;
            using var itemFactory = Factory("Windows.Graphics.Capture.GraphicsCaptureItem",
                new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"));
            IntPtr itemPointer = default;
            Guid itemId = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            var createItem = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Table(itemFactory.Pointer)[3];
            Marshal.ThrowExceptionForHR(createItem(itemFactory.Pointer, _source, &itemId, &itemPointer));
            using var item = new D3D.Resource(itemPointer);
            Size size = default;
            var getSize = (delegate* unmanaged[Stdcall]<IntPtr, Size*, int>)Table(itemPointer)[7];
            Marshal.ThrowExceptionForHR(getSize(itemPointer, &size));
            ValidateSize(size);
            D3D.CreateDevice(out var device, out var context);
            using var ownedDevice = device;
            using var ownedContext = context;
            using var dxgi = Query(device.Pointer, new("54ec77fa-1377-44e6-8c32-88fd5f44c84c"));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Pointer, out var inspectable));
            using var runtimeBase = new D3D.Resource(inspectable);
            using var runtime = Query(inspectable, new("a37624ab-8d5f-4650-9d3e-9eae3d9bc670"));
            using var poolFactory = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool",
                new("589b103f-6bbc-5df5-a991-02e28b3b66d5"));
            IntPtr poolPointer = default;
            var createPool = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, Size, IntPtr*, int>)Table(poolFactory.Pointer)[6];
            Marshal.ThrowExceptionForHR(createPool(poolFactory.Pointer, runtime.Pointer, 87, 2, size, &poolPointer));
            pool = new(poolPointer);
            IntPtr sessionPointer = default;
            var createSession = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Table(poolPointer)[10];
            Marshal.ThrowExceptionForHR(createSession(poolPointer, itemPointer, &sessionPointer));
            session = new(sessionPointer);
            using (var options = Query(sessionPointer, new("f2cdd966-22ae-5ea1-9596-3a289344c3be")))
            {
                var set = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Table(options.Pointer)[7];
                Marshal.ThrowExceptionForHR(set(options.Pointer, 0));
            }
            using (var options = Query(sessionPointer, new("2c39ae40-7d2e-5044-804e-8b6799d4cf9e")))
            {
                var set = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Table(options.Pointer)[7];
                Marshal.ThrowExceptionForHR(set(options.Pointer, 0));
            }
            var start = (delegate* unmanaged[Stdcall]<IntPtr, int>)Table(sessionPointer)[6];
            Marshal.ThrowExceptionForHR(start(sessionPointer));
            var next = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Table(poolPointer)[7];
            byte[]? pixels = null;
            long lastPresented = 0;
            while (Volatile.Read(ref _closing) == 0 && WallpaperEngineWindows.IsWindow(_source))
            {
                IntPtr framePointer = default;
                Marshal.ThrowExceptionForHR(next(poolPointer, &framePointer));
                if (framePointer == IntPtr.Zero) { Thread.Sleep(3); continue; }
                var frame = new D3D.Resource(framePointer);
                Size contentSize = default;
                try
                {
                    var getContentSize = (delegate* unmanaged[Stdcall]<IntPtr, Size*, int>)Table(framePointer)[8];
                    Marshal.ThrowExceptionForHR(getContentSize(framePointer, &contentSize));
                    if (contentSize.Width != size.Width || contentSize.Height != size.Height) continue;
                    long now = Stopwatch.GetTimestamp();
                    if (now - lastPresented < Stopwatch.Frequency / 60 ||
                        Interlocked.CompareExchange(ref _pending, 1, 0) != 0) continue;
                    bool handedOff = false;
                    try
                    {
                        long timestamp = 0;
                        var getTime = (delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Table(framePointer)[7];
                        Marshal.ThrowExceptionForHR(getTime(framePointer, &timestamp));
                        IntPtr surfacePointer = default;
                        var surface = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Table(framePointer)[6];
                        Marshal.ThrowExceptionForHR(surface(framePointer, &surfacePointer));
                        using var surfaceObject = new D3D.Resource(surfacePointer);
                        using var access = Query(surfacePointer, new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"));
                        IntPtr texturePointer = default;
                        Guid textureId = D3D.Texture2DId;
                        var getTexture = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Table(access.Pointer)[3];
                        Marshal.ThrowExceptionForHR(getTexture(access.Pointer, &textureId, &texturePointer));
                        using var texture = new D3D.Resource(texturePointer);
                        staging ??= D3D.CreateStagingTexture(device, size.Width, size.Height);
                        pixels ??= new byte[checked(size.Width * size.Height * 4)];
                        D3D.CopyRegion(context, staging, texture, 0, 0, size.Width, size.Height);
                        var mapped = D3D.MapRead(context, staging);
                        try
                        {
                            for (int y = 0; y < size.Height; y++)
                                Marshal.Copy(mapped.Data + checked((int)(y * mapped.RowPitch)),
                                    pixels, y * size.Width * 4, size.Width * 4);
                        }
                        finally { D3D.Unmap(context, staging); }
                        lastPresented = now;
                        double age = (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - timestamp / 1e7) * 1000;
                        _present(new(pixels, size.Width, size.Height, age), () => Volatile.Write(ref _pending, 0));
                        handedOff = true;
                    }
                    finally { if (!handedOff) Volatile.Write(ref _pending, 0); }
                }
                finally
                {
                    Close(frame);
                    if (contentSize.Width > 0 && contentSize.Height > 0 &&
                        (contentSize.Width != size.Width || contentSize.Height != size.Height))
                    {
                        ValidateSize(contentSize);
                        var recreate = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, Size, int>)Table(poolPointer)[6];
                        Marshal.ThrowExceptionForHR(recreate(poolPointer, runtime.Pointer, 87, 2, contentSize));
                        size = contentSize;
                        staging?.Dispose(); staging = null; pixels = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (Volatile.Read(ref _closing) == 0) _failed(error);
        }
        finally
        {
            try { staging?.Dispose(); }
            finally
            {
                try { Close(session); }
                finally
                {
                    try { Close(pool); }
                    finally
                    {
                        if (initialized >= 0) RoUninitialize();
                        _completion.TrySetResult();
                    }
                }
            }
        }
    }

    private static void ValidateSize(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > 16_777_216)
            throw new InvalidOperationException("Wallpaper render dimensions are invalid.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern void RoUninitialize();
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string text, uint length, out IntPtr value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoGetActivationFactory(
        IntPtr name, in Guid id, out IntPtr factory);
    [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr device, out IntPtr runtimeDevice);
}
