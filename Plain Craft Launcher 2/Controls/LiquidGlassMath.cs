using System;
using System.Windows;
using System.Windows.Media;

namespace PCL;

/// <summary>Geometry shared by the Tahoe material and its verification harness.</summary>
internal static class TahoeGlassMath
{
    public static double FiniteClamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    public static double NormalizeRadius(double radius, Size size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height)
            ? Math.Min(FiniteClamp(radius, 0, 4096, 24), Math.Max(0, Math.Min(size.Width, size.Height) / 2))
            : 0;

    public static double EighthPower(double ratio)
    {
        ratio = FiniteClamp(ratio, 0, 1, 0);
        double square = ratio * ratio;
        double fourth = square * square;
        return fourth * fourth;
    }

    public static bool TryGetMatrix(Visual from, Visual to, out Matrix matrix)
    {
        matrix = Matrix.Identity;
        try
        {
            GeneralTransform transform = from.TransformToVisual(to);
            if (!transform.TryTransform(new Point(), out Point origin) ||
                !transform.TryTransform(new Point(1, 0), out Point x) ||
                !transform.TryTransform(new Point(0, 1), out Point y)) return false;
            matrix = new Matrix(x.X - origin.X, x.Y - origin.Y, y.X - origin.X, y.Y - origin.Y, origin.X, origin.Y);
            return IsFinite(matrix) && matrix.HasInverse;
        }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static bool IsFinite(Matrix matrix) =>
        double.IsFinite(matrix.M11) && double.IsFinite(matrix.M12) &&
        double.IsFinite(matrix.M21) && double.IsFinite(matrix.M22) &&
        double.IsFinite(matrix.OffsetX) && double.IsFinite(matrix.OffsetY);

    public static Rect SamplingBounds(Matrix localToSource, Size panelSize, Size sourceSize,
        double refraction, double blur)
    {
        if (!IsFinite(localToSource) || !localToSource.HasInverse ||
            !double.IsFinite(panelSize.Width) || !double.IsFinite(panelSize.Height) ||
            !double.IsFinite(sourceSize.Width) || !double.IsFinite(sourceSize.Height) ||
            panelSize.Width <= 0 || panelSize.Height <= 0 || sourceSize.Width <= 0 || sourceSize.Height <= 0)
            return Rect.Empty;
        Rect crop = new MatrixTransform(localToSource).TransformBounds(new Rect(panelSize));
        double f = FiniteClamp(refraction, 0, .15, .05);
        double b = FiniteClamp(blur, 0, 32, 2);
        // f is the video's full-source UV coefficient, not a fixed DIP displacement.
        crop.Inflate(sourceSize.Width * f + b * 3 + 2, sourceSize.Height * f + b * 3 + 2);
        crop.Intersect(new Rect(sourceSize));
        return crop;
    }

    public static Vector SourceDirection(Matrix localToSource, Vector normal)
    {
        Vector direction = localToSource.Transform(normal);
        double length = direction.Length;
        return double.IsFinite(length) && length > 1e-9 ? direction / length : new Vector();
    }
}
