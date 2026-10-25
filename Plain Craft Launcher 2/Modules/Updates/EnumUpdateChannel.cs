namespace PCL;

/// <summary>
///     取版本时允许收哪些预发布等级。
///     <para>
///         数值越大越激进，判定统一写成「版本等级 &lt;= 通道等级」，
///         所以每个通道都是**包含式**的：选了激进通道照样能收到正式版。
///     </para>
///     <para>
///         等级本身（后缀怎么算、各档叫什么）在 <see cref="Core.App.Updates.UpdateChannelLevels"/>，
///         那边是纯逻辑、有单元测试；这里只负责「用户选项 → 取版本通道」这一步映射。
///     </para>
/// </summary>
public enum UpdateChannel
{
    /// <summary>只收正式版。</summary>
    stable = 0,

    /// <summary>正式版 + 预览版（pre / beta / alpha / rc）。</summary>
    preview = 1,

    /// <summary>正式版 + 预览版 + 快照版（snapshot）。</summary>
    snapshot = 2,

    /// <summary>全部，含不稳定版（nightly）。</summary>
    latest = 3
}

public enum UpdateArch
{
    x64,
    arm64
}

/// <summary>
///     用户选的通道 → 取版本通道。
/// </summary>
public static class UpdateChannelMapping
{
    /// <summary>
    ///     用户选项与取版本通道一一对应（Stable=0 / Preview=1 / Snapshot=2 / Latest=3），
    ///     直接强转即可；越界值夹回两端，避免手改配置文件写出 9 这种值时行为诡异。
    /// </summary>
    public static UpdateChannel ToChannel(Core.App.UpdateChannel channel)
    {
        var value = (int)channel;
        if (value <= (int)UpdateChannel.stable) return UpdateChannel.stable;
        if (value >= (int)UpdateChannel.latest) return UpdateChannel.latest;
        return (UpdateChannel)value;
    }
}
