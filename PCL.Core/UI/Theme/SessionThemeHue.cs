using System;

namespace PCL.Core.UI.Theme;

/// <summary>One launch-wide hue for the Jester preset; never writes user theme settings.</summary>
public static class SessionThemeHue
{
    public const int JesterPreset = 13;
    private static readonly int LaunchHue = Random.Shared.Next(360);

    public static int Resolve(int preset, int configuredHue) =>
        preset == JesterPreset ? LaunchHue : configuredHue;
}
