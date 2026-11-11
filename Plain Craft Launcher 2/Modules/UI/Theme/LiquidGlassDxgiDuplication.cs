using System;
using System.Runtime.InteropServices;

namespace PCL;

/// <summary>Minimal desktop-duplication ABI; all returned pointers own one COM reference.</summary>
internal static unsafe class LiquidGlassDxgiDuplication
{
    internal const int NotFound = unchecked((int)0x887A0002);
    internal const int WaitTimeout = unchecked((int)0x887A0027);
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid Output1Id = new("00cddea8-939b-4b83-a340-a685226666cc");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OutputDescription
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int Attached;
        public uint Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DuplicationDescription
    {
        public uint Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanlineOrdering, Scaling;
        public uint Rotation;
        public int SystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameInformation
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectanglesCoalesced, ProtectedContentMasked;
        public int PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }

    public static LiquidGlassD3D11.Resource Factory()
    {
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in FactoryId, out IntPtr factory));
        return new(factory);
    }

    public static LiquidGlassD3D11.Resource? Adapter(LiquidGlassD3D11.Resource factory, uint index) =>
        Enumerate(factory, 12, index);
    public static LiquidGlassD3D11.Resource? Output(LiquidGlassD3D11.Resource adapter, uint index) =>
        Enumerate(adapter, 7, index);

    private static LiquidGlassD3D11.Resource? Enumerate(LiquidGlassD3D11.Resource owner, int slot, uint index)
    {
        IntPtr pointer = IntPtr.Zero, instance = owner.Pointer;
        int result = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtable(instance)[slot])(instance, index, &pointer);
        if (result == NotFound) return null;
        Marshal.ThrowExceptionForHR(result);
        return new(pointer);
    }

    public static OutputDescription DescribeOutput(LiquidGlassD3D11.Resource output)
    {
        OutputDescription description = default;
        IntPtr instance = output.Pointer;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, OutputDescription*, int>)Vtable(instance)[7])(instance, &description));
        return description;
    }

    public static LiquidGlassD3D11.Resource Duplicate(LiquidGlassD3D11.Resource output, LiquidGlassD3D11.Resource device)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(output.Pointer, in Output1Id, out IntPtr instance));
        try
        {
            IntPtr duplication = IntPtr.Zero;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtable(instance)[22])(
                instance, device.Pointer, &duplication));
            return new(duplication);
        }
        finally { Marshal.Release(instance); }
    }

    public static DuplicationDescription DescribeDuplication(LiquidGlassD3D11.Resource duplication)
    {
        DuplicationDescription description = default;
        IntPtr instance = duplication.Pointer;
        ((delegate* unmanaged[Stdcall]<IntPtr, DuplicationDescription*, void>)Vtable(instance)[7])(instance, &description);
        return description;
    }

    public static bool Acquire(LiquidGlassD3D11.Resource duplication, out LiquidGlassD3D11.Resource? texture)
    {
        texture = null;
        IntPtr instance = duplication.Pointer, resource = IntPtr.Zero;
        FrameInformation frame = default;
        int result = ((delegate* unmanaged[Stdcall]<IntPtr, uint, FrameInformation*, IntPtr*, int>)Vtable(instance)[8])(
            instance, 20, &frame, &resource);
        if (result == WaitTimeout) return false;
        Marshal.ThrowExceptionForHR(result);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(resource, in LiquidGlassD3D11.Texture2DId, out IntPtr pointer));
            texture = new(pointer);
            return true;
        }
        catch
        {
            ReleaseFrame(duplication);
            throw;
        }
        finally { if (resource != IntPtr.Zero) Marshal.Release(resource); }
    }

    public static void ReleaseFrame(LiquidGlassD3D11.Resource duplication)
    {
        IntPtr instance = duplication.Pointer;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtable(instance)[14])(instance));
    }

    private static IntPtr* Vtable(IntPtr instance) => *(IntPtr**)instance;
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out IntPtr factory);
}
