using System;

namespace PCL.Core.UI;

/// <summary>Background opacity never changes glass or text opacity in liquid-glass mode.</summary>
public static class LiquidGlassComposition
{
    public static double WindowOpacity(bool glassEnabled, int configuredOpacity)
        => glassEnabled ? 1d : Math.Clamp(configuredOpacity, 0, 600) / 1000d + .4d;

    public static double BackgroundOpacity(bool glassEnabled, int configuredOpacity)
        => glassEnabled ? Math.Clamp(configuredOpacity, 0, 600) / 600d : 1d;
}
