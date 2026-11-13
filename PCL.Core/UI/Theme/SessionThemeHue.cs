using System;

namespace PCL.Core.UI.Theme;

/// <summary>One hue per random preset per launch; never writes user theme settings.</summary>
public static class SessionThemeHue
{
    public const int JesterPreset = 13;
    public const int LuckyPreset = 12;
    private static readonly int JesterHue = Random.Shared.Next(360);
    private static readonly int LuckyHue = Random.Shared.Next(360);

    public static int Resolve(int preset, int configuredHue) =>
        preset switch { JesterPreset => JesterHue, LuckyPreset => LuckyHue, _ => configuredHue };
}
