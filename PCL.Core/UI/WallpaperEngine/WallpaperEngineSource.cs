using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Core.UI.WallpaperEngine;

/// <summary>A read-only snapshot of the desktop assignment; never a playback-position contract.</summary>
public sealed record WallpaperEngineSelection(string File, string PropertiesJson);

public sealed record WallpaperEngineProject(string File, string Kind, string PropertiesJson);

public static class WallpaperEngineSource
{
    public static WallpaperEngineSelection? ReadSelection(
        string json, string userName, int monitorIndex, string? deviceName = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement profile = default;
        if (root.TryGetProperty("general", out _))
            profile = root;
        else
        {
            foreach (var entry in root.EnumerateObject())
                if (entry.Name.Equals(userName, StringComparison.OrdinalIgnoreCase) &&
                    entry.Value.ValueKind == JsonValueKind.Object)
                { profile = entry.Value; break; }
        }
        if (profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty("general", out var general) ||
            !general.TryGetProperty("wallpaperconfig", out var wallpaper) ||
            !wallpaper.TryGetProperty("selectedwallpapers", out var selected) ||
            selected.ValueKind != JsonValueKind.Object)
            return null;

        string monitorKey = "Monitor" + Math.Max(0, monitorIndex);
        JsonElement assignment = default;
        if (!selected.TryGetProperty(monitorKey, out assignment) &&
            (deviceName == null || !selected.TryGetProperty(deviceName, out assignment)))
        {
            // Clone/span layouts explicitly use one assignment for all displays.
            if (!wallpaper.TryGetProperty("layout", out var layout) ||
                !layout.TryGetInt32(out int mode) || mode == 0)
                return null;
            var entries = selected.EnumerateObject().ToArray();
            if (entries.Length != 1) return null;
            monitorKey = entries[0].Name;
            assignment = entries[0].Value;
        }
        if (assignment.ValueKind != JsonValueKind.Object ||
            !assignment.TryGetProperty("file", out var file) ||
            file.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(file.GetString()))
            return null;
        string path = file.GetString()!;
        string properties = "{}";
        if (profile.TryGetProperty("wproperties", out var allProperties) &&
            allProperties.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in allProperties.EnumerateObject())
            {
                if (!SamePath(entry.Name, path) || entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (entry.Value.TryGetProperty(monitorKey, out var values) &&
                    values.ValueKind == JsonValueKind.Object)
                    properties = values.GetRawText();
                break;
            }
        }
        return new(path, properties);
    }

    public static WallpaperEngineProject ResolveProject(
        WallpaperEngineSelection selection, Func<string, string> readFile)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(readFile);
        string current = Path.GetFullPath(selection.File);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presets = new List<JsonObject>();
        string kind;
        for (int depth = 0; ; depth++)
        {
            if (depth >= 8 || !seen.Add(current))
                throw new InvalidDataException("Wallpaper Engine preset dependency cycle or depth limit.");
            string extension = Path.GetExtension(current).ToLowerInvariant();
            if (extension is ".mp4" or ".webm" or ".wmv" or ".avi" or ".mov" or ".m4v")
            { kind = "video"; break; }
            if (extension is ".html" or ".htm")
            { kind = "web"; break; }
            if (extension == ".pkg")
            { kind = "scene"; break; }
            if (extension != ".json")
                throw new InvalidDataException("Unsupported Wallpaper Engine wallpaper file.");

            var manifest = JsonNode.Parse(readFile(current)) as JsonObject
                ?? throw new InvalidDataException("Wallpaper Engine project is not a JSON object.");
            string type = manifest["type"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
            if (type is "video" or "scene" or "web")
            { kind = type; break; }
            if (type.Length != 0)
                throw new InvalidDataException("Unsupported Wallpaper Engine project type: " + type);
            string dependency = manifest["dependency"]?.GetValue<string>() ?? "";
            if (dependency.Length == 0 || !dependency.All(char.IsAsciiDigit))
                throw new InvalidDataException("Wallpaper Engine preset dependency is invalid.");
            if (manifest["preset"] is JsonObject preset) presets.Add(preset);
            string parent = Path.GetDirectoryName(Path.GetDirectoryName(current)!)!;
            current = Path.Combine(parent, dependency, "project.json");
        }
        var merged = new JsonObject();
        // The selected preset wins over its base preset; current per-monitor values win last.
        for (int i = presets.Count - 1; i >= 0; i--)
            foreach (var pair in presets[i])
                if (pair.Value != null) merged[pair.Key] = pair.Value.DeepClone();
        if (JsonNode.Parse(selection.PropertiesJson) is not JsonObject overrides)
            throw new InvalidDataException("Wallpaper Engine properties are not an object.");
        foreach (var pair in overrides) merged[pair.Key] = pair.Value?.DeepClone();
        // This applies only to the uniquely named Lite renderer, never the desktop instance.
        merged["volume"] = 0;
        return new(current, kind, merged.ToJsonString());
    }

    public static (int Width, int Height) RenderSize(int width, int height, int quality)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        double scale = Math.Clamp(quality, 25, 100) / 100d;
        scale = Math.Min(scale, 3840d / Math.Max(width, height));
        int w = Math.Max(16, (int)Math.Round(width * scale));
        int h = Math.Max(16, (int)Math.Round(height * scale));
        return (w + (w & 1), h + (h & 1));
    }

    public static int SelectMonitor(
        double left, double top, double width, double height,
        IReadOnlyList<(double Left, double Top, double Width, double Height)> monitors,
        int previous = -1)
    {
        if (monitors.Count == 0) return -1;
        int selected = previous >= 0 && previous < monitors.Count ? previous : 0;
        double area = -1;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            double overlap = Math.Max(0, Math.Min(left + width, m.Left + m.Width) - Math.Max(left, m.Left)) *
                             Math.Max(0, Math.Min(top + height, m.Top + m.Height) - Math.Max(top, m.Top));
            if (overlap > area || overlap == area && i == previous)
            { area = overlap; selected = i; }
        }
        return selected;
    }

    private static bool SamePath(string first, string second) =>
        first.Replace('\\', '/').TrimEnd('/').Equals(
            second.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
