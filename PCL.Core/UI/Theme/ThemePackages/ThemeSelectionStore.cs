using System;
using System.IO;
using System.Text.Json;

namespace PCL.Core.UI.Theme.Packages;

public sealed record ThemeSelection(string? Id = null, string? ContentHash = null,
    string? ForcedConsent = null, bool Pending = false);

/// <summary>Separate optional state; never rewrites the user's normal launcher configuration.</summary>
public sealed class ThemeSelectionStore(string themesDirectory)
{
    private string StatePath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(themesDirectory))!, "theme-selection.json");

    public ThemeSelection Read()
    {
        if (!File.Exists(StatePath)) return new();
        if (new FileInfo(StatePath).Length > 65536) throw new InvalidDataException("Invalid theme selection size.");
        return JsonSerializer.Deserialize<ThemeSelection>(File.ReadAllBytes(StatePath), ThemeApi.JsonOptions)
            ?? throw new InvalidDataException("Invalid theme selection.");
    }

    public void Write(ThemeSelection value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        string temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, ThemeApi.JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, StatePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
