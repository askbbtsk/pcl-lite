using System;

namespace PCL.Core.Link.Discord;

/// <summary>
/// 游戏侧的快照。由启动器前端填充，PCL.Core 不直接引用 WPF 运行时。
/// </summary>
/// <param name="Running">游戏进程是否还活着。</param>
/// <param name="InstanceName">当前实例名，如 <c>1.20.1-Forge</c>。</param>
/// <param name="MemoryBytes">游戏进程工作集，取不到时为 0。</param>
/// <param name="ServerAddress">当前连接的服务器地址（host 或 host:port），未联机时为 <see langword="null"/>。</param>
public sealed record DiscordGameSnapshot(
    bool Running,
    string? InstanceName,
    long MemoryBytes,
    string? ServerAddress);

/// <summary>
/// 前端向 Rich Presence 服务注入运行时能力的桥。
/// </summary>
public static class DiscordHostBridge
{
    /// <summary>
    /// 读取游戏状态。返回 <see langword="null"/> 表示读不到（启动器还没初始化完）。
    /// <para>实现方必须自己保证线程安全（内部切到 UI 线程）。</para>
    /// </summary>
    public static Func<DiscordGameSnapshot?>? GameSnapshotProvider { get; set; }
}
