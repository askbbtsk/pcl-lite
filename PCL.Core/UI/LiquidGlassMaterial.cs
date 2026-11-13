using System;
using System.Windows;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.UI.Theme;

namespace PCL.Core.UI;

/// <summary>Theme-harmonized optical tokens derived from the active PCL-Lite theme for Apple-style Liquid Glass.</summary>
public sealed record LiquidGlassThemePalette(
    bool Dark,
    bool HighContrast,
    Color WindowTint,
    Color SidebarTint,
    Color ToolbarTint,
    Color ButtonTint,
    Color CardSurface,
    Color CardBorder,
    Color Accent,
    Color ActionText,
    Color NavCheckedFill,
    Color NavHoverFill,
    Color NavPressFill,
    Color PrimaryButtonFill,
    Color PrimaryButtonHoverFill,
    Color PrimaryButtonText,
    Color FieldSurface,
    Color FieldFocusSurface,
    Color FieldBorder,
    Color Text,
    Color SecondaryText,
    Color SidebarSection,
    Color Brush1,
    Color Brush2,
    Color Brush3,
    Color Brush4,
    Color Brush6,
    Color Brush7,
    Color Brush8,
    Color Gray1,
    Color Gray2,
    Color Gray3);

/// <summary>Theme-aware material wash, independent from captured desktop colors and wallpaper opacity.</summary>
public static class LiquidGlassMaterial
{
    public static int FrostRadius(int configured) => Math.Clamp(configured, 0, 24);
    public static int RefractionSetting(int configured) => Math.Clamp(configured, 0, 10);
    public static double RefractionStrength(int configured) => RefractionSetting(configured) / 100d;
    public static int ResolveRefractionPercent(int percent, int legacySetting) =>
        percent == -1 ? RefractionSetting(legacySetting) * 20 : Math.Clamp(percent, 0, 200);
    public static double RefractionFromPercent(int percent) => Math.Clamp(percent, 0, 200) / 2000d;
    public static int CardOpacityPercent(int configured, bool dark) =>
        configured == -1 ? dark ? 77 : 80 : Math.Clamp(configured, 0, 100);
    public static Color CardTint(Color original, int configured) => configured == -1 ? original :
        Color.FromArgb((byte)Math.Round(Math.Clamp(configured, 0, 100) * 2.55),
            original.R, original.G, original.B);
    public static Color AdjustBrightness(Color original, int configured)
    {
        var amount = Math.Clamp(configured, -50, 50) / 100d;
        if (amount == 0) return original;
        byte Channel(byte value) => (byte)Math.Round(amount > 0
            ? value + (255 - value) * amount : value * (1 + amount));
        return Color.FromArgb(original.A, Channel(original.R), Channel(original.G), Channel(original.B));
    }
    public static Color Tint(bool dark) => dark ? Color.FromRgb(23, 29, 39) : Colors.White;
    public static double Opacity(bool dark, bool desktop, double original) =>
        desktop ? Math.Max(original, dark ? .55 : .65) : original;

    public static double SurfaceWashOpacity(bool dark, bool desktop, double wallpaperOpacity, double desktopOpacity) =>
        Math.Clamp(desktop ? desktopOpacity : wallpaperOpacity, 0d, 1d);

    /// <summary>Creates a subtle top-lit hairline rim for selected glass navigation capsules.</summary>
    public static Brush NavigationPillBorder(bool dark, bool isChecked, bool highContrast)
    {
        if (!isChecked) return Brushes.Transparent;
        if (highContrast) return SystemColors.HighlightTextBrush;
        var brush = new LinearGradientBrush(
            Color.FromArgb(dark ? (byte)46 : (byte)112, 255, 255, 255),
            Color.FromArgb(dark ? (byte)12 : (byte)26, 255, 255, 255),
            new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    public static (double Hue, double Saturation, double LightAdjust) ResolveCurrentThemeHsl()
    {
        if (ThemeService.GetCustomThemeArgs() is { } custom)
            return (custom.Hue, custom.Saturation, custom.LightAdjust);

        return ThemeService.CurrentTheme switch
        {
            ColorTheme.SkyBlue => (205d, 78d, 0d),
            ColorTheme.CatBlue => (210d, 80d, 0d),
            ColorTheme.DeathBlue => (226d, 58d, -4d),
            ColorTheme.HmclBlue => (234d, 48d, -4d),
            _ => (210d, 80d, 0d)
        };
    }

    public static LiquidGlassThemePalette ComputePalette(
        double hue, double saturation, double lightAdjust, bool dark, bool highContrast = false)
    {
        if (highContrast)
        {
            return new LiquidGlassThemePalette(
                dark, true,
                SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.WindowColor,
                SystemColors.WindowBrush.Color, SystemColors.WindowTextColor,
                SystemColors.HotTrackColor, SystemColors.WindowTextColor,
                SystemColors.HighlightColor, SystemColors.WindowColor, SystemColors.HighlightColor,
                SystemColors.HighlightColor, SystemColors.HighlightColor, SystemColors.HighlightTextColor,
                SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.WindowTextColor,
                SystemColors.WindowTextColor, SystemColors.WindowTextColor, SystemColors.WindowColor,
                SystemColors.WindowTextColor, SystemColors.HotTrackColor, SystemColors.HotTrackColor, SystemColors.HotTrackColor,
                SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.WindowColor,
                SystemColors.WindowTextColor, SystemColors.WindowTextColor, SystemColors.WindowTextColor);
        }

        double sat = Math.Clamp(saturation, 0d, 100d);
        double adj = Math.Clamp(lightAdjust, -20d, 20d);
        // Preserve neutral titanium/obsidian aesthetic on low-saturation presets (e.g. 玄素黑) while keeping clear CTA contrast.
        double accentSat = sat < 18d ? Math.Clamp(sat * 1.5d + 6d, 10d, 28d) : Math.Clamp(sat * 1.05d, 38d, 96d);

        Color winTint = HslColor(hue, sat * 0.35d, dark ? Math.Clamp(14d + adj * 0.12d, 9d, 20d) : 95.5d);
        Color sideTint = HslColor(hue, sat * 0.42d, dark ? Math.Clamp(17d + adj * 0.12d, 11d, 23d) : 94.0d);
        Color toolTint = HslColor(hue, sat * 0.35d, dark ? Math.Clamp(21d + adj * 0.12d, 14d, 27d) : 97.0d);
        Color btnTint = HslColor(hue, sat * 0.32d, dark ? Math.Clamp(22d + adj * 0.12d, 15d, 28d) : 96.5d);

        Color cardBase = HslColor(hue, sat * 0.16d, dark ? 17.5d : 98.5d);
        Color cardSurface = Color.FromArgb(dark ? (byte)196 : (byte)204, cardBase.R, cardBase.G, cardBase.B);
        Color accent = HslColor(hue, accentSat, dark ? 60d : 48d);
        Color actionText = HslColor(hue, Math.Min(100d, accentSat * 1.05d), dark ? 86d : 30d);

        Color cardBorder = dark
            ? Color.FromArgb(30, 255, 255, 255)
            : Color.FromArgb(26, accent.R, accent.G, accent.B);

        Color pearlTint = Blend(Colors.White, accent, 0.08d);
        Color navChecked = dark
            ? Color.FromArgb(84, accent.R, accent.G, accent.B)
            : Color.FromArgb(206, pearlTint.R, pearlTint.G, pearlTint.B);
        Color navHover = dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(118, 255, 255, 255);
        Color navPress = dark ? Color.FromArgb(44, 255, 255, 255) : Color.FromArgb(172, 255, 255, 255);

        Color primaryBackdrop = dark ? winTint : Colors.White;
        double primarySat = Math.Clamp(sat < 18d ? sat * 1.4d + 8d : sat * 0.92d, 10d, 92d);
        double primaryLight = dark ? 44d : 43d;
        Color primaryRgb = HslColor(hue, primarySat, primaryLight);
        Color primaryText = ChooseReadablePrimaryInk(hue, primarySat, ref primaryLight, primaryBackdrop, dark ? 0.82d : 0.89d, out primaryRgb);
        Color primaryHoverRgb = HslColor(hue, Math.Clamp(primarySat * 1.04d, 12d, 96d), Math.Clamp(primaryLight + 5d, 20d, 78d));
        Color primaryFill = Color.FromArgb(dark ? (byte)208 : (byte)226, primaryRgb.R, primaryRgb.G, primaryRgb.B);
        Color primaryHoverFill = Color.FromArgb(dark ? (byte)230 : (byte)242, primaryHoverRgb.R, primaryHoverRgb.G, primaryHoverRgb.B);

        Color fieldDark = HslColor(hue, sat * 0.25d, 15.5d);
        Color fieldSurface = dark
            ? Color.FromArgb(148, fieldDark.R, fieldDark.G, fieldDark.B)
            : Color.FromArgb(196, 255, 255, 255);
        Color fieldFocus = dark
            ? Color.FromArgb(182, fieldDark.R, fieldDark.G, fieldDark.B)
            : Color.FromArgb(232, 255, 255, 255);
        Color fieldBorder = dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(42, 0, 0, 0);

        Color text = dark ? Color.FromRgb(242, 244, 248) : Color.FromRgb(28, 34, 44);
        Color secondaryText = dark ? Color.FromRgb(186, 196, 212) : Color.FromRgb(82, 92, 108);
        Color sideSection = dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(88, 255, 255, 255);

        Color brush1 = HslColor(hue, sat * 0.20d, dark ? 95d : 22d);
        Color brush2 = HslColor(hue, accentSat, dark ? 68d : 44d);
        Color brush3 = accent;
        Color brush4 = HslColor(hue, accentSat * 0.92d, dark ? 74d : 56d);
        Color brush6 = HslColor(hue, sat * 0.45d, dark ? 30d : 88d);
        Color brush7 = HslColor(hue, sat * 0.38d, dark ? 24d : 92d);
        Color brush8 = HslColor(hue, sat * 0.30d, dark ? 20d : 95d);

        Color gray1 = dark ? Color.FromRgb(226, 232, 240) : Color.FromRgb(42, 54, 68);
        Color gray2 = dark ? Color.FromRgb(202, 212, 224) : Color.FromRgb(64, 78, 94);
        Color gray3 = dark ? Color.FromRgb(178, 190, 206) : Color.FromRgb(84, 98, 114);

        return new LiquidGlassThemePalette(
            dark, false,
            winTint, sideTint, toolTint, btnTint,
            cardSurface, cardBorder, accent, actionText,
            navChecked, navHover, navPress,
            primaryFill, primaryHoverFill, primaryText,
            fieldSurface, fieldFocus, fieldBorder,
            text, secondaryText, sideSection,
            brush1, brush2, brush3, brush4, brush6, brush7, brush8,
            gray1, gray2, gray3);
    }

    private static Color HslColor(double hue, double saturation, double lightness)
    {
        var (r, g, b) = PclThemeEngine.FromHsl2(hue, Math.Clamp(saturation, 0d, 100d), Math.Clamp(lightness, 0d, 100d));
        return Color.FromRgb((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
    }

    private static Color Blend(Color a, Color b, double amount)
    {
        double t = Math.Clamp(amount, 0d, 1d);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static Color ChooseReadablePrimaryInk(
        double hue, double saturation, ref double lightness, Color backdrop, double opacity, out Color resolvedRgb)
    {
        Color darkInk = Color.FromRgb(20, 26, 36);
        for (int step = 0; step < 8; step++)
        {
            resolvedRgb = HslColor(hue, saturation, lightness);
            Color eff = Blend(backdrop, resolvedRgb, opacity);
            double lumEff = RelativeLuminance(eff);
            double whiteRatio = 1.05d / (lumEff + 0.05d);
            double darkRatio = (lumEff + 0.05d) / (RelativeLuminance(darkInk) + 0.05d);
            if (whiteRatio >= 4.7d) return Colors.White;
            if (darkRatio >= 4.7d) return darkInk;
            lightness = whiteRatio >= darkRatio ? Math.Max(18d, lightness - 5d) : Math.Min(78d, lightness + 5d);
        }
        resolvedRgb = HslColor(hue, saturation, lightness);
        Color finalEff = Blend(backdrop, resolvedRgb, opacity);
        return RelativeLuminance(finalEff) > 0.18d ? darkInk : Colors.White;
    }

    private static double RelativeLuminance(Color c)
    {
        static double Lin(byte channel)
        {
            double s = channel / 255d;
            return s <= 0.03928d ? s / 12.92d : Math.Pow((s + 0.055d) / 1.055d, 2.4d);
        }
        return 0.2126d * Lin(c.R) + 0.7152d * Lin(c.G) + 0.0722d * Lin(c.B);
    }
}
