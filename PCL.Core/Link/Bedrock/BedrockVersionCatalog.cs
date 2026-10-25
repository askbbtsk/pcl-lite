using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Bedrock;

/// <summary>安装包形态。</summary>
public enum BedrockPackageKind
{
    /// <summary>微软商店包（UWP / MSIX），解包后向系统注册，通过 <c>shell:appsFolder</c> 启动。</summary>
    Uwp,

    /// <summary>GDK 包，下发的是加密的 <c>.msixvc</c>，解包到独立目录后直接运行主程序。</summary>
    Gdk
}

/// <summary>版本列表里的一条。</summary>
/// <param name="Key">列表键，形如 <c>1.21.120.20</c>，可能带括号后缀，不能直接当目录名。</param>
/// <param name="Version">版本号，取自 <c>ID</c> 字段，形如 <c>1.21.12020</c>。</param>
/// <param name="Channel">发布通道：Release / Preview / Beta。</param>
/// <param name="Kind">安装包形态。</param>
/// <param name="Date">发布日期。</param>
/// <param name="Arch">架构，优先取 x64。</param>
/// <param name="Meta">
/// 下载线索。UWP 是 Store 的 UpdateID（GUID），要用 FE3 接口换成真实地址；
/// GDK 直接就是 <c>.msixvc</c> 的地址。
/// </param>
/// <param name="Md5">官方 MD5，用于下载后校验。</param>
public sealed record BedrockVersionEntry(
    string Key,
    string Version,
    string Channel,
    BedrockPackageKind Kind,
    string Date,
    string Arch,
    string Meta,
    string Md5)
{
    /// <summary>GDK 条目直接带地址，UWP 条目需要再解析一次。</summary>
    public bool HasDirectUrl => Meta.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>给人看的标签，例如 <c>1.21.120.20 · 正式版 · UWP</c>。</summary>
    public string DisplayName => $"{Key} · {Channel} · {(Kind == BedrockPackageKind.Uwp ? "UWP" : "GDK")}";
}

/// <summary>
///     基岩版版本列表。
///     <para>
///         数据源 <c>data.mcappx.com</c> 是社区维护的商店版本索引（mcappx 站点同源），
///         上游 PCL-Symbio-Edition 用的也是这个。它只提供「有哪些版本、去哪下」，
///         不提供任何破解内容：UWP 包是微软商店 CDN 上的原包，GDK 包是 Xbox CDN 上的原包。
///     </para>
/// </summary>
public static class BedrockVersionCatalog
{
    public const string SourceUrl = "https://data.mcappx.com/v2/bedrock.json";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>拉取并解析版本列表。失败时抛异常，由调用方决定是提示还是用缓存。</summary>
    public static async Task<IReadOnlyList<BedrockVersionEntry>> FetchAsync(CancellationToken token = default)
    {
        using var response = await Client.GetAsync(SourceUrl, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return Parse(text);
    }

    /// <summary>把原始 JSON 解析成条目列表。抽出来是为了能单测，不必联网。</summary>
    public static IReadOnlyList<BedrockVersionEntry> Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new FormatException("版本列表不是 JSON 对象。");

        // 外层包了 CreationTime / From_mcappx.com 两个键，真正的数据在内层
        var table = root["From_mcappx.com"] as JsonObject
                    ?? throw new FormatException("版本列表里找不到 From_mcappx.com 节点。");

        var result = new List<BedrockVersionEntry>(table.Count);
        foreach (var (key, node) in table)
        {
            if (node is not JsonObject item) continue;

            var variations = item["Variations"] as JsonArray;
            if (variations is not { Count: > 0 }) continue;

            // 一个版本可能同时有 x64 / x86 / arm64，桌面端只关心 x64
            var variation = variations.OfType<JsonObject>()
                                .FirstOrDefault(v => string.Equals(v["Arch"]?.GetValue<string>(), "x64",
                                    StringComparison.OrdinalIgnoreCase))
                            ?? variations[0] as JsonObject;
            if (variation is null) continue;

            var metadata = variation["MetaData"] as JsonArray;
            // 同一个版本可能有多条元数据（增量更新），取最后一条：上游 BedrockLauncher.Core 也是这个约定
            var meta = metadata is { Count: > 0 }
                ? metadata[^1]?.GetValue<string>() ?? ""
                : "";
            if (meta.Length == 0) continue;

            var kind = string.Equals(item["BuildType"]?.GetValue<string>(), "GDK", StringComparison.OrdinalIgnoreCase)
                ? BedrockPackageKind.Gdk
                : BedrockPackageKind.Uwp;

            result.Add(new BedrockVersionEntry(
                key,
                item["ID"]?.GetValue<string>() ?? key,
                item["Type"]?.GetValue<string>() ?? "Release",
                kind,
                item["Date"]?.GetValue<string>() ?? "",
                variation["Arch"]?.GetValue<string>() ?? "x64",
                meta,
                variation["MD5"]?.GetValue<string>() ?? ""));
        }

        return result
            .OrderByDescending(e => SortKey(e.Key))
            .ThenByDescending(e => e.Date, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     把 <c>1.21.120.20</c> 这类键拆成可以直接字符串比较的定宽序列；括号后缀（如 <c>72(01)</c>）忽略。
    ///     <para>
    ///         返回 <c>000001.000021.000120.000020</c> 这种形式：每段补到 6 位，
    ///         这样降序排序就等于按数字大小排，且不必给 <see cref="Enumerable.OrderByDescending{TSource,TKey}(IEnumerable{TSource},Func{TSource,TKey})" />
    ///         传自定义比较器（<see cref="List{T}" /> 本身不实现 <see cref="IComparable" />）。
    ///     </para>
    /// </summary>
    private static string SortKey(string key)
    {
        var numbers = new List<int>(4);
        var buffer = 0;
        var inNumber = false;
        foreach (var ch in key)
        {
            if (char.IsDigit(ch))
            {
                buffer = buffer * 10 + (ch - '0');
                inNumber = true;
            }
            else if (inNumber)
            {
                numbers.Add(buffer);
                buffer = 0;
                inNumber = false;
                if (numbers.Count == 4) break;
            }
        }

        if (inNumber && numbers.Count < 4) numbers.Add(buffer);
        while (numbers.Count < 4) numbers.Add(0);
        return string.Join('.', numbers.Select(n => n.ToString("D6")));
    }
}
