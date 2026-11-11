using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class DesktopDuplicationPixelsTests
{
    [TestMethod]
    public void EveryRotationCropsAndReadsExpectedDesktopPixelsWithPaddedRows()
    {
        const int sourceWidth = 4, sourceHeight = 3;
        foreach (uint rotation in new uint[] { 0, 1, 2, 3, 4 })
        {
            int width = rotation is 2 or 4 ? sourceHeight : sourceWidth;
            int height = rotation is 2 or 4 ? sourceWidth : sourceHeight;
            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            for (int w = 1; w <= width - x; w++)
            for (int h = 1; h <= height - y; h++)
            {
                var crop = DesktopDuplicationPixels.SourceCrop(x, y, w, h, sourceWidth, sourceHeight, rotation);
                int rowPitch = crop.Width * 4 + 12;
                byte[] mapped = new byte[rowPitch * crop.Height];
                for (int row = 0; row < crop.Height; row++)
                for (int col = 0; col < crop.Width; col++)
                    mapped[row * rowPitch + col * 4] = (byte)((row + crop.Y) * sourceWidth + col + crop.X);
                var handle = GCHandle.Alloc(mapped, GCHandleType.Pinned);
                try
                {
                    var actual = new byte[w * h * 4];
                    DesktopDuplicationPixels.Read(handle.AddrOfPinnedObject(), rowPitch, actual, w, h, rotation);
                    for (int row = 0; row < h; row++)
                    for (int col = 0; col < w; col++)
                    {
                        (int sx, int sy) = rotation switch
                        {
                            0 or 1 => (x + col, y + row),
                            2 => (y + row, sourceHeight - 1 - x - col),
                            3 => (sourceWidth - 1 - x - col, sourceHeight - 1 - y - row),
                            _ => (sourceWidth - 1 - y - row, x + col)
                        };
                        Assert.AreEqual((byte)(sy * sourceWidth + sx), actual[(row * w + col) * 4]);
                        Assert.AreEqual((byte)255, actual[(row * w + col) * 4 + 3]);
                    }
                }
                finally { handle.Free(); }
            }
        }
    }

    [TestMethod]
    public void InvalidCropAndMappedBuffersAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopDuplicationPixels.SourceCrop(-1, 0, 1, 1, 4, 3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopDuplicationPixels.SourceCrop(0, 0, int.MaxValue, 1, 4, 3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopDuplicationPixels.SourceCrop(0, 0, 1, 1, 4, 3, 99));
        Assert.Throws<ArgumentNullException>(() => DesktopDuplicationPixels.Read(IntPtr.Zero, 4, new byte[4], 1, 1, 1));
        Assert.Throws<ArgumentException>(() => DesktopDuplicationPixels.Read(new IntPtr(1), 3, new byte[4], 1, 1, 1));
    }

    [TestMethod]
    public void LightDesktopUsesWhiteWashAndDarkDesktopUsesDarkWashWithoutChangingWallpaperMode()
    {
        Assert.AreEqual(Colors.White, LiquidGlassMaterial.Tint(false));
        var dark = LiquidGlassMaterial.Tint(true);
        Assert.IsTrue(dark.R < 40 && dark.G < 40 && dark.B < 40);
        Assert.IsTrue(LiquidGlassMaterial.Opacity(false, true, .1) >= .6);
        Assert.IsTrue(LiquidGlassMaterial.Opacity(true, true, .1) >= .5);
        Assert.AreEqual(.1, LiquidGlassMaterial.Opacity(false, false, .1));
        Assert.AreEqual(.1, LiquidGlassMaterial.Opacity(true, false, .1));
    }
}
