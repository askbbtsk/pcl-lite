using System;

namespace PCL.Core.UI;

public sealed record LiquidGlassProfile(int FrameRate, double BlurRadius, double Dispersion,
    double Highlight, bool Motion);

/// <summary>Deterministic presets; cost labels are relative expectations, not hardware benchmarks.</summary>
public static class LiquidGlassPerformance
{
    public const int MinimumFrameRate = 30;
    public const int MaximumLimitedFrameRate = 240;
    public const int UnlimitedSliderValue = 211;
    public const int CustomPreset = 4;

    public static LiquidGlassProfile GetProfile(int preset) => preset switch
    {
        0 => new(30, 0, 0, .35, false),
        2 => new(90, 2, .20, .65, true),
        3 => new(120, 3, .30, .8, true),
        _ => new(60, 1, .08, .5, true)
    };

    public static int NormalizePreset(int preset) => Math.Clamp(preset, 0, CustomPreset);
    public static int Adjustment(int configured, int fallback, int maximum) =>
        Math.Clamp(configured == -1 ? fallback : configured, 0, maximum);
    public static int SamplingFrameRate(int configured, int animationFrameRate) =>
        NormalizeFrameRate(configured == -1 ? animationFrameRate : configured);
    public static (int Width, int Height) SamplingSize(int width, int height, int quality)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        var scale = Math.Clamp(quality, 25, 100) / 100d;
        return (Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)));
    }
    public static LiquidGlassProfile ResolveProfile(int preset, int frameRate, int blur,
        int dispersion, int highlight, int motion)
    {
        var recommended = GetProfile(preset);
        return new LiquidGlassProfile(NormalizeFrameRate(frameRate),
            Adjustment(blur, (int)recommended.BlurRadius, 32),
            Adjustment(dispersion, (int)Math.Round(recommended.Dispersion * 100), 100) / 100d,
            Adjustment(highlight, (int)Math.Round(recommended.Highlight * 100), 100) / 100d,
            motion == -1 ? recommended.Motion : motion != 0);
    }
    public static bool IsEnabled(bool enabled, bool performanceConsent) => enabled && performanceConsent;
    public static bool ShouldCaptureDesktop(bool enabled, bool performanceConsent, bool desktopRequested, bool desktopConsent)
        => IsEnabled(enabled, performanceConsent) && desktopRequested && desktopConsent;
    public static int NormalizeFrameRate(int fps) => fps == 0 ? 0 : Math.Clamp(fps, MinimumFrameRate, MaximumLimitedFrameRate);
    public static int FrameRateFromSlider(int value) => value >= UnlimitedSliderValue ? 0 :
        Math.Clamp(value, 0, UnlimitedSliderValue - 1) + MinimumFrameRate;
    public static int SliderFromFrameRate(int fps) => fps == 0 ? UnlimitedSliderValue :
        NormalizeFrameRate(fps) - MinimumFrameRate;
    public static bool ShouldRender(long elapsedTicks, long frequency, int fps)
        => elapsedTicks >= 0 && frequency > 0 && (fps == 0 || elapsedTicks >= frequency / (double)NormalizeFrameRate(fps));
}
