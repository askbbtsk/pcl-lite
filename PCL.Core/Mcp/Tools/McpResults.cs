using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp.Tools;

/// <summary>工具返回值构造。所有工具统一返回 <c>{ ok: bool, ... }</c>，模型只需要看一个字段。</summary>
internal static class McpResults
{
    public static Dictionary<string, object?> Ok(params (string Key, object? Value)[] fields)
    {
        var result = new Dictionary<string, object?> { ["ok"] = true };
        foreach (var (key, value) in fields) result[key] = value;
        return result;
    }

    public static Dictionary<string, object?> Error(string message, string? hint = null)
    {
        var result = new Dictionary<string, object?> { ["ok"] = false, ["error"] = message };
        if (!string.IsNullOrWhiteSpace(hint)) result["hint"] = hint;
        return result;
    }
}

/// <summary>
/// 路径安全校验。
/// <para>
/// MCP 的入参来自模型，必须假设它可能写出 <c>../../Windows/System32</c>。
/// 所有涉及文件路径的工具都要先过这里，把操作锁死在游戏目录内。
/// </para>
/// </summary>
internal static class McpPath
{
    /// <summary>把相对路径解析到 <paramref name="root" /> 下；越界时返回 null。</summary>
    public static string? ResolveUnder(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;

        string rootFull, targetFull;
        try
        {
            rootFull = Path.GetFullPath(root);
            targetFull = Path.GetFullPath(
                Path.IsPathRooted(relative ?? "") ? relative! : Path.Combine(rootFull, relative ?? ""));
        }
        catch (Exception)
        {
            return null;
        }

        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (string.Equals(targetFull.TrimEnd(Path.DirectorySeparatorChar), rootFull.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return targetFull;
        return targetFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? targetFull : null;
    }

    /// <summary>只取文件名部分，去掉任何目录成分。</summary>
    public static string FileNameOnly(string? input)
        => string.IsNullOrWhiteSpace(input) ? "" : Path.GetFileName(input.Trim());
}

/// <summary>把 <see cref="InstanceLocation" /> 解析成工具可以直接用的形式。</summary>
internal static class McpInstanceResolver
{
    public static (InstanceLocation? Location, Dictionary<string, object?>? Error) Resolve(string? name)
    {
        if (McpHostBridge.LocateInstance is null)
            return (null, McpResults.Error(McpHostBridge.RequireHostAttached()));

        var resolved = McpHostBridge.ResolveInstanceName(name);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            var available = McpHostBridge.ListInstances?.Invoke() ?? [];
            return (null, McpResults.Error(
                "没有指定实例，启动器当前也没有选中任何实例。",
                available.Count > 0
                    ? $"可用的实例：{string.Join("、", available.Take(20))}"
                    : "请先让用户在启动器里创建一个实例。"));
        }

        var location = McpHostBridge.LocateInstance(resolved);
        if (location is null)
        {
            var available = McpHostBridge.ListInstances?.Invoke() ?? [];
            return (null, McpResults.Error(
                $"找不到实例「{resolved}」。",
                available.Count > 0 ? $"可用的实例：{string.Join("、", available.Take(20))}" : null));
        }

        return (location, null);
    }
}
