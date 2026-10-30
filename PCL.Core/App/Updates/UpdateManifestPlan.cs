using System;
using System.Collections.Generic;
using System.Linq;
using PCL.Core.Utils;

namespace PCL.Core.App.Updates;

/// <summary>
/// 本次检查更新最终采用哪份清单、包含哪些源。
/// </summary>
/// <param name="Sources">按权重降序排好的源。</param>
/// <param name="UsedV2">是否用上了多源清单。false 表示退回了 v1 单指针。</param>
/// <param name="V2Failure">退回 v1 时 v2 失败的原因，用于日志；用上 v2 时为 null。</param>
/// <param name="Problems">解析过程中被跳过的条目说明。</param>
public sealed record UpdateManifestPlan(
    IReadOnlyList<UpdateSourceEntry> Sources,
    bool UsedV2,
    string? V2Failure,
    IReadOnlyList<string> Problems);

/// <summary>
/// 决定「这次用哪份清单」。
/// <para>
/// 顺序是 <c>updatev2.json</c> → <c>update.json</c>。抽成纯函数是为了能被单元测试盯住——
/// 这条回退链是整个更新功能最不能出错的一段：v2 文件没推上去、被反代回了一页 HTML、
/// 或者手改 json 改坏了，任何一种都必须<b>静默退化成单源</b>，而不是让用户看到「检查更新失败」。
/// </para>
/// </summary>
public static class UpdateManifestPlanner
{
    /// <summary>
    /// 根据两份清单原文（哪份没拉到就传 null）决定最终用哪份。
    /// </summary>
    /// <exception cref="FormatException">两份都用不了。</exception>
    public static UpdateManifestPlan Build(string? v2Body, string? v1Body)
    {
        string v2Failure;

        // 1) 多源清单
        if (string.IsNullOrWhiteSpace(v2Body))
        {
            v2Failure = "多源清单没拉到";
        }
        else
        {
            try
            {
                var parsed = UpdateManifestV2.Parse(v2Body);
                return new UpdateManifestPlan(parsed.Sources, true, null, parsed.Problems);
            }
            catch (FormatException ex)
            {
                v2Failure = ex.Message;
            }
        }

        // 2) v1 单指针兜底
        if (string.IsNullOrWhiteSpace(v1Body))
            throw new FormatException($"两份清单都不可用：多源清单（{v2Failure}）；单指针清单也没拉到。");

        string pointer;
        try
        {
            pointer = UpdateManifestPointer.Extract(v1Body);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"两份清单都不可用：多源清单（{v2Failure}）；单指针清单（{ex.Message}）");
        }

        var (owner, repo) = UpdateManifestPointer.Parse(pointer);
        var tier = UpdateManifestV2.TierOfficial;

        return new UpdateManifestPlan(
            [new UpdateSourceEntry(owner, repo, UpdateManifestV2.WeightOfTier(tier), tier)],
            false,
            v2Failure,
            []);
    }
}

/// <summary>
/// 多个源都报了版本时，同一权重档内挑出该给用户的那一个。
/// <para>
/// 规则是<b>权重优先</b>：调用方先把源按权重分档、从最高档开始问，
/// 最高档只要有一个源把版本报回来就当场定下来，低权重档根本不问。
/// 所以这个函数实际只需要处理「同一档内部的平手」——权重相等时才比版本号，
/// 同一档本来就该是「同一个项目的几个镜像」，谁的包新就用谁的。
/// </para>
/// <para>
/// 为什么不让低权重的高版本号赢：那样的话，清单里只要混进一个别的项目
/// （手滑粘错仓库，或者某个镜像仓库被接管后发了个大版本号的假包），
/// 它会因为版本号更高直接赢，客户端就去下载并执行别人的 exe 了。
/// 权重是发布者亲手写的，版本号是远端仓库说了算的，该信前者。
/// </para>
/// <para>
/// 「这个仓库到底是不是本项目的」由 <c>update.json</c> 身份校验负责
/// （见 <see cref="UpdateSourceSelector"/> 的调用方），不靠版本号猜。
/// </para>
/// </summary>
public static class UpdateSourceSelector
{
    /// <summary>一个候选源报上来的版本。</summary>
    /// <param name="VersionName">版本号原文，需要是合法 SemVer。</param>
    /// <param name="Weight">源权重。</param>
    /// <param name="Index">调用方自己的下标，用来把结果映射回去。</param>
    public sealed record Candidate(string VersionName, int Weight, int Index);

    /// <summary>
    /// 按「权重降序 → 版本降序」挑出最该用的那个候选，返回它的 <see cref="Candidate.Index"/>；
    /// 没有可用候选时返回 -1。
    /// <para>
    /// 返回的是<b>排好序之后的第一名</b>，调用方按需从第一名开始往后试
    /// （某个源身份校验不过就顺延到下一个）。
    /// </para>
    /// </summary>
    public static int PickBestIndex(IEnumerable<Candidate> candidates)
    {
        var ordered = OrderByPreference(candidates);
        return ordered.Count == 0 ? -1 : ordered[0];
    }

    /// <summary>
    /// 按「权重降序 → 版本降序」把所有候选排好，返回下标序列。
    /// <para>
    /// 调用方要「第一个不可用就试下一个」时用这个，比反复调
    /// <see cref="PickBestIndex"/> 再手动剔除要清楚。
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> OrderByPreference(IEnumerable<Candidate> candidates)
    {
        var parsed = new List<(SemVer Version, int Weight, int Index)>();

        foreach (var candidate in candidates)
        {
            // 版本号解析不了的直接丢：拿它比大小没有意义，留着还可能排到最前面
            if (!SemVer.TryParse(candidate.VersionName, out var version) || version is null) continue;
            parsed.Add((version, candidate.Weight, candidate.Index));
        }

        return parsed
            .OrderByDescending(x => x.Weight)
            .ThenByDescending(x => x.Version)
            .ThenBy(x => x.Index)
            .Select(x => x.Index)
            .ToList();
    }

    /// <summary>一个权重档。</summary>
    /// <param name="Weight">这一档的权重。</param>
    /// <param name="Indices">这一档里的源下标，保持调用方给的顺序。</param>
    public sealed record WeightTier(int Weight, IReadOnlyList<int> Indices);

    /// <summary>
    /// 把源按权重分档，<b>从高到低</b>。
    /// <para>
    /// 调用方按这个结果逐档询问：<b>高权重档只要有一个源回来了，就当场定下来，
    /// 低权重档连请求都不发</b>。低权重不是「并列候选」，是「高权重全挂了才轮到」的替补。
    /// 分档逻辑单独抽出来，是因为这条规则写反了不会报错、只会静默地用错源，
    /// 必须有测试盯着。
    /// </para>
    /// </summary>
    /// <param name="sources">每个源的权重与调用方自己的下标。</param>
    public static IReadOnlyList<WeightTier> GroupByWeight(IEnumerable<(int Weight, int Index)> sources)
    {
        return sources
            .GroupBy(x => x.Weight)
            .OrderByDescending(g => g.Key)
            .Select(g => new WeightTier(g.Key, g.Select(x => x.Index).ToList()))
            .ToList();
    }
}
