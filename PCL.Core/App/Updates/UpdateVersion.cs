using System;
using System.Collections.Generic;
using System.Globalization;
using PCL.Core.Utils;

namespace PCL.Core.App.Updates;

/// <summary>
/// 启动器版本名的比较规则。
/// <para>
/// 为什么不直接用 <see cref="SemVer"/>：SemVer 规定「带后缀的版本低于同号正式版」，
/// 这对 <c>pre</c> / <c>snapshot</c> / <c>nightly</c> 是对的，但 <c>hotfix</c> 正好相反——
/// 热修复是**打在同号正式版之上**的补丁，<c>1.6.0-hotfix-1</c> 必须比 <c>1.6.0</c> 新，
/// 否则 1.6.0 的用户永远收不到它（这恰恰是最需要它的时候）。
/// </para>
/// <para>
/// 所以这里给后缀分档，档位决定它相对「同号正式版」是高还是低；同一档里再比序号。
/// 规则按顺序生效：
/// </para>
/// <list type="number">
/// <item>主 / 次 / 修订号（<c>1.6.0</c> 的三段），大的赢</item>
/// <item>后缀档位：hotfix(+1) &gt; 正式版(0) &gt; pre(-1) &gt; snapshot(-2) &gt; nightly(-3)</item>
/// <item>后缀序号：<c>hotfix-2</c> &gt; <c>hotfix-1</c>；没写序号按 1 算（<c>pre</c> 等价于 <c>pre-1</c>）</item>
/// <item>以上全平手时退回 SemVer 的后缀比较，保住 <c>beta &lt; pre</c> 这类老顺序</item>
/// </list>
/// </summary>
public static class UpdateVersion
{
    /// <summary>热修复档：比同号正式版更新。</summary>
    public const int RankHotfix = 1;

    /// <summary>正式版档。</summary>
    public const int RankStable = 0;

    /// <summary>预览档（pre / preview / beta / alpha / rc，以及认不出来的后缀）。</summary>
    public const int RankPreview = -1;

    /// <summary>快照档。</summary>
    public const int RankSnapshot = -2;

    /// <summary>不稳定档。</summary>
    public const int RankLatest = -3;

    /// <summary>比较用的字符串比较器，供 <c>OrderBy</c> 之类的 LINQ 用。</summary>
    public static IComparer<string?> Comparer { get; } = new StringComparer();

    /// <summary>
    /// 比较两个版本名。返回值语义同 <see cref="IComparable"/>：负数表示 <paramref name="a"/> 更旧。
    /// <para>
    /// 解析不了的版本一律排在最后：一个脏 tag 不该被当成「最新」推给用户。
    /// </para>
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var okA = TryParse(a, out var keyA);
        var okB = TryParse(b, out var keyB);

        if (!okA && !okB) return string.Compare(a, b, StringComparison.Ordinal);
        if (!okA) return -1;
        if (!okB) return 1;

        return keyA.CompareTo(keyB);
    }

    /// <summary><paramref name="candidate"/> 是否比 <paramref name="current"/> 新。</summary>
    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    /// <summary><paramref name="candidate"/> 是否不比 <paramref name="reference"/> 旧。</summary>
    public static bool IsAtLeast(string? candidate, string? reference) => Compare(candidate, reference) >= 0;

    /// <summary>
    ///     远程版本 <paramref name="remote"/> 是否不比本地 <paramref name="current"/> 新，也就是「已经是最新」。
    ///     <para>
    ///         单独抽出来是因为这里极容易写反：判断「已是最新」时随手写成
    ///         <see cref="IsAtLeast" />(remote, current)，看着像「远程够新」，实际含义是
    ///         「远程 ≥ 当前」，于是 `1.6.1-hotfix-1` 对着 `1.6.1` 会直接判成已是最新，
    ///         热修复永远发不出去。方向只有一个：远程比当前新 → 不是最新。
    ///     </para>
    /// </summary>
    public static bool IsUpToDate(string? remote, string? current) => !IsNewer(remote, current);

    /// <summary>把一个版本名拆成可比较的键。</summary>
    public static bool TryParse(string? name, out Key key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(name)) return false;

        // SemVer 的正则只认小写 v，GitHub 上大写 V 的 tag 也不少见，先自己削掉
        var text = name.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        if (!SemVer.TryParse(text, out var semVer) || semVer is null) return false;

        var suffix = semVer.Prerelease;
        key = new Key(
            semVer.Major, semVer.Minor, semVer.Patch,
            _RankOf(suffix), _SequenceOf(suffix), _FamilyOf(suffix));
        return true;
    }

    /// <summary>
    /// 后缀 → 档位。判定顺序不能换：<c>hotfix</c> 与 <c>nightly</c> 这类词一旦被
    /// 后面的兜底规则抢先，就会掉进「预览档」，整条排序都错。
    /// </summary>
    private static int _RankOf(string suffix)
    {
        if (suffix.Length == 0) return RankStable;
        if (suffix.Contains("hotfix", StringComparison.OrdinalIgnoreCase)) return RankHotfix;
        if (suffix.Contains("nightly", StringComparison.OrdinalIgnoreCase)) return RankLatest;
        if (suffix.Contains("snapshot", StringComparison.OrdinalIgnoreCase)) return RankSnapshot;
        return RankPreview;
    }

    /// <summary>
    /// 后缀里的序号，取最后一段纯数字。没写就按 1 算，
    /// 这样 <c>1.6.0-pre</c>（1.6.0 之前的老版本）与 <c>1.6.0-pre-1</c> 等价，
    /// 不用回头去改历史 Release 的 tag。
    /// </summary>
    private static int _SequenceOf(string suffix)
    {
        if (suffix.Length == 0) return 0;

        var parts = suffix.Split(['-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 &&
            int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            return sequence;

        return 1;
    }

    /// <summary>
    /// 后缀去掉尾部序号之后的「家族名」：<c>hotfix-2</c> → <c>hotfix</c>、<c>pre</c> → <c>pre</c>。
    /// <para>
    /// 只在最后一层平手时用来收尾。带着序号比会把 <c>hotfix</c> 和 <c>hotfix-1</c> 判成两个版本
    /// （字符串前缀比较），而它们本该等价。
    /// </para>
    /// </summary>
    private static string _FamilyOf(string suffix)
    {
        if (suffix.Length == 0) return string.Empty;

        var parts = suffix.Split(['-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return suffix;

        return int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? string.Join('-', parts[..^1])
            : suffix;
    }

    /// <summary>拆分后的版本键。</summary>
    public readonly record struct Key(int Major, int Minor, int Patch, int Rank, int Sequence, string Family)
        : IComparable<Key>
    {
        public int CompareTo(Key other)
        {
            var compare = Major.CompareTo(other.Major);
            if (compare != 0) return compare;

            compare = Minor.CompareTo(other.Minor);
            if (compare != 0) return compare;

            compare = Patch.CompareTo(other.Patch);
            if (compare != 0) return compare;

            compare = Rank.CompareTo(other.Rank);
            if (compare != 0) return compare;

            compare = Sequence.CompareTo(other.Sequence);
            if (compare != 0) return compare;

            // 同档同号：按家族名收尾（beta < pre 这类老顺序）
            return new SemVer(0, 0, 0, Family).CompareTo(new SemVer(0, 0, 0, other.Family));
        }
    }

    private sealed class StringComparer : IComparer<string?>
    {
        public int Compare(string? x, string? y) => UpdateVersion.Compare(x, y);
    }
}
