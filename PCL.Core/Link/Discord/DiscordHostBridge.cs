using System;

namespace PCL.Core.Link.Discord;

/// <summary>
/// 游戏侧的快照。由启动器前端填充，PCL.Core 不直接引用 WPF 运行时。
/// </summary>
/// <param name="Running">游戏进程是否还活着。</param>
/// <param name="InstanceName">当前实例名，如 <c>1.20.1-Forge</c>。</param>
/// <param name="MemoryBytes">游戏进程工作集，取不到时为 0。</param>
/// <param name="ServerAddress">当前连接的服务器地址（host 或 host:port），未联机时为 <see langword="null"/>。</param>
/// <param name="StartedAt">
/// 游戏进程的启动时间，取不到时为 <see langword="null"/>。
/// <para>
/// 游玩时长用它做起点，而不是「第一次发现游戏在跑的那一刻」。后者会随着一次读取失败
/// 被清空重来，用户看到的就是时长忽然从头开始；用进程启动时间则怎么重算都是同一个值。
/// </para>
/// </param>
public sealed record DiscordGameSnapshot(
    bool Running,
    string? InstanceName,
    long MemoryBytes,
    string? ServerAddress,
    DateTime? StartedAt = null);

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
