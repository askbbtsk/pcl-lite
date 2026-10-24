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
    public const string GitHubOwner = "bedkillerspacex-boop";

    /// <summary>仓库名。</summary>
    public const string GitHubRepo = "Pcl-Lite";

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
}
