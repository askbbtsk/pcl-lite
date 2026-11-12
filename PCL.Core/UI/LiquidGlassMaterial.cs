using System;
using System.Windows.Media;

namespace PCL.Core.UI;

/// <summary>Theme-aware material wash, independent from captured desktop colors and wallpaper opacity.</summary>
public static class LiquidGlassMaterial
{
    public static int FrostRadius(int configured) => Math.Clamp(configured, 0, 24);
    public static int RefractionSetting(int configured) => Math.Clamp(configured, 5, 15);
    public static double RefractionStrength(int configured) => RefractionSetting(configured) / 100d;
    public static Color Tint(bool dark) => dark ? Color.FromRgb(23, 29, 39) : Colors.White;
    public static double Opacity(bool dark, bool desktop, double original) =>
        desktop ? Math.Max(original, dark ? .55 : .65) : original;
}
