using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.App;

namespace PCL.Core.Mcp;

/// <summary>
/// MCP 操作审计日志。
/// <para>
/// 只记录会改变状态的操作（写文件、控制进程、改配置），只读操作不入账，
/// 否则日志会被 <c>pcl_list_mods</c> 这类高频调用淹掉。
/// </para>
/// <para>
/// 落地为 JSON Lines：一行一条，方便 <c>Select-String</c> 或任何日志工具直接筛。
/// 位置在 <c>&lt;启动器目录&gt;/PCL/Log/mcp-audit.log</c>。
/// </para>
/// </summary>
internal static class McpAudit
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, byte> Warned = new(StringComparer.Ordinal);

    public static string LogPath =>
        Path.Combine(Basics.ExecutableDirectory, "PCL", "Log", "mcp-audit.log");

    /// <summary>记录一次操作。</summary>
    /// <param name="tool">工具名。</param>
    /// <param name="arguments">入参的简短摘要（已经过裁剪，不含大字段）。</param>
    /// <param name="ok">工具是否返回成功。</param>
    /// <param name="detail">失败原因或影响范围之类的补充。</param>
    public static void Record(string tool, string? arguments, bool ok, string? detail = null)
    {
        try
        {
            var entry = new JsonObject
            {
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                ["tool"] = tool,
                ["ok"] = ok,
                ["arguments"] = arguments,
                ["detail"] = detail
            };

            var line = entry.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // 审计写不进去不能影响工具本身；同一种错误只提示一次，别把日志刷爆
            if (Warned.TryAdd(ex.GetType().Name, (byte)0))
                Logging.LogWrapper.Warn(ex, "Mcp", "写入 MCP 审计日志失败");
        }
    }

    /// <summary>
    ///     把入参压成一行摘要：去掉空值，超长截断，避免把整个 mod 列表写进日志。
    /// </summary>
    public static string Summarize(JsonObject? arguments)
    {
        if (arguments is null) return "{}";

        var parts = new List<string>();
        foreach (var (key, value) in arguments)
        {
            if (value is null) continue;
            var text = value.ToJsonString();
            if (text.Length > 120) text = text[..120] + "…";
            parts.Add($"{key}={text}");
            if (parts.Count >= 8) break;
        }

        var summary = string.Join(", ", parts);
        return summary.Length > 400 ? summary[..400] + "…" : summary;
    }
}
