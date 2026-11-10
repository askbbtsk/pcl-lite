using System;
using System.Windows;

namespace PCL.Core.UI;

/// <summary>Background opacity never changes glass or text opacity in liquid-glass mode.</summary>
public static class LiquidGlassComposition
{
    public static void Apply(bool glassEnabled, int configuredOpacity, Window window,
        UIElement background, UIElement wallpaper, UIElement desktop)
    {
        window.Opacity = WindowOpacity(glassEnabled, configuredOpacity);
        background.Opacity = wallpaper.Opacity = BackgroundOpacity(glassEnabled, configuredOpacity);
        // This source is also sampled by glass surfaces, which must remain fully visible.
        desktop.Opacity = 1d;
    }

    public static double WindowOpacity(bool glassEnabled, int configuredOpacity)
        => glassEnabled ? 1d : Math.Clamp(configuredOpacity, 0, 600) / 1000d + .4d;

    public static double BackgroundOpacity(bool glassEnabled, int configuredOpacity)
        => glassEnabled ? Math.Clamp(configuredOpacity, 0, 600) / 600d : 1d;
}
