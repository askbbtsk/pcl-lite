using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class WindowsGraphicsProjectionTests
{
    [TestMethod]
    public void CaptureUsesEmbeddedTypesWithoutExternalSdkOrRuntimeReferences()
    {
        var assembly = typeof(TahoeCapturePlatform).Assembly;
        Assert.AreEqual(assembly, typeof(GraphicsCaptureSession).Assembly);
        Assert.AreEqual(assembly, typeof(MarshalInterface<>).Assembly);
        foreach (var reference in assembly.GetReferencedAssemblies())
            Assert.IsFalse(new[] { "Microsoft.Windows.SDK.NET", "WinRT.Runtime" }.Contains(reference.Name));
        Assert.AreEqual(new Guid("a37624ab-8d5f-4650-9d3e-9eae3d9bc670"), typeof(IDirect3DDevice).GUID);
        Assert.AreEqual(new Guid("0bf4a146-13c1-4694-bee3-7abf15eaf586"), typeof(IDirect3DSurface).GUID);
    }

    [TestMethod]
    public void NativeD3DDeviceCanRoundTripThroughEmbeddedWinRtProjection()
    {
        // WARP is part of Windows and also exercises this ABI on runners without a GPU.
        LiquidGlassD3D11.CreateDevice(out var device, out var context, software: true);
        using (device)
        using (context)
        {
            using var staging = LiquidGlassD3D11.CreateStagingTexture(device, 16, 32);
            var description = LiquidGlassD3D11.Describe(staging);
            Assert.AreEqual(16u, description.Width);
            Assert.AreEqual(32u, description.Height);
            Assert.AreEqual(87u, description.Format);
            Assert.AreEqual(3u, description.Usage);
            Assert.AreEqual(0x20000u, description.CpuAccessFlags);
            var mapped = LiquidGlassD3D11.MapRead(context, staging);
            try
            {
                Assert.AreNotEqual(IntPtr.Zero, mapped.Data);
                Assert.IsTrue(mapped.RowPitch >= 16 * 4);
            }
            finally { LiquidGlassD3D11.Unmap(context, staging); }
            IntPtr pointer = LiquidGlassD3D11.CreateWinRtDevice(device);
            IDirect3DDevice projected;
            try { projected = MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
            using (projected)
            {
                IntPtr roundTrip = MarshalInterface<IDirect3DDevice>.FromManaged(projected);
                try
                {
                    var iid = typeof(IDirect3DDevice).GUID;
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(roundTrip, in iid, out var sameDevice));
                    try { Assert.AreNotEqual(IntPtr.Zero, sameDevice); }
                    finally { Marshal.Release(sameDevice); }
                }
                finally { Marshal.Release(roundTrip); }

                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !GraphicsCaptureSession.IsSupported()) return;
                using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(projected,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32(16, 16));
                int unexpectedFrames = 0;
                void Frame(Direct3D11CaptureFramePool _, object args) => unexpectedFrames++;
                pool.FrameArrived += Frame;
                pool.Recreate(projected, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32(32, 32));
                pool.FrameArrived -= Frame;
                Assert.AreEqual(0, unexpectedFrames, "A pool without a capture session must not produce frames.");
            }
        }
    }
    [TestMethod]
    public void NativeTextureCopyUsesCorrectRegionAndRowPitchAndReleasesIdempotently()
    {
        LiquidGlassD3D11.CreateDevice(out var device, out var context, software: true);
        using (device)
        using (context)
        {
            var pixels = Enumerable.Range(0, 4 * 3 * 4).Select(i => (byte)i).ToArray();
            using var source = LiquidGlassD3D11.CreateStagingTexture(device, 4, 3, pixels);
            using var target = LiquidGlassD3D11.CreateStagingTexture(device, 2, 2);
            for (int iteration = 0; iteration < 3; iteration++)
            {
                LiquidGlassD3D11.CopyRegion(context, target, source, 1, 1, 2, 2);
                var mapped = LiquidGlassD3D11.MapRead(context, target);
                try
                {
                    for (int row = 0; row < 2; row++)
                    {
                        var actual = new byte[8];
                        Marshal.Copy(mapped.Data + checked((int)mapped.RowPitch * row), actual, 0, actual.Length);
                        CollectionAssert.AreEqual(pixels.Skip(((row + 1) * 4 + 1) * 4).Take(8).ToArray(), actual);
                    }
                }
                finally { LiquidGlassD3D11.Unmap(context, target); }
            }
            target.Dispose();
            target.Dispose();
            Assert.Throws<ObjectDisposedException>(() => LiquidGlassD3D11.Describe(target));
            Assert.Throws<ArgumentOutOfRangeException>(() => LiquidGlassD3D11.CreateStagingTexture(device, 0, 1));
            Assert.Throws<ArgumentException>(() => LiquidGlassD3D11.CreateStagingTexture(device, 2, 2, new byte[1]));
        }
    }
}
