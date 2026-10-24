using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Mcp;

/// <summary>
/// 启动参数白名单校验。
/// <para>
/// <c>pcl_launch_game</c> 允许传额外 JVM 参数，这些参数会被原样拼进 Java 命令行。
/// 问题在于 <c>-javaagent:xxx.jar</c> 之类的参数等于在游戏 JVM 里执行任意代码——
/// 那已经超出「帮我整理 mod」的授权范围，也不是用户在打开 MCP 时预期过的事。
/// </para>
/// <para>
/// 所以这里挡掉「能在 JVM 里加载外部代码」的一类参数，其余照常放行。
/// </para>
/// </summary>
public static class McpLaunchGuards
{
    /// <summary>单个参数的长度上限。</summary>
    private const int MaxArgumentLength = 512;

    /// <summary>额外参数的数量上限。</summary>
    private const int MaxArgumentCount = 32;

    /// <summary>精确匹配即拒绝的参数（不区分大小写）。</summary>
    private static readonly HashSet<string> ForbiddenExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "-jar",
        "-cp",
        "-classpath",
        "-m",
        "--module",
        "-p",
        "--module-path"
    };

    /// <summary>以这些前缀开头的参数一律拒绝。</summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "-javaagent:",
        "-agentlib:",
        "-agentpath:",
        "-xbootclasspath",
        "-djava.system.class.loader",
        "-djava.security.manager",
        "-djava.security.policy",
        "-djavax.net.ssl.keystore",
        "-xx:onerror=",
        "-xx:onoutofmemoryerror="
    ];

    /// <summary>
    ///     校验一组额外参数。返回 false 时 <paramref name="error" /> 是可以直接回给模型的说明。
    /// </summary>
    public static bool TryValidate(IReadOnlyList<string>? arguments, out string? error)
    {
        error = null;
        if (arguments is null || arguments.Count == 0) return true;

        if (arguments.Count > MaxArgumentCount)
        {
            error = $"额外参数最多 {MaxArgumentCount} 个，当前传了 {arguments.Count} 个。";
            return false;
        }

        foreach (var raw in arguments)
        {
            var argument = raw?.Trim() ?? "";
            if (argument.Length == 0)
            {
                error = "额外参数里不能有空字符串。";
                return false;
            }

            if (argument.Length > MaxArgumentLength)
            {
                error = $"参数过长（{argument.Length} 字符），上限 {MaxArgumentLength}。";
                return false;
            }

            // 这些参数最后是用空格拼接的，带空白或引号会被拆错，导致实际执行的不是模型想要的东西
            if (argument.Any(char.IsWhiteSpace) || argument.Contains('"'))
            {
                error = $"参数「{argument}」包含空白或引号，无法安全传递。一个参数请写成不带空格的形式。";
                return false;
            }

            if (ForbiddenExact.Contains(argument))
            {
                error = $"不允许通过 MCP 传「{argument}」：它会改变 Java 的启动目标或类路径。";
                return false;
            }

            var lower = argument.ToLowerInvariant();
            var forbidden = ForbiddenPrefixes.FirstOrDefault(lower.StartsWith);
            if (forbidden is not null)
            {
                error = $"不允许通过 MCP 传「{forbidden}…」这类参数：它可以在游戏 JVM 里加载或执行外部代码，"
                        + "超出 MCP 的授权范围。需要这类参数请让用户自己在启动器设置里加。";
                return false;
            }
        }

        return true;
    }
}
