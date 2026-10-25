using System;

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
    /// </summary>
    public const string ServiceOwner = "Pcl-Lite";

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
}
