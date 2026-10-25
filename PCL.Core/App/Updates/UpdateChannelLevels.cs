using System;

namespace PCL.Core.App.Updates;

/// <summary>
///     版本后缀 → 不稳定等级。
///     <para>
///         等级只用来回答一个问题：「这个版本该不该推给选了某个通道的用户」。
///         判定统一写成 <c>版本等级 &lt;= 通道等级</c>，所以每个通道都是**包含式**的：
///         选了激进通道照样收得到正式版。这条很关键——否则选了「始终最新」的人
///         在正式版发布后会卡住，因为他们手里的 nightly 版本号比新正式版大，
///         永远等不到「更新的」预发布。
///     </para>
/// </summary>
public static class UpdateChannelLevels
{
    /// <summary>正式版：版本名不带任何后缀。</summary>
    public const int Stable = 0;

    /// <summary>预览版：pre / preview / beta / alpha / rc 等，统一归到这一档。</summary>
    public const int Preview = 1;

    /// <summary>快照版：snapshot。</summary>
    public const int Snapshot = 2;

    /// <summary>不稳定版：nightly。</summary>
    public const int Latest = 3;

    /// <summary>
    ///     从版本后缀判定不稳定等级，没有后缀就是正式版。
    ///     <para>
    ///         nightly 必须最先判——它是最激进的一档，不能被后面的规则抢先。
    ///         其余预发布后缀分得再细也没有对应的通道，归成预览一档反而不会漏收。
    ///     </para>
    /// </summary>
    public static int FromSuffix(string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix)) return Stable;

        var text = suffix.Trim().TrimStart('-', '.').Trim();
        if (text.Length == 0) return Stable;

        if (text.Contains("nightly", StringComparison.OrdinalIgnoreCase)) return Latest;
        if (text.Contains("snapshot", StringComparison.OrdinalIgnoreCase)) return Snapshot;
        return Preview;
    }

    /// <summary>
    ///     从完整版本名判定等级，例如 <c>1.3.2-beta</c> → 预览级、<c>v1.3.2</c> → 正式级。
    /// </summary>
    public static int FromVersionName(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName)) return Stable;

        var text = versionName.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        // SemVer 里后缀跟在第一个 '-' 之后；'+' 之后是构建元数据，与等级无关
        var dash = text.IndexOf('-');
        if (dash < 0) return Stable;

        var suffix = text[(dash + 1)..];
        var plus = suffix.IndexOf('+');
        if (plus >= 0) suffix = suffix[..plus];

        return FromSuffix(suffix);
    }

    /// <summary>等级的中文说明，用于日志与提示。</summary>
    public static string Describe(int level) => level switch
    {
        Latest => "不稳定版（nightly）",
        Snapshot => "快照版（snapshot）",
        Preview => "预览版（pre）",
        _ => "正式版"
    };
}
