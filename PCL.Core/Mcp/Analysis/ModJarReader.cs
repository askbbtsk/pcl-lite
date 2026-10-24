using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCL.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp.Analysis;

/// <summary>
/// 从一个 jar 里读出来的元数据。不同加载器写的位置完全不一样，
/// 这里统一成一份结构，分析工具就只需要看这一份。
/// </summary>
public sealed record JarMetadata
{
    public string ModId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string Authors { get; init; } = "";

    /// <summary>Fabric / Quilt / Forge / NeoForge / LiteLoader / ResourcePack / Shader / Vanilla / Unknown</summary>
    public string Loader { get; init; } = "Unknown";

    /// <summary>声明的 Minecraft 版本或版本范围。</summary>
    public string McVersion { get; init; } = "";

    /// <summary>入口类或入口点摘要。</summary>
    public string Entry { get; init; } = "";

    /// <summary>依赖列表，形如 <c>fabric-api &gt;=0.90.0</c>。</summary>
    public IReadOnlyList<string> Depends { get; init; } = [];

    /// <summary>声明为不兼容的项。</summary>
    public IReadOnlyList<string> Breaks { get; init; } = [];

    /// <summary>元数据来自哪个文件，排查解析失败时用。</summary>
    public string Source { get; init; } = "";
}

/// <summary>
/// 扫描结果里的一条文件记录。
/// </summary>
public sealed record ModFileEntry
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required bool Enabled { get; init; }
    public long SizeBytes { get; init; }
    public DateTime Modified { get; init; }
    public JarMetadata? Metadata { get; init; }

    /// <summary>用于去重与冲突检测的标识：优先 modId，退化为文件名主干。</summary>
    public string Identity =>
        !string.IsNullOrWhiteSpace(Metadata?.ModId)
            ? Metadata!.ModId
            : Path.GetFileNameWithoutExtension(FileName).Replace(".disabled", "", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// jar / zip 元数据读取器。
/// <para>
/// 用 <see cref="FileShare.ReadWrite" /> 打开：游戏运行中 mod 文件是被占用的，
/// 用默认共享模式读会直接抛 IOException，导致「一边玩游戏一边让 AI 整理 mod」这个最常见的场景挂掉。
/// </para>
/// </summary>
public static class ModJarReader
{
    private static readonly string[] ModExtensions = [".jar", ".litemod", ".zip"];

    public static bool LooksLikeArchive(string path)
    {
        var name = path.Replace(".disabled", "", StringComparison.OrdinalIgnoreCase);
        return ModExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>读一个 jar 的元数据。读不出来时返回 <see langword="null" />，不抛异常。</summary>
    public static JarMetadata? Read(string jarPath)
    {
        try
        {
            using var stream = new FileStream(jarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            return ReadFabric(archive)
                   ?? ReadQuilt(archive)
                   ?? ReadForgeToml(archive, "META-INF/neoforge.mods.toml", "NeoForge")
                   ?? ReadForgeToml(archive, "META-INF/mods.toml", "Forge")
                   ?? ReadMcModInfo(archive)
                   ?? ReadLiteLoader(archive)
                   ?? ReadPackMeta(archive)
                   ?? ReadVersionJson(archive)
                   ?? ReadManifest(archive);
        }
        catch (Exception)
        {
            return null;
        }
    }

    #region 各加载器格式

    private static JarMetadata? ReadFabric(ZipArchive archive)
    {
        var text = ReadEntry(archive, "fabric.mod.json");
        if (text is null) return null;

        var root = TryParseJson(text) as JsonObject;
        if (root is null) return null;

        var depends = new List<string>();
        var breaks = new List<string>();
        CollectDependencyMap(root["depends"] as JsonObject, depends);
        CollectDependencyMap(root["breaks"] as JsonObject, breaks);

        var entryPoints = new List<string>();
        if (root["entrypoints"] is JsonObject entryRoot)
            foreach (var (key, value) in entryRoot)
                if (value is JsonArray arr)
                    foreach (var item in arr)
                        entryPoints.Add(item is JsonValue ? $"{key}: {item}" : $"{key}: {item?.ToJsonString() ?? ""}");

        return new JarMetadata
        {
            ModId = GetString(root["id"]),
            Name = GetString(root["name"]),
            Version = GetString(root["version"]),
            Description = GetString(root["description"]),
            Authors = JoinAuthors(root["authors"]),
            Loader = "Fabric",
            McVersion = ExtractMcVersion(depends),
            Entry = string.Join("; ", entryPoints.Take(4)),
            Depends = depends,
            Breaks = breaks,
            Source = "fabric.mod.json"
        };
    }

    private static JarMetadata? ReadQuilt(ZipArchive archive)
    {
        var text = ReadEntry(archive, "quilt.mod.json");
        if (text is null) return null;

        var root = TryParseJson(text) as JsonObject;
        var quiltLoader = root?["quilt_loader"] as JsonObject;
        if (quiltLoader is null) return null;

        var depends = new List<string>();
        if (quiltLoader["depends"] is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonObject dep)
                    depends.Add($"{GetString(dep["id"])} {GetString(dep["versions"])}".Trim());

        return new JarMetadata
        {
            ModId = GetString(quiltLoader["id"]),
            Name = GetString(quiltLoader["metadata"]?["name"]),
            Version = GetString(quiltLoader["version"]),
            Description = GetString(quiltLoader["metadata"]?["description"]),
            Authors = JoinAuthors(quiltLoader["metadata"]?["contributors"]),
            Loader = "Quilt",
            McVersion = ExtractMcVersion(depends),
            Depends = depends,
            Source = "quilt.mod.json"
        };
    }

    /// <summary>
    /// 解析 Forge / NeoForge 的 mods.toml。
    /// 项目里没有 TOML 库，这里只实现 mods.toml 实际用到的子集：
    /// <c>[[mods]]</c> 段、<c>[[dependencies.xxx]]</c> 段、<c>key="value"</c> 与三引号多行字符串。
    /// </summary>
    private static JarMetadata? ReadForgeToml(ZipArchive archive, string entryName, string loader)
    {
        var text = ReadEntry(archive, entryName);
        if (text is null) return null;

        string modId = "", name = "", version = "", description = "", authors = "";
        var depends = new List<string>();
        var section = "";
        var inMultiline = false;
        var multilineKey = "";
        var multilineBuffer = new StringBuilder();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();

            if (inMultiline)
            {
                if (line.Contains("\"\"\"", StringComparison.Ordinal))
                {
                    multilineBuffer.Append(' ').Append(line[..line.IndexOf("\"\"\"", StringComparison.Ordinal)]);
                    var value = multilineBuffer.ToString().Trim();
                    if (multilineKey == "description") description = value;
                    inMultiline = false;
                    multilineBuffer.Clear();
                }
                else
                {
                    multilineBuffer.Append(' ').Append(line);
                }

                continue;
            }

            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                section = line.Trim('[', ']');
                continue;
            }

            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']');
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var valueRaw = line[(eq + 1)..].Trim();

            if (valueRaw.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                var rest = valueRaw[3..];
                var endIndex = rest.IndexOf("\"\"\"", StringComparison.Ordinal);
                if (endIndex >= 0)
                {
                    Assign(key, rest[..endIndex].Trim());
                }
                else
                {
                    inMultiline = true;
                    multilineKey = key;
                    multilineBuffer.Clear().Append(rest);
                }

                continue;
            }

            Assign(key, Unquote(valueRaw));

            void Assign(string k, string v)
            {
                if (section == "mods")
                {
                    switch (k)
                    {
                        case "modId" when modId.Length == 0: modId = v; break;
                        case "displayName" when name.Length == 0: name = v; break;
                        case "version" when version.Length == 0: version = v; break;
                        case "description" when description.Length == 0: description = v; break;
                        case "authors" when authors.Length == 0: authors = v; break;
                    }
                }
                else if (section.StartsWith("dependencies", StringComparison.OrdinalIgnoreCase))
                {
                    if (k == "modId" && v.Length > 0) depends.Add($"{v} {GetPendingRange()}");
                }
            }

            string GetPendingRange()
            {
                // 依赖段里 versionRange 通常在 modId 之后一行，取最近一次出现的值
                var match = Regex.Match(text, @"versionRange\s*=\s*""([^""]*)""");
                return match.Success ? match.Groups[1].Value : "";
            }
        }

        if (modId.Length == 0 && name.Length == 0) return null;

        return new JarMetadata
        {
            ModId = modId,
            Name = name.Length > 0 ? name : modId,
            Version = version,
            Description = description,
            Authors = authors,
            Loader = loader,
            McVersion = ExtractMcVersion(depends),
            Depends = depends,
            Source = entryName
        };
    }

    private static JarMetadata? ReadMcModInfo(ZipArchive archive)
    {
        var text = ReadEntry(archive, "mcmod.info");
        if (text is null) return null;

        var node = TryParseJson(text);
        JsonObject? item = node switch
        {
            JsonArray array when array.Count > 0 => array[0] as JsonObject,
            JsonObject obj when obj["modList"] is JsonArray modList && modList.Count > 0 => modList[0] as JsonObject,
            JsonObject obj => obj,
            _ => null
        };
        if (item is null) return null;

        var mcVersion = GetString(item["mcversion"]);
        if (mcVersion.Length == 0) mcVersion = GetString(item["acceptedMinecraftVersions"]);

        return new JarMetadata
        {
            ModId = GetString(item["modid"]),
            Name = GetString(item["name"]),
            Version = GetString(item["version"]),
            Description = GetString(item["description"]),
            Authors = JoinAuthors(item["authorList"]) is { Length: > 0 } list ? list : GetString(item["author"]),
            Loader = "Forge",
            McVersion = mcVersion,
            Entry = GetString(item["entrypoint"]),
            Source = "mcmod.info"
        };
    }

    private static JarMetadata? ReadLiteLoader(ZipArchive archive)
    {
        var text = ReadEntry(archive, "litemod.json");
        if (text is null) return null;
        if (TryParseJson(text) is not JsonObject root) return null;

        return new JarMetadata
        {
            ModId = GetString(root["name"]),
            Name = GetString(root["name"]),
            Version = GetString(root["version"]),
            Description = GetString(root["description"]),
            Authors = GetString(root["author"]),
            Loader = "LiteLoader",
            McVersion = GetString(root["mcversion"]),
            Source = "litemod.json"
        };
    }

    private static JarMetadata? ReadPackMeta(ZipArchive archive)
    {
        var text = ReadEntry(archive, "pack.mcmeta");
        if (text is null) return null;
        if (TryParseJson(text) is not JsonObject root) return null;

        var pack = root["pack"] as JsonObject;
        var description = GetString(pack?["description"]);
        var isShader = archive.GetEntry("shaders/") is not null
                       || archive.Entries.Any(e => e.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase));

        return new JarMetadata
        {
            Name = Path.GetFileNameWithoutExtension(archive.Entries.FirstOrDefault()?.FullName ?? ""),
            Description = description,
            Loader = isShader ? "Shader" : "ResourcePack",
            Source = "pack.mcmeta"
        };
    }

    private static JarMetadata? ReadVersionJson(ZipArchive archive)
    {
        var text = ReadEntry(archive, "version.json");
        if (text is null) return null;
        if (TryParseJson(text) is not JsonObject root) return null;

        return new JarMetadata
        {
            Name = GetString(root["name"]),
            Version = GetString(root["id"]),
            Loader = "Vanilla",
            McVersion = GetString(root["id"]),
            Source = "version.json"
        };
    }

    private static JarMetadata? ReadManifest(ZipArchive archive)
    {
        var text = ReadEntry(archive, "META-INF/MANIFEST.MF");
        if (text is null) return null;

        string? Field(string key)
        {
            var match = Regex.Match(text, $@"^{Regex.Escape(key)}\s*:\s*(.+)$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        var title = Field("Implementation-Title") ?? Field("Specification-Title");
        var version = Field("Implementation-Version") ?? Field("Specification-Version");
        var mainClass = Field("Main-Class");

        if (title is null && mainClass is null) return null;

        return new JarMetadata
        {
            Name = title ?? "",
            Version = version ?? "",
            Loader = "Unknown",
            Entry = mainClass ?? "",
            Source = "META-INF/MANIFEST.MF"
        };
    }

    #endregion

    #region 工具方法

    private static string? ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return text.Length > 64 * 1024 ? text[..(64 * 1024)] : text;
    }

    private static JsonNode? TryParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text, JsonCompat.NodeOptions, JsonCompat.DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetString(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s ?? "";
            return value.ToJsonString().Trim('"');
        }

        return node.ToJsonString();
    }

    private static string JoinAuthors(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue) return GetString(node);
        if (node is JsonArray array)
            return string.Join(", ", array.Select(item => item is JsonObject obj
                ? GetString(obj["name"]) is { Length: > 0 } n ? n : GetString(obj["id"])
                : GetString(item)).Where(s => s.Length > 0));
        if (node is JsonObject obj)
            return string.Join(", ", obj.Select(kv => $"{kv.Key}: {GetString(kv.Value)}"));
        return "";
    }

    /// <summary>
    /// 从依赖列表里挑出 minecraft 那一条并去掉前缀，得到干净的版本范围。
    /// 依赖列表存的是 <c>minecraft ~1.21</c> 这种整串，直接当版本用会把显示和比较都带偏。
    /// </summary>
    private static string ExtractMcVersion(IEnumerable<string> depends)
    {
        var entry = depends.FirstOrDefault(d => d.StartsWith("minecraft", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return "";
        var index = entry.IndexOf(' ');
        return index > 0 ? entry[(index + 1)..].Trim() : "";
    }

    private static void CollectDependencyMap(JsonObject? map, List<string> target)
    {
        if (map is null) return;
        foreach (var (key, value) in map)
        {
            var range = GetString(value);
            target.Add(range.Length > 0 && range != "*" ? $"{key} {range}" : key);
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];
        return value;
    }

    #endregion
}
