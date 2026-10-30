using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Core.App.Lunar;

/// <summary>
///     官方 Lunar 启动器自己那份启动设置（<c>settings/launcher.json</c>）。
/// </summary>
/// <param name="AllocatedMemoryMb">分配内存（MB）。<c>0</c> 表示文件里没写或读不出来。</param>
/// <param name="JvmArgs">自定义 JVM 参数，原样保留启动器里那一行。</param>
public sealed record LunarLauncherSettings(int AllocatedMemoryMb, string JvmArgs)
{
    /// <summary>没读到任何东西。</summary>
    public static readonly LunarLauncherSettings Empty = new(0, string.Empty);

    public bool HasMemory => AllocatedMemoryMb > 0;
    public bool HasJvmArgs => !string.IsNullOrWhiteSpace(JvmArgs);
}

/// <summary>
///     读取官方 Lunar 启动器的设置文件。
///     <para>
///         为什么要读它：PCL 和官方启动器共用 <c>~/.lunarclient</c> 时，内存和 JVM 参数
///         是<b>启动器各管各的</b> —— 官方那份存在 <c>settings/launcher.json</c> 的
///         <c>settings.allocatedMemory</c> / <c>settings.jvmArgs</c>，游戏进程只认命令行，
///         不看这个文件。PCL 以前完全不看它，于是「在 Lunar 启动器里调成 8G，换 PCL 启动
///         却只有 3G」——用户视角就是「PCL 把我的设置覆盖了」。
///     </para>
///     <para>
///         现在把它当成兜底值：PCL 设置页没填的时候，直接沿用官方启动器的数字。
///         <b>只读不写</b>，避免两边互相覆盖 —— 官方启动器每次改设置都会整份重写，
///         PCL 写进去的内容活不过下一次点击。
///     </para>
/// </summary>
public static class LunarLauncherConfig
{
    /// <summary>启动器设置文件的位置。</summary>
    public static string FilePath(string lunarRoot) => Path.Combine(lunarRoot, "settings", "launcher.json");

    /// <summary>
    ///     设置里指定的根目录优先，没指定就用默认的 <c>~/.lunarclient</c>。
    /// </summary>
    public static string ResolveRoot(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? LunarClientService.DefaultLunarRoot : configured;

    /// <summary>
    ///     读一份。文件不存在、内容坏掉、字段类型不对，一律退回 <see cref="LunarLauncherSettings.Empty" />。
    /// </summary>
    /// <param name="lunarRoot">Lunar 根目录；<see langword="null" /> 表示按配置解析。</param>
    public static LunarLauncherSettings Read(string? lunarRoot = null)
    {
        try
        {
            var path = FilePath(lunarRoot ?? ResolveRoot(null));
            if (!File.Exists(path)) return LunarLauncherSettings.Empty;
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 读不出来就当没有，别让启动流程跟着挂
            return LunarLauncherSettings.Empty;
        }
    }

    /// <summary>
    ///     解析 <c>launcher.json</c> 的内容。单独拆出来是为了能在单元测试里直接喂字符串。
    /// </summary>
    public static LunarLauncherSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return LunarLauncherSettings.Empty;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return LunarLauncherSettings.Empty;
        }

        if (root?["settings"] is not JsonObject settings) return LunarLauncherSettings.Empty;

        return new LunarLauncherSettings(_ReadMemory(settings["allocatedMemory"]), _ReadJvmArgs(settings["jvmArgs"]));
    }

    private static int _ReadMemory(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;

        // 正常是整数；老版本 / 手改过的文件里见过字符串形式，一起兼容掉
        if (value.TryGetValue<int>(out var number)) return Math.Max(0, number);
        if (value.TryGetValue<double>(out var real)) return real > 0 ? (int)Math.Round(real) : 0;
        if (value.TryGetValue<string>(out var text) && int.TryParse(text.Trim(), out var parsed))
            return Math.Max(0, parsed);

        return 0;
    }

    private static string _ReadJvmArgs(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text.Trim();
            case JsonArray array:
                // 启动器里实际是「一行字符串」，但别假设它永远不改成数组
                return string.Join(' ', array
                    .Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            default:
                return string.Empty;
        }
    }
}
