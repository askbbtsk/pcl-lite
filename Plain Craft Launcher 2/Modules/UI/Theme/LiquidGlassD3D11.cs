using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PCL;

/// <summary>
/// The small D3D11 boundary required by desktop capture. Uses Windows' d3d11.dll;
/// owns each returned COM reference and preserves the capture pipeline's GPU operations.
/// Vtable slots and layouts follow d3d11.h (ID3D11Device, Texture2D and DeviceContext).
/// </summary>
internal static unsafe class LiquidGlassD3D11
{
    internal static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    internal sealed class Resource(IntPtr pointer) : IDisposable
    {
        private IntPtr _pointer = pointer != IntPtr.Zero ? pointer : throw new ArgumentNullException(nameof(pointer));
        public IntPtr Pointer => _pointer != IntPtr.Zero ? _pointer : throw new ObjectDisposedException(nameof(Resource));
        public void Dispose()
        {
            IntPtr released = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
            if (released != IntPtr.Zero) Marshal.Release(released);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format;
        public uint SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MappedTexture
    {
        public IntPtr Data;
        public uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InitialTextureData
    {
        public IntPtr Data;
        public uint RowPitch, SlicePitch;
    }

    public static void CreateDevice(out Resource device, out Resource context, bool software = false, IntPtr adapter = default)
    {
        uint[] featureLevels = [0xb000, 0xa100]; // 11.0, 10.1; unchanged from the original capture path.
        int result = D3D11CreateDevice(adapter, adapter != IntPtr.Zero ? 0u : software ? 5u : 1u, IntPtr.Zero, 0x20,
            featureLevels, (uint)featureLevels.Length, 7, out IntPtr nativeDevice, out _, out IntPtr nativeContext);
        if (result < 0)
        {
            if (nativeContext != IntPtr.Zero) Marshal.Release(nativeContext);
            if (nativeDevice != IntPtr.Zero) Marshal.Release(nativeDevice);
            Marshal.ThrowExceptionForHR(result);
        }
        device = new Resource(nativeDevice);
        context = new Resource(nativeContext);
    }

    public static Resource CreateStagingTexture(Resource device, int width, int height, byte[]? initialPixels = null,
        bool cpuReadable = true)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (initialPixels != null && initialPixels.Length != checked(width * height * 4))
            throw new ArgumentException("Initial pixels must contain exactly width * height BGRA pixels.", nameof(initialPixels));
        var description = new TextureDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = 87, SampleCount = 1, Usage = cpuReadable ? 3u : 0u, CpuAccessFlags = cpuReadable ? 0x20000u : 0u
            // DXGI_FORMAT_B8G8R8A8_UNORM; D3D11_USAGE_STAGING; D3D11_CPU_ACCESS_READ.
        };
        IntPtr instance = device.Pointer, texture = IntPtr.Zero;
        var create = (delegate* unmanaged[Stdcall]<IntPtr, TextureDescription*, IntPtr, IntPtr*, int>)Vtable(instance)[5];
        fixed (byte* pixels = initialPixels)
        {
            var initial = new InitialTextureData { Data = (IntPtr)pixels, RowPitch = checked((uint)(width * 4)) };
            Marshal.ThrowExceptionForHR(create(instance, &description,
                initialPixels == null ? IntPtr.Zero : (IntPtr)(&initial), &texture));
        }
        return new Resource(texture);
    }

    public static TextureDescription Describe(Resource texture)
    {
        IntPtr instance = texture.Pointer;
        TextureDescription description = default;
        var describe = (delegate* unmanaged[Stdcall]<IntPtr, TextureDescription*, void>)Vtable(instance)[10];
        describe(instance, &description);
        return description;
    }

    public static void CopyRegion(Resource context, Resource target, Resource source, int x, int y, int width, int height)
    {
        var box = new Box { Left = checked((uint)x), Top = checked((uint)y),
            Right = checked((uint)(x + width)), Bottom = checked((uint)(y + height)), Back = 1 };
        IntPtr instance = context.Pointer;
        var copy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Vtable(instance)[46];
        copy(instance, target.Pointer, 0, 0, 0, 0, source.Pointer, 0, &box);
    }

    public static MappedTexture MapRead(Resource context, Resource texture)
    {
        IntPtr instance = context.Pointer;
        MappedTexture mapped = default;
        var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedTexture*, int>)Vtable(instance)[14];
        Marshal.ThrowExceptionForHR(map(instance, texture.Pointer, 0, 1, 0, &mapped));
        return mapped;
    }

    public static void Unmap(Resource context, Resource texture)
    {
        IntPtr instance = context.Pointer;
        var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vtable(instance)[15];
        unmap(instance, texture.Pointer, 0);
    }

    private static IntPtr* Vtable(IntPtr instance) => *(IntPtr**)instance;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags,
        [In] uint[] featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device,
        out uint featureLevel, out IntPtr context);

}
