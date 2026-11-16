using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCL.Core.UI.WallpaperEngine;

public static class WallpaperEngineCommands
{
    /// <summary>
    /// WE extracts RAW~(...)~END from GetCommandLineW before normal argv decoding.
    /// ProcessStartInfo.ArgumentList escapes its JSON quotes and WE then returns 4.
    /// Only this command uses the documented raw transport, without invoking a shell.
    /// </summary>
    public static string PropertiesArguments(string windowName, string wrappedProperties)
    {
        if (!Regex.IsMatch(windowName,
                @"\APCL-Lite-Wallpaper-[0-9]+-[0-9]+-[0-9a-fA-F]{32}\z",
                RegexOptions.CultureInvariant))
            throw new ArgumentException("Not a launcher-owned wallpaper window.", nameof(windowName));
        if (!wrappedProperties.StartsWith("RAW~(", StringComparison.Ordinal) ||
            !wrappedProperties.EndsWith(")~END", StringComparison.Ordinal))
            throw new ArgumentException("Missing Wallpaper Engine raw JSON delimiters.", nameof(wrappedProperties));
        string json = wrappedProperties[5..^5];
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Wallpaper properties must be a JSON object.", nameof(wrappedProperties));
        // A delimiter inside a JSON string must not terminate the raw payload early.
        json = document.RootElement.GetRawText().Replace(")~END", "\\u0029~END", StringComparison.Ordinal);
        return $"-control applyProperties -location \"{windowName}\" -properties RAW~({json})~END";
    }
}
