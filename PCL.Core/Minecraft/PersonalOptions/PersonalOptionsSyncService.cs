using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PCL.Core.Minecraft.PersonalOptions;

[Flags]
public enum PersonalOptionCategories
{
    None = 0,
    KeyBindings = 1,
    Mouse = 2,
    View = 4,
    Accessibility = 8,
    Audio = 16,
    All = KeyBindings | Mouse | View | Accessibility | Audio,
}

public sealed class PersonalOptionsProfile
{
    public int SchemaVersion { get; init; } = 1;
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.Ordinal);
}

public sealed record PersonalOptionsMergeResult(string Text, IReadOnlyList<string> AppliedKeys);

public sealed record PersonalOptionsSession(string OptionsPath, string ProfilePath, PersonalOptionCategories Categories,
    bool TargetUsesModernKeyNames, bool FovIsDegrees);

/// <summary>Canonicalizes and synchronizes supported vanilla options across Minecraft versions.</summary>
public static class PersonalOptionsSyncService
{
    private static readonly HashSet<string> VanillaBindings = CreateVanillaBindings();

    private static HashSet<string> CreateVanillaBindings()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            "key_key.attack", "key_key.use", "key_key.pickItem", "key_key.forward", "key_key.back",
            "key_key.left", "key_key.right", "key_key.jump", "key_key.sneak", "key_key.sprint",
            "key_key.drop", "key_key.inventory", "key_key.chat", "key_key.playerlist", "key_key.command",
            "key_key.screenshot", "key_key.togglePerspective", "key_key.smoothCamera", "key_key.fullscreen",
            "key_key.spectatorOutlines", "key_key.advancements", "key_key.swapOffhand",
            "key_key.saveToolbarActivator", "key_key.loadToolbarActivator",
        };
        for (var index = 1; index <= 9; index++) keys.Add($"key_key.hotbar.{index}");
        return keys;
    }

    private static readonly HashSet<string> MouseOptions = new(StringComparer.Ordinal)
    {
        "mouseSensitivity", "invertYMouse", "mouseWheelSensitivity", "discrete_mouse_scroll",
        "rawMouseInput", "touchscreen", "toggleSprint", "toggleCrouch",
    };

    private static readonly HashSet<string> ViewOptions = new(StringComparer.Ordinal)
    {
        "fov", "fovEffectScale", "screenEffectScale", "bobView",
    };

    private static readonly HashSet<string> AccessibilityOptions = new(StringComparer.Ordinal)
    {
        "showSubtitles", "autoJump", "highContrast", "darknessEffectScale", "hideLightningFlashes",
        "notificationDisplayTime",
    };

    private static readonly Dictionary<int, string> LegacyKeyNames = BuildLegacyKeyNames();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static PersonalOptionsProfile LoadProfile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return new PersonalOptionsProfile();
        try
        {
            var profile = JsonSerializer.Deserialize<PersonalOptionsProfile>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                ?? throw new InvalidDataException("Personal options profile is empty.");
            if (profile.SchemaVersion != 1) throw new InvalidDataException($"Unsupported personal options schema {profile.SchemaVersion}.");
            if (profile.Values is null) throw new InvalidDataException("Personal options profile has no values map.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (rawKey, rawValue) in profile.Values)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || rawValue is null) continue;
                var key = CanonicalKey(rawKey);
                var value = CanonicalValue(key, rawValue);
                if (value is not null && CategoryOf(key) != PersonalOptionCategories.None) values[key] = value;
            }
            return new PersonalOptionsProfile { SchemaVersion = 1, Values = values };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Unable to read the personal options profile.", ex);
        }
    }

    public static void SaveProfile(string path, PersonalOptionsProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SchemaVersion != 1) throw new InvalidDataException($"Unsupported personal options schema {profile.SchemaVersion}.");
        if (profile.Values is null) throw new InvalidDataException("Personal options profile has no values map.");
        var normalized = new PersonalOptionsProfile
        {
            SchemaVersion = 1,
            Values = profile.Values
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                .Select(pair => (Key: CanonicalKey(pair.Key), Value: CanonicalValue(CanonicalKey(pair.Key), pair.Value)))
                .Where(static pair => pair.Value is not null && CategoryOf(pair.Key) != PersonalOptionCategories.None)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal),
        };
        var fullPath = Path.GetFullPath(path);
        using var profileLease = PersonalOptionsFileCoordinator.TryAcquireProfile(fullPath)
            ?? throw new IOException("The personal options profile is in use.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(normalized, JsonOptions), new UTF8Encoding(false));
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>Applies the shared profile before launch and returns the token the host should retain until process exit.</summary>
    public static PersonalOptionsSession PrepareSession(string optionsPath, string profilePath,
        PersonalOptionCategories categories, bool targetUsesModernKeyNames, bool fovIsDegrees = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        var fullOptionsPath = Path.GetFullPath(optionsPath);
        using var optionsLease = PersonalOptionsFileCoordinator.TryAcquire(fullOptionsPath)
            ?? throw new IOException("Personal options are owned by a running game session.");
        var fullProfilePath = Path.GetFullPath(profilePath);
        var profile = LoadProfile(fullProfilePath);
        var currentText = File.Exists(fullOptionsPath) ? ReadOptionsText(fullOptionsPath) : string.Empty;
        var merge = Apply(currentText, profile, categories, targetUsesModernKeyNames, fovIsDegrees);
        if (merge.AppliedKeys.Count > 0 && !string.Equals(currentText, merge.Text, StringComparison.Ordinal))
            WriteOptionsText(fullOptionsPath, merge.Text);
        return new PersonalOptionsSession(fullOptionsPath, fullProfilePath, categories, targetUsesModernKeyNames, fovIsDegrees);
    }

    /// <summary>Captures user changes from the stopped game process into the shared profile.</summary>
    public static void FinishSession(PersonalOptionsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var optionsLease = PersonalOptionsFileCoordinator.TryAcquire(session.OptionsPath)
            ?? throw new IOException("Personal options are owned by a running game session.");
        if (!File.Exists(session.OptionsPath)) return;
        var profile = LoadProfile(session.ProfilePath);
        var captured = Capture(ReadOptionsText(session.OptionsPath), profile, session.Categories);
        SaveProfile(session.ProfilePath, captured);
    }

    public static PersonalOptionsProfile Capture(string optionsText, PersonalOptionsProfile? existing,
        PersonalOptionCategories categories)
    {
        var values = existing is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(existing.Values, StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in Parse(optionsText))
        {
            var key = CanonicalKey(rawKey);
            if ((CategoryOf(key) & categories) == 0) continue;
            var canonical = CanonicalValue(key, rawValue);
            if (canonical is not null) values[key] = canonical;
        }
        return new PersonalOptionsProfile { Values = values };
    }

    public static PersonalOptionsMergeResult Apply(string currentText, PersonalOptionsProfile profile,
        PersonalOptionCategories categories, bool targetUsesModernKeyNames, bool fovIsDegrees = true)
    {
        ArgumentNullException.ThrowIfNull(currentText);
        ArgumentNullException.ThrowIfNull(profile);
        var hadBom = currentText.Length > 0 && currentText[0] == '\uFEFF';
        var text = hadBom ? currentText[1..] : currentText;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var finalNewline = text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal);
        var lines = string.IsNullOrEmpty(text)
            ? new List<string>()
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (finalNewline && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawValue) in profile.Values)
        {
            var key = CanonicalKey(rawKey);
            if ((CategoryOf(key) & categories) == 0) continue;
            var value = CanonicalValue(key, rawValue);
            if (value is null) continue;
            if (key == "fov" && !fovIsDegrees)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees)) continue;
                value = ((degrees - 70d) / 40d).ToString("0.########", CultureInfo.InvariantCulture);
            }
            else if (IsBinding(key))
            {
                var converted = ToVersionBinding(value, targetUsesModernKeyNames);
                if (converted is null) continue;
                value = converted;
            }
            replacements[key] = value;
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0) continue;
            var originalKey = lines[i][..separator];
            var canonicalKey = CanonicalKey(originalKey);
            if (!replacements.TryGetValue(canonicalKey, out var replacement)) continue;
            lines[i] = originalKey + ":" + replacement;
            applied.Add(canonicalKey);
        }

        foreach (var pair in replacements.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (applied.Contains(pair.Key)) continue;
            var outputKey = !targetUsesModernKeyNames && pair.Key == "key_key.swapOffhand" ? "key_key.swapHands" : pair.Key;
            lines.Add(outputKey + ":" + pair.Value);
            applied.Add(pair.Key);
        }

        var merged = string.Join(newline, lines);
        if (finalNewline) merged += newline;
        if (hadBom) merged = "\uFEFF" + merged;
        return new PersonalOptionsMergeResult(merged, applied.OrderBy(static key => key, StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            values[CanonicalKey(line[..separator])] = line[(separator + 1)..];
        }
        return values;
    }

    private static string ReadOptionsText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = new UTF8Encoding(false, true).GetString(bytes, hasBom ? Encoding.UTF8.Preamble.Length : 0,
            bytes.Length - (hasBom ? Encoding.UTF8.Preamble.Length : 0));
        return hasBom ? "\uFEFF" + text : text;
    }

    private static void WriteOptionsText(string path, string text)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var hasBom = text.StartsWith('\uFEFF');
        var content = hasBom ? text[1..] : text;
        var encoding = new UTF8Encoding(hasBom, true);
        var tempPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content, encoding);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static string CanonicalKey(string key) => key == "key_key.swapHands" ? "key_key.swapOffhand" : key;

    private static string? CanonicalValue(string key, string value)
    {
        if (value.Length > 4096 || value.IndexOfAny(['\0', '\n', '\r']) >= 0) return null;
        if (key == "fov")
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fov) || double.IsNaN(fov) || double.IsInfinity(fov)) return null;
            if (fov >= -1d && fov <= 1d) fov = 70d + fov * 40d;
            return fov is >= 30d and <= 110d ? fov.ToString("0.########", CultureInfo.InvariantCulture) : null;
        }
        if (!IsBinding(key)) return value;
        if (value.StartsWith("key.keyboard.", StringComparison.Ordinal) || value.StartsWith("key.mouse.", StringComparison.Ordinal)) return value;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacy)) return null;
        if (legacy is <= -1 and >= -100)
        {
            var button = legacy + 100;
            return "key.mouse." + (button == 0 ? "left" : button == 1 ? "right" : button == 2 ? "middle" : (button + 1).ToString(CultureInfo.InvariantCulture));
        }
        return LegacyKeyNames.TryGetValue(legacy, out var keyName) ? "key.keyboard." + keyName : null;
    }

    private static PersonalOptionCategories CategoryOf(string key)
    {
        if (VanillaBindings.Contains(key)) return PersonalOptionCategories.KeyBindings;
        if (MouseOptions.Contains(key)) return PersonalOptionCategories.Mouse;
        if (ViewOptions.Contains(key)) return PersonalOptionCategories.View;
        if (AccessibilityOptions.Contains(key)) return PersonalOptionCategories.Accessibility;
        if (key.StartsWith("soundCategory_", StringComparison.Ordinal)) return PersonalOptionCategories.Audio;
        return PersonalOptionCategories.None;
    }

    private static bool IsBinding(string key) => key.StartsWith("key_", StringComparison.Ordinal);

    private static string? ToVersionBinding(string canonical, bool modern)
    {
        if (modern) return canonical;
        if (!canonical.StartsWith("key.keyboard.", StringComparison.Ordinal) && !canonical.StartsWith("key.mouse.", StringComparison.Ordinal)) return canonical;
        if (canonical.StartsWith("key.mouse.", StringComparison.Ordinal))
        {
            var name = canonical[10..];
            return name switch
            {
                "left" => "-100",
                "right" => "-99",
                "middle" => "-98",
                _ when int.TryParse(name, out var number) && number >= 4 && number <= 100 => (number - 101).ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        var keyName = canonical[13..];
        var match = LegacyKeyNames.FirstOrDefault(pair => pair.Value == keyName);
        return match.Value is null ? null : match.Key.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<int, string> BuildLegacyKeyNames()
    {
        var keys = new Dictionary<int, string>
        {
            [0] = "unknown", [1] = "escape", [14] = "backspace", [15] = "tab", [28] = "enter",
            [29] = "left.control", [42] = "left.shift", [54] = "right.shift", [56] = "left.alt", [57] = "space",
            [58] = "caps.lock", [69] = "num.lock", [70] = "scroll.lock", [87] = "f11", [88] = "f12",
            [156] = "keypad.enter", [157] = "right.control", [181] = "keypad.divide", [183] = "print.screen",
            [184] = "right.alt", [197] = "pause", [199] = "home", [200] = "up", [201] = "page.up",
            [203] = "left", [205] = "right", [207] = "end", [208] = "down", [209] = "page.down",
            [210] = "insert", [211] = "delete", [219] = "left.win", [220] = "right.win", [221] = "menu",
            [12] = "minus", [13] = "equal", [26] = "left.bracket", [27] = "right.bracket", [43] = "backslash",
            [39] = "semicolon", [40] = "apostrophe", [41] = "grave.accent", [51] = "comma", [52] = "period",
            [53] = "slash", [55] = "keypad.multiply", [74] = "keypad.subtract", [78] = "keypad.add", [83] = "keypad.decimal",
        };
        for (var index = 0; index <= 9; index++) keys[2 + index] = index == 9 ? "0" : (index + 1).ToString(CultureInfo.InvariantCulture);
        foreach (var (start, row) in new[] { (16, "qwertyuiop"), (30, "asdfghjkl"), (44, "zxcvbnm") })
            for (var index = 0; index < row.Length; index++) keys[start + index] = row[index].ToString();
        for (var index = 1; index <= 10; index++) keys[58 + index] = "f" + index.ToString(CultureInfo.InvariantCulture);
        foreach (var (code, key) in new[] { (71, "7"), (72, "8"), (73, "9"), (75, "4"), (76, "5"), (77, "6"), (79, "1"), (80, "2"), (81, "3"), (82, "0") })
            keys[code] = "keypad." + key;
        return keys;
    }
}
