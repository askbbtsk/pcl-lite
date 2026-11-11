using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
[DoNotParallelize]
public sealed class DxgiDesktopCaptureTests
{
    [TestMethod]
    public void RealMonitorSessionPublishesNewGeometryEvenWhenDesktopIsStationary()
    {
        var platform = new TahoeCapturePlatform((_, _) => { }, () => 60);
        if (!platform.IsSupported) { Assert.Inconclusive("The current Windows version does not support exclusion."); return; }
        var monitors = platform.GetMonitors();
        if (monitors.Count == 0) { Assert.Inconclusive("No desktop monitor is attached."); return; }
        var first = new System.Threading.Tasks.TaskCompletionSource<TahoeCaptureFrame>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var resized = new System.Threading.Tasks.TaskCompletionSource<TahoeCaptureFrame>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        ITahoeMonitorCapture capture;
        try
        {
            capture = platform.CreateMonitor(monitors[0].Handle, (frame, completed) =>
            {
                try
                {
                    if (frame.Region.Version == 1) first.TrySetResult(frame);
                    if (frame.Region.Version == 2) resized.TrySetResult(frame);
                }
                finally { completed(); }
            }, error => { failure = error; first.TrySetException(error); resized.TrySetException(error); });
        }
        catch (COMException error) when (error.HResult is unchecked((int)0x887A0004)
            or unchecked((int)0x887A0022) or unchecked((int)0x80070005))
        {
            Assert.Inconclusive("Desktop duplication is unavailable in this Windows session: " + error.HResult);
            return;
        }
        using (capture)
        {
            capture.SetRegion(new(0, 0, 8, 8, 0, 0, 1));
            Assert.IsTrue(first.Task.Wait(TimeSpan.FromSeconds(3)), "Initial desktop frame did not arrive.");
            Assert.AreEqual(8 * 8 * 4, first.Task.Result.Pixels.Length);
            capture.SetRegion(new(0, 0, 4, 4, 0, 0, 2));
            Assert.IsTrue(resized.Task.Wait(TimeSpan.FromSeconds(3)), "Changed geometry must publish without waiting for a new desktop frame.");
            Assert.AreEqual(4 * 4 * 4, resized.Task.Result.Pixels.Length);
            Assert.IsNull(failure);
            capture.Dispose();
            capture.Dispose();
            Assert.IsTrue(capture.Completion.Wait(TimeSpan.FromSeconds(2)),
                "Native ownership must be released before the same process duplicates this output again.");
        }
    }

    [TestMethod]
    public void DxgiFactoryEnumeratesOutputsOnTheirOwningAdapters()
    {
        using var factory = LiquidGlassDxgiDuplication.Factory();
        using var adapter = LiquidGlassDxgiDuplication.Adapter(factory, 0);
        Assert.IsNotNull(adapter, "A Windows graphics adapter, including software, must be enumerable.");
        for (uint index = 0; ; index++)
        {
            using var output = LiquidGlassDxgiDuplication.Output(adapter, index);
            if (output is null) break;
            var description = LiquidGlassDxgiDuplication.DescribeOutput(output);
            if (description.Attached != 0)
            {
                Assert.IsTrue(description.Right > description.Left);
                Assert.IsTrue(description.Bottom > description.Top);
                Assert.AreNotEqual(IntPtr.Zero, description.Monitor);
            }
        }
        Assert.IsNull(LiquidGlassDxgiDuplication.Adapter(factory, uint.MaxValue));
    }

    [TestMethod]
    public void AttachedDesktopCanAcquireCopyAndReleaseARealDuplicationFrame()
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
                if (LiquidGlassDxgiDuplication.DescribeOutput(output).Attached == 0) continue;
                LiquidGlassD3D11.CreateDevice(out var device, out var context, adapter: adapter.Pointer);
                using (device)
                using (context)
                {
                    LiquidGlassD3D11.Resource duplication;
                    try { duplication = LiquidGlassDxgiDuplication.Duplicate(output, device); }
                    catch (COMException error) when (error.HResult is unchecked((int)0x887A0004)
                        or unchecked((int)0x887A0022) or unchecked((int)0x80070005))
                    {
                        Assert.Inconclusive("This Windows session does not expose desktop duplication: " + error.HResult);
                        return;
                    }
                    using (duplication)
                    {
                        var description = LiquidGlassDxgiDuplication.DescribeDuplication(duplication);
                        Assert.AreEqual(87u, description.Format);
                        for (int attempt = 0; attempt < 30; attempt++)
                        {
                            if (!LiquidGlassDxgiDuplication.Acquire(duplication, out var frame)) continue;
                            try
                            {
                                using (frame)
                                using (var target = LiquidGlassD3D11.CreateStagingTexture(device, 1, 1))
                                {
                                    LiquidGlassD3D11.CopyRegion(context, target, frame!, 0, 0, 1, 1);
                                    var mapped = LiquidGlassD3D11.MapRead(context, target);
                                    try { Assert.AreNotEqual(IntPtr.Zero, mapped.Data); }
                                    finally { LiquidGlassD3D11.Unmap(context, target); }
                                }
                            }
                            finally { LiquidGlassDxgiDuplication.ReleaseFrame(duplication); }
                            return;
                        }
                        Assert.Inconclusive("The remote/headless desktop did not provide a frame within 600 ms.");
                    }
                }
            }
        }
        Assert.Inconclusive("No attached desktop output is available on this runner.");
    }

    [TestMethod]
    public void CaptureHasNoExternalWinRtOrGraphicsWrapperReferences()
    {
        var assembly = typeof(TahoeCapturePlatform).Assembly;
        foreach (var reference in assembly.GetReferencedAssemblies())
            Assert.IsFalse(new[] { "Microsoft.Windows.SDK.NET", "WinRT.Runtime", "Vortice.Direct3D11", "Vortice.DXGI" }.Contains(reference.Name));
        Assert.AreEqual(96, Marshal.SizeOf<LiquidGlassDxgiDuplication.OutputDescription>());
        Assert.AreEqual(36, Marshal.SizeOf<LiquidGlassDxgiDuplication.DuplicationDescription>());
        Assert.AreEqual(48, Marshal.SizeOf<LiquidGlassDxgiDuplication.FrameInformation>());
    }

    [TestMethod]
    public void NativeD3DDeviceCanCreateAndMapStagingTextures()
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
            using var source = LiquidGlassD3D11.CreateStagingTexture(device, 4, 3, pixels, cpuReadable: false);
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
