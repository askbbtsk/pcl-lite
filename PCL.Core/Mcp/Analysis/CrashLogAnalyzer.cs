using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp.Analysis;

/// <summary>一条崩溃相关的判断。</summary>
public sealed record CrashHint
{
    public required string Severity { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Suggestion { get; init; }
}

/// <summary>崩溃分析结果。</summary>
public sealed record CrashReport
{
    public required string Source { get; init; }
    public required string SourcePath { get; init; }
    public string Description { get; init; } = "";
    public string ExceptionType { get; init; } = "";
    public string ExceptionMessage { get; init; } = "";
    public IReadOnlyList<string> Causes { get; init; } = [];
    public IReadOnlyList<string> SuspectMods { get; init; } = [];
    public IReadOnlyList<string> StackHighlights { get; init; } = [];
    public IReadOnlyList<CrashHint> Hints { get; init; } = [];
    public string Summary { get; init; } = "";
    public string Tail { get; init; } = "";
}

/// <summary>
/// 崩溃日志分析。
/// <para>
/// 纯文本规则匹配，不依赖启动器内部的 CrashAnalyzer——后者会弹窗、会写临时目录，
/// 不适合被 MCP 在后台线程调用。这里的规则集覆盖了实际遇到最多的情况。
/// </para>
/// </summary>
public static class CrashLogAnalyzer
{
    private const long MaxReadBytes = 6L * 1024 * 1024;

    /// <summary>
    /// 分析一个实例最近一次崩溃。优先看 crash-reports，其次看 latest.log。
    /// </summary>
    public static CrashReport? AnalyzeInstance(InstanceLocation location)
    {
        var crashDir = ModDirectoryScanner.CrashReports(location);
        var latestCrash = FindLatest(crashDir, "crash-*.txt");
        if (latestCrash is not null)
            return AnalyzeFile(latestCrash, "crash-report");

        var logsDir = ModDirectoryScanner.Logs(location);
        var latestLog = Path.Combine(logsDir, "latest.log");
        if (File.Exists(latestLog))
            return AnalyzeFile(latestLog, "latest.log");

        var debugLog = Path.Combine(logsDir, "debug.log");
        if (File.Exists(debugLog))
            return AnalyzeFile(debugLog, "debug.log");

        return null;
    }

    public static string? FindLatest(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return null;
        try
        {
            return Directory.EnumerateFiles(directory, pattern)
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static CrashReport AnalyzeFile(string path, string source)
    {
        var text = ReadTail(path, MaxReadBytes);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var description = FirstMatch(text, @"^Description:\s*(.+)$");
        var exceptionType = FirstMatch(text, @"^(?:java\.lang\.|net\.minecraft\.|org\.spongepowered\.)?(\w+(?:\.\w+)*(?:Exception|Error))\b");
        var exceptionMessage = FirstMatch(text, @"(?:Exception|Error)(?::\s*)(.+)$");

        var causes = Regex.Matches(text, @"^Caused by:\s*(.+)$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct()
            .Take(8)
            .ToList();

        var suspects = ExtractSuspectMods(text);
        var hints = BuildHints(text, causes, suspects);

        var stack = lines
            .Where(l => Regex.IsMatch(l, @"^\s*at\s+[\w$.]+\(") && !l.Contains("java.base/", StringComparison.Ordinal))
            .Take(12)
            .ToList();

        var summary = hints.Count > 0
            ? hints[0].Title
            : exceptionType.Length > 0
                ? $"崩溃类型：{exceptionType}"
                : "没有识别出明确的崩溃特征，可能需要看完整日志。";

        var tail = string.Join('\n', lines.TakeLast(40));

        return new CrashReport
        {
            Source = source,
            SourcePath = path,
            Description = description,
            ExceptionType = exceptionType,
            ExceptionMessage = exceptionMessage,
            Causes = causes,
            SuspectMods = suspects,
            StackHighlights = stack,
            Hints = hints,
            Summary = summary,
            Tail = tail
        };
    }

    #region 规则

    private static List<CrashHint> BuildHints(string text, List<string> causes, List<string> suspects)
    {
        var hints = new List<CrashHint>();

        void Add(string severity, string title, string detail, string suggestion)
            => hints.Add(new CrashHint
            {
                Severity = severity,
                Title = title,
                Detail = detail,
                Suggestion = suggestion
            });

        if (text.Contains("DuplicateModsFoundException", StringComparison.Ordinal)
            || text.Contains("duplicate mods", StringComparison.OrdinalIgnoreCase))
        {
            var dupes = Regex.Matches(text, @"Mod ID:\s*(\S+)")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();
            Add("error", "mod 重复安装",
                dupes.Count > 0 ? $"重复的 mod：{string.Join("、", dupes)}" : "检测到 DuplicateModsFoundException。",
                "同一个 mod 在 mods 目录里有多份，只保留一份。用 pcl_analyze_instance 可以直接看到是哪几个文件。");
        }

        if (text.Contains("Missing or unsupported mandatory dependencies", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ModResolutionException", StringComparison.Ordinal))
        {
            var deps = Regex.Matches(text, @"(?:Mod ID|modId)'?:\s*'?([\w-]+)")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .Take(10)
                .ToList();
            Add("error", "缺少前置 mod",
                deps.Count > 0 ? $"涉及：{string.Join("、", deps)}" : "加载器报依赖解析失败。",
                "按提示的 modId 去下载页补装前置；装完再启动。");
        }

        if (text.Contains("NoSuchMethodError", StringComparison.Ordinal)
            || text.Contains("NoClassDefFoundError", StringComparison.Ordinal)
            || text.Contains("ClassNotFoundException", StringComparison.Ordinal))
        {
            Add("error", "类/方法找不到：mod 版本与游戏版本不匹配",
                "最常见的原因是 mod 是给另一个 MC 版本编译的，或者缺了它依赖的库 mod。",
                "用 pcl_analyze_instance 看有没有 mc-version-mismatch 和 missing-dependency 两项。");
        }

        if (text.Contains("OutOfMemoryError", StringComparison.Ordinal))
            Add("error", "内存不足",
                "Java 堆在运行中耗尽，通常是分配的内存不够或者有 mod 内存泄漏。",
                "把实例内存调到 6~8 GB；如果已经是 8 GB 还 OOM，用 pcl_analyze_instance 看 mod 数量是否过多。");

        if (text.Contains("Pixel format not accelerated", StringComparison.Ordinal)
            || text.Contains("Failed to create window", StringComparison.Ordinal)
            || text.Contains("GLFW error", StringComparison.Ordinal)
            || text.Contains("OpenGL", StringComparison.Ordinal) && text.Contains("error", StringComparison.OrdinalIgnoreCase))
            Add("error", "显卡 / OpenGL 初始化失败",
                "游戏窗口创建不出来，一般是显卡驱动过旧、缺少 OpenGL 支持，或者在远程桌面/虚拟机里跑。",
                "更新显卡驱动；如果是核显老机器，可以试试关掉光影、降低 OpenGL 要求。");

        if (text.Contains("UnsatisfiedLinkError", StringComparison.Ordinal))
            Add("error", "本地库加载失败",
                "多为 LWJGL 版本与系统架构不匹配（例如 32 位 Java 跑 64 位库）。",
                "确认用的是 64 位 Java；如果装了自定义 LWJGL，先删掉换回默认。");

        if (text.Contains("Mixin", StringComparison.Ordinal) && text.Contains("apply", StringComparison.OrdinalIgnoreCase)
            && text.Contains("failed", StringComparison.OrdinalIgnoreCase))
            Add("warning", "Mixin 注入失败",
                "某个 mod 试图改动的游戏代码被另一个 mod 改过了，两者冲突。",
                "看 stack 里提到的类名属于哪个 mod，禁用其中一个；常见冲突组合是优化类 mod 之间。");

        if (text.Contains("Could not find or load main class", StringComparison.Ordinal)
            || text.Contains("Error: Could not find or load", StringComparison.Ordinal))
            Add("error", "版本 jar 损坏或缺失",
                "启动参数里的主类在 jar 里找不到，通常是版本文件下载不完整。",
                "在实例设置里重新下载该版本（或删掉 versions 下对应目录重装）。");

        if (text.Contains("Exit Code: -1073740791", StringComparison.Ordinal)
            || text.Contains("0xC0000409", StringComparison.OrdinalIgnoreCase))
            Add("error", "进程被系统强制终止（栈溢出保护）",
                "常见于显卡驱动崩溃或某个 mod 递归过深。",
                "先更新显卡驱动；仍然复现的话逐个禁用近期新装的 mod。");

        if (suspects.Count > 0)
            Add("warning", "可疑 mod",
                $"栈里出现了这些 mod 的包名：{string.Join("、", suspects)}",
                "先用 pcl_disable_mod 把它们逐个禁用做二分排查，能最快定位问题源。");

        if (hints.Count == 0 && causes.Count > 0)
            Add("info", "只抓到异常链，没匹配到已知模式",
                $"最内层原因：{causes[^1]}",
                "把完整的 crash-report 内容交给模型分析，或者按「最近装了什么 mod」的思路排查。");

        return hints;
    }

    /// <summary>
    /// 从 stack trace 里猜可疑 mod：抓包名前缀，与常见 mod 包名规律做匹配。
    /// 不追求精确，目的是给一个「先查这几个」的短名单。
    /// </summary>
    private static List<string> ExtractSuspectMods(string text)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "java", "javax", "jdk", "sun", "net.minecraft", "net.minecraftforge", "cpw", "org.spongepowered",
            "com.mojang", "org.lwjgl", "it.unimi", "io.netty", "org.apache", "com.google", "org.slf4j",
            "kotlin", "kotlinx", "org.jetbrains", "oshi", "com.electronwill", "org.objectweb"
        };

        var result = new List<string>();
        foreach (Match match in Regex.Matches(text, @"^\s*at\s+([\w$]+(?:\.[\w$]+){1,3})\.",
                     RegexOptions.Multiline))
        {
            var full = match.Groups[1].Value;
            var segments = full.Split('.');
            var prefix = string.Join('.', segments.Take(2));
            if (ignored.Contains(prefix)) continue;
            if (prefix.StartsWith("java.", StringComparison.Ordinal)) continue;
            if (prefix.StartsWith("net.minecraft", StringComparison.Ordinal)) continue;
            if (!result.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                result.Add(prefix);
            if (result.Count >= 12) break;
        }

        return result;
    }

    #endregion

    #region 读取

    private static string FirstMatch(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    /// <summary>
    /// 读文件末尾，最多 <paramref name="maxBytes" />。
    /// 崩溃日志动辄几十 MB，全读进来会拖慢整个工具调用。
    /// </summary>
    private static string ReadTail(string path, long maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = stream.Length;
            if (length <= maxBytes)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                return reader.ReadToEnd();
            }

            stream.Seek(-maxBytes, SeekOrigin.End);
            var buffer = new byte[maxBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception)
        {
            return "";
        }
    }

    #endregion
}
