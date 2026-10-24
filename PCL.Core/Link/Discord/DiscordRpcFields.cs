using System.Collections.Generic;

namespace PCL.Core.Link.Discord;

/// <summary>
/// Rich Presence 可展示的内容项。存进配置时用逗号分隔的字符串。
/// <para>
/// 单独放一个文件是因为它被三处引用（配置默认值、设置页复选框、渲染逻辑），
/// 而且没有任何依赖，可以脱离整个启动器单独测试。
/// </para>
/// </summary>
public static class DiscordRpcFields
{
    /// <summary>启动器版本号（未开游戏时显示在标题行）。</summary>
    public const string LauncherVersion = "launcherVersion";

    /// <summary>启动器内存占用（未开游戏时）。</summary>
    public const string LauncherMemory = "launcherMemory";

    /// <summary>游戏实例名（开游戏时显示在标题行）。</summary>
    public const string GameVersion = "gameVersion";

    /// <summary>游戏进程内存占用（开游戏时）。</summary>
    public const string GameMemory = "gameMemory";

    /// <summary>时长：开游戏时是游玩时长，未开时是启动器运行时长。</summary>
    public const string PlayTime = "playTime";

    /// <summary>服务器地址。涉及隐私，默认关闭。</summary>
    public const string ServerAddress = "serverAddress";

    /// <summary>默认开启的项。服务器地址刻意不在里面。</summary>
    public const string DefaultValue = LauncherVersion + "," + LauncherMemory + "," + PlayTime;

    /// <summary>全部项，供设置页生成复选框。</summary>
    public static readonly IReadOnlyList<string> All =
        [LauncherVersion, LauncherMemory, GameVersion, GameMemory, PlayTime, ServerAddress];
}
