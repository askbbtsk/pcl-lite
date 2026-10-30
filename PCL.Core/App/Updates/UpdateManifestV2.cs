using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace PCL.Core.App.Updates;

/// <summary>
/// 清单里的一个更新源。
/// </summary>
/// <param name="Owner">仓库所有者。</param>
/// <param name="Repo">仓库名。</param>
/// <param name="Weight">
/// 权重。数字越大越优先。<b>只要这个源没失败，就用它</b>，哪怕别家报了个更高的版本号——
/// 权重是发布者亲手写在清单里的，版本号是远端仓库说了算的。
/// 只有权重打平（同一档的几个镜像）时才比版本号，谁的包新用谁的。
/// </param>
/// <param name="Tier">等级名（official / mirror / backup / community），仅用于日志与默认权重推导。</param>
public sealed record UpdateSourceEntry(string Owner, string Repo, int Weight, string Tier)
{
    /// <summary>回显用的 <c>owner/repo</c>。</summary>
    public string Pointer => $"{Owner}/{Repo}";

    public override string ToString() => $"{Pointer}({Tier}, {Weight})";
}

/// <summary>
/// 解析结果：能用的源 + 被跳过的条目（用于日志，不阻断整份清单）。
/// </summary>
public sealed class UpdateManifestV2Result
{
    public required IReadOnlyList<UpdateSourceEntry> Sources { get; init; }

    /// <summary>被跳过的条目说明。一条手误不该拖垮整份清单，但要留下痕迹。</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>
/// <c>updatev2.json</c>（多源清单）的解析与校验。
/// <para>
/// 为什么要 v2：v1 的 <c>update.json</c> 只有一行仓库指针，只表达得了「去问这一个仓库」。
/// 一旦那个仓库整体出事（组织被标记、账号被暂停、Release 被删），客户端没有任何退路。
/// v2 允许一次列出多个仓库，每个带权重，客户端挨个问、挑版本最新的那个。
/// </para>
/// <para>
/// 兼容性硬要求：<b>老客户端只认 <c>update.json</c>，那份文件必须原样留着</b>。
/// 所以这里读的是另一个文件 <c>updatev2.json</c>，新客户端先试 v2，拿不到（404 / 写坏 / 超时）
/// 再退回 v1 的单指针。两条路都断才算这次检查更新失败。
/// </para>
/// <para>
/// 解析风格刻意和 <see cref="UpdateManifestPointer"/> 一致：宽容写法、严格形状校验。
/// 手改 json 时多写一对括号、把源写成裸字符串、顶层直接给数组，都能认。
/// </para>
/// </summary>
public static class UpdateManifestV2
{
    /// <summary>官方源。权重最高。</summary>
    public const string TierOfficial = "official";

    /// <summary>镜像源（别人转发/同步的同一个项目）。</summary>
    public const string TierMirror = "mirror";

    /// <summary>备用源（换了个账号托管的副本）。</summary>
    public const string TierBackup = "backup";

    /// <summary>社区源（第三方维护的构建，可能落后）。</summary>
    public const string TierCommunity = "community";

    /// <summary>没写等级时的默认权重。介于 mirror 与 backup 之间。</summary>
    public const int DefaultWeight = 50;

    /// <summary>源数量上限。防手滑粘一大坨进来，每一条都要发一次请求。</summary>
    private const int MaxSources = 16;

    /// <summary>报错时把原文截断到多少字符。</summary>
    private const int PreviewLength = 60;

    /// <summary>顶层放源列表的键名，按顺序找。</summary>
    private static readonly string[] ListKeys =
        ["sources", "repositories", "repos", "mirrors", "servers", "nodes"];

    /// <summary>单个源对象里表示「仓库地址」的键名。</summary>
    private static readonly string[] PointerKeys =
        ["repo", "repository", "update", "target", "url", "latest", "pointer"];

    /// <summary>单个源对象里表示「等级」的键名。</summary>
    private static readonly string[] TierKeys = ["tier", "level", "kind", "role", "class"];

    /// <summary>单个源对象里表示「权重」的键名。</summary>
    private static readonly string[] WeightKeys = ["weight", "priority", "score", "rank"];

    /// <summary>
    /// 等级 → 默认权重。
    /// <para>
    /// official 必须是最高的：它是本项目自己发的包，别的源都只是「官方源暂时问不到时的替补」。
    /// </para>
    /// </summary>
    public static int WeightOfTier(string? tier) => (tier ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        TierOfficial or "primary" or "main" or "官方" => 100,
        TierMirror or "cdn" or "镜像" => 70,
        TierBackup or "fallback" or "备用" => 40,
        TierCommunity or "thirdparty" or "第三方" => 25,
        _ => DefaultWeight
    };

    /// <summary>
    /// 从清单原文解析出所有可用的源。
    /// </summary>
    /// <exception cref="FormatException">整份清单不可用（空、是网页、不是 JSON、一个合法源都没有）。</exception>
    public static UpdateManifestV2Result Parse(string? body)
    {
        var text = (body ?? string.Empty).Trim().TrimStart('\uFEFF').Trim();
        if (text.Length == 0)
            throw new FormatException("多源清单是空的。");

        // 反代 / 镜像出错时经常回一页 HTML，直接点出来，别让 JSON 解析器背锅
        if (text[0] is '<')
            throw new FormatException($"返回的是网页而不是多源清单（{_Preview(text)}），多半是通道被墙或仓库不可见。");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            throw new FormatException($"多源清单不是合法的 JSON（{_Preview(text)}）。", ex);
        }

        var problems = new List<string>();
        var rawEntries = _CollectEntries(root, problems);

        var sources = new List<UpdateSourceEntry>();
        foreach (var raw in rawEntries)
        {
            if (sources.Count >= MaxSources)
            {
                problems.Add($"源数量超过 {MaxSources} 个，后面的已忽略。");
                break;
            }

            var entry = _ToEntry(raw, problems);
            if (entry is null) continue;

            // 同一个仓库写两遍没有意义，权重取高的那个
            var existing = sources.FindIndex(x =>
                x.Owner.Equals(entry.Owner, StringComparison.OrdinalIgnoreCase) &&
                x.Repo.Equals(entry.Repo, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                if (sources[existing].Weight < entry.Weight) sources[existing] = entry;
                continue;
            }

            sources.Add(entry);
        }

        if (sources.Count == 0)
            throw new FormatException(
                $"多源清单里没有一个可用的仓库地址（{_Preview(text)}）。" +
                (problems.Count > 0 ? "问题：" + string.Join("；", problems) : string.Empty));

        // 按权重降序：调用方按这个顺序问，先问最该问的
        return new UpdateManifestV2Result
        {
            Sources = sources
                .OrderByDescending(x => x.Weight)
                .ThenBy(x => x.Pointer, StringComparer.Ordinal)
                .ToList(),
            Problems = problems
        };
    }

    /// <summary>
    /// 把顶层结构摊平成「一堆待解析的条目」。
    /// 支持：顶层数组、<c>{sources:[...]}</c>、以及 v1 那种单指针对象（当成一个 official 源）。
    /// </summary>
    private static List<JsonNode?> _CollectEntries(JsonNode? root, List<string> problems)
    {
        var list = new List<JsonNode?>();

        switch (root)
        {
            case JsonArray array:
            {
                list.AddRange(array);
                return list;
            }
            case JsonObject obj:
            {
                foreach (var key in ListKeys)
                {
                    if (obj[key] is not JsonArray array) continue;
                    list.AddRange(array);
                    return list;
                }

                // 没有源列表：可能整份就是 v1 的单指针写法，直接当官方源用。
                // 这样 updatev2.json 里写老格式也照样能跑，改一半不会把更新搞挂。
                string single;
                try
                {
                    single = UpdateManifestPointer.Extract(obj.ToJsonString());
                }
                catch (FormatException ex)
                {
                    throw new FormatException($"多源清单里既没有源列表，也不是单指针写法：{ex.Message}", ex);
                }

                list.Add(JsonValue.Create(single));
                return list;
            }
            case JsonValue value when value.TryGetValue<string>(out var bare):
            {
                // 纯字符串：一个源
                list.Add(JsonValue.Create(bare));
                return list;
            }
            default:
                problems.Add("清单顶层既不是数组也不是对象。");
                return list;
        }
    }

    /// <summary>把一个条目（字符串或对象）解析成源。解析不出来返回 null 并记一条问题。</summary>
    private static UpdateSourceEntry? _ToEntry(JsonNode? node, List<string> problems)
    {
        string? pointer;
        string? tier = null;
        int? explicitWeight = null;

        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
            {
                pointer = text;
                break;
            }
            case JsonObject obj:
            {
                pointer = _FirstString(obj, PointerKeys);

                // 对象里没写 pointer，但可能写成 {"name": ..., "repo": ...} 之类，兜底找唯一字符串
                if (string.IsNullOrWhiteSpace(pointer))
                {
                    var strings = obj
                        .Where(pair => pair.Value is JsonValue v && v.TryGetValue<string>(out _))
                        .Select(pair => pair.Value!.GetValue<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                    if (strings.Count == 1) pointer = strings[0];
                }

                tier = _FirstString(obj, TierKeys);
                explicitWeight = _FirstInt(obj, WeightKeys);
                break;
            }
            default:
                problems.Add($"无法识别的条目（{node?.GetType().Name ?? "null"}），已跳过。");
                return null;
        }

        if (string.IsNullOrWhiteSpace(pointer))
        {
            problems.Add("有条目没有写仓库地址，已跳过。");
            return null;
        }

        // 形状校验跟 v1 一样严：被墙时反代常回一页 HTML 或「404: Not Found」，
        // 不挡住的话日志里会变成「仓库地址看不懂」，把真正的原因（通道挂了）掩盖掉
        if (!UpdateManifestPointer.LooksLikePointer(pointer))
        {
            problems.Add($"「{_Preview(pointer)}」不像仓库地址，已跳过。");
            return null;
        }

        string owner, repo;
        try
        {
            (owner, repo) = UpdateManifestPointer.Parse(pointer);
        }
        catch (FormatException ex)
        {
            problems.Add($"「{_Preview(pointer)}」解析失败：{ex.Message}");
            return null;
        }

        // 没写等级就当镜像源：权重落在中间，既不抢官方源的位置，也不至于排到备用源后面
        var normalizedTier = string.IsNullOrWhiteSpace(tier) ? TierMirror : tier.Trim().ToLowerInvariant();

        return new UpdateSourceEntry(owner, repo, explicitWeight ?? WeightOfTier(normalizedTier), normalizedTier);
    }

    private static string? _FirstString(JsonObject obj, IEnumerable<string> keys)
    {
        foreach (var key in keys)
            if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s.Trim();
        return null;
    }

    private static int? _FirstInt(JsonObject obj, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (obj[key] is not JsonValue v) continue;

            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);

            // 有人会把权重写成字符串 "100"，也认
            if (v.TryGetValue<string>(out var s) &&
                int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    /// <summary>日志 / 异常里只放一小段原文，够定位就行。</summary>
    private static string _Preview(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= PreviewLength ? flat : flat[..PreviewLength] + "…";
    }
}
