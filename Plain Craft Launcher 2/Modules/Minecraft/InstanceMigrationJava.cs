using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace PCL;

/// <summary>Reselects for a fresh target; never carries the source instance's fixed Java preference.</summary>
internal static class InstanceMigrationJava
{
    internal static bool SelectForTarget(McInstance target)
    {
        var minimum = new Version(1, 8, 0, 0);
        var maximum = new Version(999, 999, 999, 999);
        var info = target.Info;
        if (info.Valid && info.vanilla >= new Version(20, 0, 5)) minimum = new Version(21, 0, 0, 0);
        else if (info.Valid && info.vanilla.Major >= 18) minimum = new Version(17, 0, 0, 0);
        else if (info.Valid && info.vanilla.Major >= 17) minimum = new Version(16, 0, 0, 0);
        if (info.HasForge)
        {
            if (info.vanilla >= new Version(6, 0, 1) && info.vanilla <= new Version(7, 0, 2))
            { minimum = new Version(1, 7, 0, 0); maximum = new Version(1, 7, 999, 999); }
            else if (info.vanilla.Major <= 12 || !info.Valid) maximum = new Version(1, 8, 999, 999);
            else if (info.vanilla.Major <= 14) maximum = new Version(1, 10, 999, 999);
            else if (info.vanilla.Major == 15) maximum = new Version(1, 15, 999, 999);
            else if (McVersionComparer.CompareVersionGe(info.Forge, "34.0.0") && McVersionComparer.CompareVersionGe("36.2.25", info.Forge))
                maximum = new Version(1, 8, 0, 321);
        }
        if (int.TryParse(target.JsonObject["javaVersion"]?["majorVersion"]?.ToString(), out var major) && major > 0)
        {
            var declared = major <= 8 ? new Version(1, major, 0, 0) : new Version(major, 0, 0, 0);
            if (declared > minimum) minimum = declared;
        }
        IReadOnlySet<int>? allowed = target.JsonObject["compatibleJavaMajors"] is JsonArray majors
            ? majors.Select(value => int.TryParse(value?.ToString(), out var parsed) ? parsed : 0).Where(value => value > 0).ToHashSet() : null;
        if (allowed is { Count: > 0 })
        {
            allowed = allowed.Where(value =>
            {
                var low = value <= 8 ? new Version(1, value, 0, 0) : new Version(value, 0, 0, 0);
                var high = value <= 8 ? new Version(1, value, 999, 999) : new Version(value, 999, 999, 999);
                return high >= minimum && low <= maximum;
            }).ToHashSet();
            if (allowed.Count == 0) return false;
        }
        else allowed = null;
        if (maximum < minimum) return false;
        lock (ModJava.javaLock)
            return ModJava.JavaSelect("$$", minimum, maximum, target, enforceVersionRange: true, compatibleMajors: allowed) is not null;
    }
}
