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

    public static LiquidGlassProfile GetProfile(int preset) => preset switch
    {
        0 => new(30, 0, 0, .35, false),
        2 => new(90, 2, .20, .65, true),
        3 => new(120, 3, .30, .8, true),
        _ => new(60, 1, .08, .5, true)
    };

    public static int NormalizePreset(int preset) => Math.Clamp(preset, 0, 3);
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
