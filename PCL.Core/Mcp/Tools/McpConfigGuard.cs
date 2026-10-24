using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Mcp.Tools;

/// <summary>
/// 配置类工具的访问闸门。
/// <para>
/// 不加这层的话，模型可以：用 <c>pcl_config_get</c> 把 MCP 访问口令原样读进对话记录；
/// 用 <c>pcl_config_set</c> 把「允许修改游戏文件」改成 true、把口令清空、把端口改掉。
/// 也就是说，一旦用户打开「允许修改启动器设置」，前面所有权限开关都会失效。
/// </para>
/// <para>
/// 因此这里分两类：<b>永远不可读</b>（口令等机密）与<b>不可写</b>（安全开关、MCP 自身配置）。
/// </para>
/// </summary>
internal static class McpConfigGuard
{
    /// <summary>明确禁止读取的键：读出来就等于把凭据抄进了对话记录。</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "McpToken"
    };

    /// <summary>键名里出现这些片段就当作机密，避免以后新增凭据字段时漏配。</summary>
    private static readonly string[] SecretFragments = ["token", "secret", "password", "apikey", "accesskey"];

    /// <summary>
    /// 禁止写入的键：要么是 MCP 自己的权限与连接参数（自我提权 / 自废武功），
    /// 要么是启动器的安全开关。
    /// </summary>
    private static readonly HashSet<string> ProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // MCP 自身：改这些等于让模型给自己发权限
        "McpEnabled",
        "McpPort",
        "McpToken",
        "McpAllowWrite",
        "McpAllowGameControl",
        "McpAllowLauncherConfig",
        // 启动器安全开关：绕过去就能扩大 MCP 的可见范围
        "SystemDebugMode",
        "SystemDebugAllowRestrictedFeature"
    };

    /// <summary>是否属于不可读的机密键。</summary>
    public static bool IsSecret(string key)
    {
        if (SecretKeys.Contains(key)) return true;
        return SecretFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>是否属于不可写的受保护键。</summary>
    public static bool IsProtected(string key)
    {
        // 机密键一律不可写，否则「清空口令」就能关掉认证
        return IsSecret(key) || ProtectedKeys.Contains(key);
    }

    /// <summary>读被拒绝时给模型的说明。</summary>
    public static string ReadDeniedMessage(string key)
        => $"「{key}」是凭据类配置，不允许通过 MCP 读取。"
           + "需要的话请让用户自己在启动器设置里查看。";

    /// <summary>写被拒绝时给模型的说明。</summary>
    public static string WriteDeniedMessage(string key)
        => $"「{key}」是安全相关配置，不允许由 MCP 修改。"
           + "包括 MCP 自身的开关、端口、口令、权限项，以及启动器的安全开关——"
           + "这些只能由用户在启动器界面里手动调整。";

    /// <summary>受保护键清单，供工具描述与文档引用。</summary>
    public static IReadOnlyList<string> ProtectedKeyList => ProtectedKeys.OrderBy(x => x).ToList();
}
