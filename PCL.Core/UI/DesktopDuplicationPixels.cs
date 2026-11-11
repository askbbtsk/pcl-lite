using System;
using System.Runtime.InteropServices;

namespace PCL.Core.UI;

/// <summary>DXGI surfaces are unrotated; map desktop coordinates into the surface before cropping.</summary>
public static class DesktopDuplicationPixels
{
    public readonly record struct Crop(int X, int Y, int Width, int Height);

    public static (int X, int Y) SourcePoint(int x, int y, int width, int height, uint rotation) => rotation switch
    {
        0 or 1 => (x, y),
        2 => (y, height - 1 - x),
        3 => (width - 1 - x, height - 1 - y),
        4 => (width - 1 - y, x),
        _ => throw new ArgumentOutOfRangeException(nameof(rotation))
    };

    public static Crop SourceCrop(int x, int y, int width, int height, int sourceWidth, int sourceHeight, uint rotation)
    {
        bool portrait = rotation is 2 or 4;
        int desktopWidth = portrait ? sourceHeight : sourceWidth;
        int desktopHeight = portrait ? sourceWidth : sourceHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0 || x < 0 || y < 0
            || (long)x + width > desktopWidth || (long)y + height > desktopHeight)
            throw new ArgumentOutOfRangeException(nameof(width), "Crop must stay inside its desktop output.");
        var a = SourcePoint(x, y, sourceWidth, sourceHeight, rotation);
        var b = SourcePoint(x + width - 1, y + height - 1, sourceWidth, sourceHeight, rotation);
        return new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);
    }

    public static void Read(IntPtr data, int rowPitch, byte[] target, int width, int height, uint rotation)
    {
        if (data == IntPtr.Zero) throw new ArgumentNullException(nameof(data));
        int stride = checked(width * 4), length = checked(stride * height);
        int mappedWidth = rotation is 2 or 4 ? height : width;
        int mappedHeight = rotation is 2 or 4 ? width : height;
        if (rotation > 4 || width <= 0 || height <= 0 || rowPitch < checked(mappedWidth * 4) || target.Length != length)
            throw new ArgumentException("Invalid mapped texture dimensions.");
        _ = checked((mappedHeight - 1) * rowPitch + mappedWidth * 4);
        for (int y = 0; y < height; y++)
        {
            if (rotation is 0 or 1)
            {
                Marshal.Copy(data + checked(y * rowPitch), target, y * stride, stride);
                continue;
            }
            for (int x = 0; x < width; x++)
            {
                var point = SourcePoint(x, y, mappedWidth, mappedHeight, rotation);
                int source = checked(point.Y * rowPitch + point.X * 4);
                int destination = y * stride + x * 4;
                for (int channel = 0; channel < 4; channel++)
                    target[destination + channel] = Marshal.ReadByte(data, source + channel);
            }
        }
        for (int index = 3; index < target.Length; index += 4) target[index] = 255;
    }
}
