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
    /// <summary>
    ///     把相对路径解析到 <paramref name="root" /> 下；越界时返回 null。
    ///     <para>
    ///         比较之前会把两边的 junction / 符号链接展开成真实路径。
    ///         只做字符串前缀比较的话，游戏目录里放一个指向 <c>C:\</c> 的目录联接就能越界。
    ///     </para>
    ///     <para>
    ///         注意这仍然不是原子的：校验与实际操作之间文件系统可能变化（TOCTOU）。
    ///         彻底解决要改成按句柄操作，代价远大于收益，这里只保证常规路径不可绕过。
    ///     </para>
    /// </summary>
    public static string? ResolveUnder(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;

        string rootFull, targetFull;
        try
        {
            rootFull = RealPath(Path.GetFullPath(root));
            targetFull = RealPath(Path.GetFullPath(
                Path.IsPathRooted(relative ?? "") ? relative! : Path.Combine(rootFull, relative ?? "")));
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

    /// <summary>
    ///     逐级展开路径中已存在部分的链接目标。不存在或解析失败的部分原样保留。
    /// </summary>
    private static string RealPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var rootPart = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(rootPart)) return full;

            var current = rootPart;
            var segments = full[rootPart.Length..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                var next = Path.Combine(current, segment);
                current = ResolveOne(next) ?? next;
            }

            return current;
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static string? ResolveOne(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists) return null;
            return info.ResolveLinkTarget(true)?.FullName;
        }
        catch (Exception)
        {
            // 没有权限、链接损坏等，当作普通路径
            return null;
        }
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
