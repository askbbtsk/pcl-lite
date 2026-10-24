
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp;

/// <summary>
/// 一个游戏实例在磁盘上的位置与基础信息。
/// </summary>
/// <param name="Name">实例名（versions 下的文件夹名）。</param>
/// <param name="InstanceDirectory">实例自身目录，以分隔符结尾。</param>
/// <param name="GameRoot">
/// 该实例实际生效的 .minecraft 根目录，以分隔符结尾。
/// 开了版本隔离时等于 <paramref name="InstanceDirectory"/>，否则等于全局 .minecraft。
/// </param>
/// <param name="McVersion">Minecraft 版本号，未知时为空串。</param>
/// <param name="Loader">加载器名称（Vanilla / Fabric / Forge / NeoForge / Quilt / LiteLoader），未知时为空串。</param>
/// <param name="LoaderVersion">加载器版本，未知时为空串。</param>
/// <param name="JavaPath">实例指定的 Java 路径，未指定时为空串。</param>
/// <param name="RamMb">实例分配的内存（MB），未知时为 0。</param>
public sealed record InstanceLocation(
    string Name,
    string InstanceDirectory,
    string GameRoot,
    string McVersion,
    string Loader,
    string LoaderVersion,
    string JavaPath,
    int RamMb);

/// <summary>
/// 宿主桥：PCL.Core 不引用 WPF 项目，所以「需要启动器内部状态」的能力由上层注入。
/// <para>
/// 没注入时对应工具会返回明确的「宿主未接入」错误，而不是空指针崩溃——
/// 这样 PCL.Core 可以被单独引用（例如测试工程）而不带整套 UI。
/// </para>
/// </summary>
public static class McpHostBridge
{
    /// <summary>列出全部实例名。</summary>
    public static Func<IReadOnlyList<string>>? ListInstances { get; set; }

    /// <summary>当前在启动器里选中的实例名。</summary>
    public static Func<string?>? GetSelectedInstance { get; set; }

    /// <summary>把实例名解析成磁盘位置与基础信息；实例不存在时返回 <see langword="null" />。</summary>
    public static Func<string, InstanceLocation?>? LocateInstance { get; set; }

    /// <summary>启动游戏。<c>instanceName</c> 为 null 表示用当前选中实例。</summary>
    public static Func<string?, IReadOnlyList<string>, Task<object?>>? LaunchGameAsync { get; set; }

    /// <summary>停止游戏进程。</summary>
    public static Func<Task<object?>>? StopGameAsync { get; set; }

    /// <summary>游戏运行状态快照。</summary>
    public static Func<object?>? GetGameState { get; set; }

    /// <summary>在启动器的 UI 线程上执行一段代码。</summary>
    public static Func<Func<object?>, Task<object?>>? InvokeOnUiAsync { get; set; }

    public static bool IsAttached => LocateInstance is not null;

    /// <summary>取当前实例名：优先用调用方给的，其次用启动器选中的。</summary>
    public static string? ResolveInstanceName(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        return GetSelectedInstance?.Invoke();
    }

    public static string RequireHostAttached()
        => "启动器宿主尚未接入 MCP 桥（McpHostBridge 未初始化），该功能不可用。";
}
