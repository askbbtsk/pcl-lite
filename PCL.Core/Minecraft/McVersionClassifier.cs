using System;
using System.Text.Json.Nodes;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public enum McVersionCategory
{
    Release,
    Snapshot,
    BeforeRelease
}

public static class McVersionClassifier
{
    public static string GetCategoryDisplayName(McVersionCategory category)
    {
        return category switch
        {
            McVersionCategory.Release => Lang.Text("Download.Version.Type.Release"),
            McVersionCategory.Snapshot => Lang.Text("Download.Version.Type.Development"),
            McVersionCategory.BeforeRelease => Lang.Text("Download.Version.Type.BeforeRelease"),
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
    }

    public static McVersionCategory? ClassifyVersion(JsonObject version)
    {
        var idLower = _GetString(version, "id").ToLowerInvariant();
        if (_IsRemovedSpecialVersion(idLower)) return null;

        var type = _GetString(version, "type");

        return type switch
        {
            "release" => McVersionCategory.Release,
            "snapshot" or "pending" => _ClassifySnapshotOrPending(version, idLower),
            "special" => null,
            _ => McVersionCategory.BeforeRelease
        };
    }

    private static bool _IsRemovedSpecialVersion(string id) => id is
        "2point0_blue" or "2point0_red" or "2point0_purple" or
        "2.0_blue" or "2.0_red" or "2.0_purple" or "2.0" or
        "20w14infinite" or "20w14∞" or "3d shareware v1.34" or "1.rv-pre1" or "15w14a" or
        "22w13oneblockatatime" or "23w13a_or_b" or "24w14potato" or "25w14craftmine" or "26w14a";

    public static DateTime GetReleaseTime(JsonObject version)
    {
        return _GetDateTime(version, "releaseTime");
    }

    private static McVersionCategory _ClassifySnapshotOrPending(JsonObject version, string idLower)
    {
        if (idLower.StartsWith("1.") &&
            !idLower.Contains("combat") &&
            !idLower.Contains("rc") &&
            !idLower.Contains("experimental") &&
            idLower != "1.2" &&
            !idLower.Contains("pre"))
        {
            version["type"] = "release";
            return McVersionCategory.Release;
        }

        return McVersionCategory.Snapshot;
    }

    private static DateTime _GetDateTime(JsonObject obj, string key)
    {
        return JsonCompat.TryGetDateTime(obj[key], out var dateTime)
            ? dateTime
            : DateTime.MinValue;
    }

    private static string _GetString(JsonObject obj, string key)
    {
        var node = obj[key];
        if (node is null) return "";

        return node is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : node.ToString();
    }
}
