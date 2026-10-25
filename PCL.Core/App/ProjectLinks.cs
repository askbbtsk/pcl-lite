using System;
using System.Collections.Generic;

namespace PCL.Core.App;

/// <summary>
/// 本项目对外的链接集中地。
/// <para>
/// 从上游 fork 之后，代码里散落着大量指向 PCL-CE 的地址，改起来容易漏。
/// 需要改仓库时只动这里的 <see cref="GitHubOwner"/> 和 <see cref="GitHubRepo"/> 两个常量即可，
/// 其余地址都由它们拼出来。
/// </para>
/// </summary>
public static class ProjectLinks
{
    // ─────────────────────────────────────────────────────────────
    //  改成你自己的仓库，这两行是唯一需要动的地方
    // ─────────────────────────────────────────────────────────────

    /// <summary>GitHub 用户名或组织名。</summary>
    public const string GitHubOwner = "Pcl-Lite";

    /// <summary>仓库名。</summary>
    public const string GitHubRepo = "Pcl-lite";

    // ─────────────────────────────────────────────────────────────
    //  更新清单仓库（迁移锚点）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 更新清单所在的组织。与 <see cref="GitHubOwner"/> 刻意分开：这份清单的作用
    /// 就是在仓库搬家之后还能把老客户端指到新地址，所以它必须留在原地不动。
    /// <para>
    /// 2026-10 从 <c>Pcl-Lite</c> 组织迁到这里：那个组织被 GitHub 标记隐藏，
    /// 组织下所有仓库对匿名访问全部 404，清单和 Release 一起失联。
    /// 个人账号下的仓库不会因为「组织被标记」而整片消失。
    /// </para>
    /// </summary>
    public const string ServiceOwner = "bedkillerspacex-boop";

    /// <summary>更新清单所在的仓库。</summary>
    public const string ServiceRepo = "Pcl-Lite-service";

    // ─────────────────────────────────────────────────────────────
    //  以下由上面两个常量推导，一般不用改
    // ─────────────────────────────────────────────────────────────

    /// <summary>仓库是否已经配置好（占位符没被替换掉就视为未配置）。</summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(GitHubOwner) &&
        !GitHubOwner.Equals("YOUR_GITHUB_NAME", StringComparison.OrdinalIgnoreCase);

    public static string Repository => $"https://github.com/{GitHubOwner}/{GitHubRepo}";

    /// <summary>仓库所有者的主页。</summary>
    public static string OwnerHome => $"https://github.com/{GitHubOwner}";

    public static string Releases => $"{Repository}/releases";

    public static string LatestRelease => $"{Releases}/latest";

    public static string Issues => $"{Repository}/issues";

    public static string PullRequests => $"{Repository}/pulls";

    public static string NewIssue => $"{Issues}/new/choose";

    public static string Contributors => $"{Repository}/graphs/contributors";

    /// <summary>仓库的简体中文 README（项目说明、支持平台与许可条款都在这里）。</summary>
    public static string Readme => $"{Repository}/blob/main/README.md";

    public static string Wiki => $"{Repository}/wiki";

    /// <summary>GitHub API：仓库的 release 列表。</summary>
    public static string ReleasesApi => $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases";

    /// <summary>GitHub API：仓库的 issue 列表。</summary>
    public static string IssuesApi => $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/issues";

    /// <summary>GitHub API：贡献者列表。</summary>
    public static string ContributorsApi => $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/contributors";

    // ─────────────────────────────────────────────────────────────
    //  更新清单：唯一入口，发布仓库换了也只改这里指向的 json
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 更新清单的原始地址（raw.githubusercontent，走 CDN，不吃 API 限流）。
    /// <para>
    /// 老客户端把这个地址硬编码在心里，所以它<b>永远不能变</b>。
    /// 以后要换发布仓库、换组织、换下载通道，都只改这份 json 里的内容，
    /// 老版本依旧能顺着它找到新版本，不会因为仓库搬家而失联。
    /// </para>
    /// </summary>
    public static string UpdateManifest =>
        $"https://raw.githubusercontent.com/{ServiceOwner}/{ServiceRepo}/main/update.json";

    /// <summary>更新清单的镜像地址（jsDelivr）。raw.githubusercontent 在国内经常连不上。</summary>
    public static string UpdateManifestMirror =>
        $"https://cdn.jsdelivr.net/gh/{ServiceOwner}/{ServiceRepo}@main/update.json";

    /// <summary>
    /// 更新清单的第三条通道：GitHub Contents API。
    /// <para>
    /// raw 与 jsDelivr 都挂的时候靠它——api.github.com 的可达性通常比 raw 好得多，
    /// 代价是吃每小时 60 次的匿名限流额度。只读一个小文件，够用。
    /// </para>
    /// </summary>
    public static string UpdateManifestApi =>
        $"https://api.github.com/repos/{ServiceOwner}/{ServiceRepo}/contents/update.json";

    /// <summary>更新清单仓库的网页地址，用于报错时告诉用户去哪儿看。</summary>
    public static string ServiceRepository => $"https://github.com/{ServiceOwner}/{ServiceRepo}";

    // ─────────────────────────────────────────────────────────────
    //  备用锚点：让「锚点本身没了」也有救
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 额外的清单锚点，每项写 <c>owner/repo</c>（该仓库根目录下也要有一份内容相同的 <c>update.json</c>）。
    /// <para>
    /// 为什么需要这个：锚点解决了「发布仓库搬家」，但解决不了「托管锚点的账号整体出事」。
    /// 2026-10 就发生过一次——组织被 GitHub 标记隐藏，组织下所有仓库对匿名访问全部 404，
    /// 清单的六条通道和 Releases 兜底源一起断，客户端没有任何退路。
    /// </para>
    /// <para>
    /// 这里可以再挂一份放在<b>另一个账号</b>下的清单：只要两份里有一份活着，
    /// 更新链路就还通。留空表示只用主锚点（默认行为，零额外请求）。
    /// </para>
    /// <para>
    /// <b>注意</b>：备用锚点只对「包含这段代码的版本」有效，
    /// 已经装在别人电脑上的旧版本只认主锚点——救不了它们，只能靠把主锚点恢复。
    /// </para>
    /// </summary>
    public static readonly string[] ExtraServiceAnchors = [];

    /// <summary>某个锚点的清单原始地址。</summary>
    public static string ManifestRaw(string owner, string repo) =>
        $"https://raw.githubusercontent.com/{owner}/{repo}/main/update.json";

    /// <summary>某个锚点的 jsDelivr 镜像地址。</summary>
    public static string ManifestMirror(string owner, string repo) =>
        $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@main/update.json";

    /// <summary>某个锚点的 Contents API 地址。</summary>
    public static string ManifestApi(string owner, string repo) =>
        $"https://api.github.com/repos/{owner}/{repo}/contents/update.json";

    /// <summary>
    /// 所有锚点（主锚点在前，随后是 <see cref="ExtraServiceAnchors"/>）。
    /// 解析不了的行直接跳过，不让一处手误拖垮整个更新。
    /// </summary>
    public static IReadOnlyList<(string Owner, string Repo)> ServiceAnchors()
    {
        var list = new List<(string Owner, string Repo)> { (ServiceOwner, ServiceRepo) };

        foreach (var entry in ExtraServiceAnchors)
        {
            var parts = (entry ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[0].Contains(':') || parts[1].Contains(':')) continue;
            if (list.Contains((parts[0], parts[1]))) continue;
            list.Add((parts[0], parts[1]));
        }

        return list;
    }
}
