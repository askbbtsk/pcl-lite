using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.App.Configuration;

namespace PCL.Core.App.Lunar;

/// <summary>
///     某一项设置是从哪儿来的。界面和日志都要说明白，否则用户改了没生效也不知道为什么。
/// </summary>
public enum LunarSettingSource
{
    /// <summary>代码里的默认值。</summary>
    Default,

    /// <summary>跟随 PCL 的全局内存设置。</summary>
    PclGlobal,

    /// <summary>官方 Lunar 启动器自己的 <c>settings/launcher.json</c>。</summary>
    LunarLauncher,

    /// <summary>Lunar 分组里的全局设置。</summary>
    LunarGlobal,

    /// <summary>这个版本单独的覆盖值。</summary>
    VersionOverride
}

/// <summary>
///     单个版本的覆盖值。<see cref="Enabled" /> 为 <see langword="false" /> 时整条忽略。
/// </summary>
/// <param name="Enabled">是否启用这个版本的覆盖。</param>
/// <param name="MemoryMb">堆上限（MB）。<c>0</c> 表示沿用全局。</param>
/// <param name="JvmArgs">附加 JVM 参数。空白表示沿用全局。</param>
public sealed record LunarVersionOverride(bool Enabled, int MemoryMb, string JvmArgs)
{
    public static readonly LunarVersionOverride Disabled = new(false, 0, string.Empty);
}

/// <summary>
///     一次启动实际生效的设置。
/// </summary>
/// <param name="MemoryMb">最终堆上限（MB），已经夹到合法区间。</param>
/// <param name="JvmArguments">切好的附加 JVM 参数，每项一个元素。</param>
/// <param name="JavaPathOverride">覆盖用的 <c>java.exe</c>；<see langword="null" /> 表示用 Lunar 自带的。</param>
/// <param name="MemorySource">堆上限的来源。</param>
/// <param name="JvmSource">JVM 参数的来源。</param>
public sealed record LunarEffectiveSettings(
    int MemoryMb,
    IReadOnlyList<string> JvmArguments,
    string? JavaPathOverride,
    LunarSettingSource MemorySource,
    LunarSettingSource JvmSource);

/// <summary>
///     Lunar 启动设置的解析与存储。
///     <para>
///         Lunar 的 JRE 由服务端按版本下发（1.8.9 要 17、1.21 要 21、26.x 要 25），
///         内存需求跟原版实例不是一回事，所以这套设置独立于 <c>Config.Launch</c>。
///         但默认仍然跟随 PCL 的全局内存，免得用户以为改了全局却没作用。
///     </para>
///     <para>
///         分层顺序：版本覆盖 → Lunar 全局 → 官方 Lunar 启动器 → PCL 全局 → 内置默认。
///         官方启动器那一层是 1.6.2 加的：两边共用 <c>~/.lunarclient</c> 时，
///         用户在 Lunar 启动器里调过的内存和 JVM 参数不该被 PCL 的默认值盖掉。
///     </para>
/// </summary>
public static class LunarLaunchProfile
{
    /// <summary>兜底堆上限。Lunar 官方启动器默认给的就是 3G。</summary>
    public const int DefaultMemoryMb = 3072;

    /// <summary>
    ///     允许的最小堆。和设置页校验器的下限保持一致 —— 两边不一致的话，
    ///     用户填 512 会看到界面上写着 512、实际启动却是 1024，说不清谁对。
    /// </summary>
    public const int MinMemoryMb = 512;

    /// <summary>再大也没有意义，防止手滑输个天文数字把 JVM 干崩。</summary>
    public const int MaxMemoryMb = 131072;

    private const string KeyEnabled = "on";
    private const string KeyMemory = "mem";
    private const string KeyJvm = "jvm";

    #region 版本覆盖的存取

    /// <summary>
    ///     读出某个版本的覆盖值。没有、格式坏了、版本号对不上，一律当「未启用」。
    /// </summary>
    public static LunarVersionOverride GetOverride(string? subVersion)
    {
        if (string.IsNullOrWhiteSpace(subVersion))
            return LunarVersionOverride.Disabled;
        if (!_ReadRoot().TryGetPropertyValue(subVersion, out var node) || node is not JsonObject item)
            return LunarVersionOverride.Disabled;

        try
        {
            return new LunarVersionOverride(
                item[KeyEnabled]?.GetValue<bool>() ?? false,
                item[KeyMemory]?.GetValue<int>() ?? 0,
                item[KeyJvm]?.GetValue<string>() ?? string.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // 值类型被人手改坏了就当没配，别让整个启动流程跟着挂
            return LunarVersionOverride.Disabled;
        }
    }

    /// <summary>
    ///     写入某个版本的覆盖值。全部为默认（未启用 / 0 / 空）时直接删掉这一条，免得越积越多。
    /// </summary>
    public static void SetOverride(string subVersion, bool enabled, int memoryMb, string jvmArgs)
    {
        if (string.IsNullOrWhiteSpace(subVersion))
            return;

        var root = _ReadRoot();
        if (!enabled && memoryMb <= 0 && string.IsNullOrWhiteSpace(jvmArgs))
        {
            root.Remove(subVersion);
        }
        else
        {
            root[subVersion] = new JsonObject
            {
                [KeyEnabled] = enabled,
                [KeyMemory] = Math.Max(0, memoryMb),
                [KeyJvm] = jvmArgs ?? string.Empty
            };
        }

        _WriteRoot(root);
    }

    /// <summary>
    ///     删掉某个版本的覆盖。
    /// </summary>
    public static void ClearOverride(string subVersion)
    {
        var root = _ReadRoot();
        if (root.Remove(subVersion)) _WriteRoot(root);
    }

    /// <summary>
    ///     已经配了覆盖的版本号，供界面显示「已覆盖」标记。
    /// </summary>
    public static IReadOnlyList<string> ListOverriddenVersions()
    {
        var result = new List<string>();
        foreach (var (key, node) in _ReadRoot())
        {
            if (node is not JsonObject item) continue;
            try
            {
                if (item[KeyEnabled]?.GetValue<bool>() ?? false) result.Add(key);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                // 同上，坏数据跳过
            }
        }

        return result;
    }

    private static JsonObject _ReadRoot()
    {
        var raw = Config.Preference.Lunar.VersionOverrides;
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        try
        {
            return JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static void _WriteRoot(JsonObject root)
    {
        Config.Preference.Lunar.VersionOverrides =
            root.Count == 0 ? string.Empty : root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    #endregion

    #region 解析

    /// <summary>
    ///     解析出这次启动实际要用的设置。
    /// </summary>
    /// <param name="subVersion">目标版本，例如 <c>1.8.9</c>。</param>
    /// <param name="pclGlobalMemoryMb">
    ///     PCL 全局内存设置换算出来的 MB 值。传 <c>0</c> 表示算不出来，直接用内置默认。
    /// </param>
    /// <param name="lunarLauncher">
    ///     官方 Lunar 启动器的设置。<see langword="null" /> 表示现读一次磁盘。
    ///     调用方已经读过就传进来，省一次 IO。
    /// </param>
    public static LunarEffectiveSettings Resolve(string? subVersion, int pclGlobalMemoryMb,
        LunarLauncherSettings? lunarLauncher = null)
    {
        var lunarConfig = Config.Preference.Lunar;
        var native = lunarLauncher ?? LunarLauncherConfig.Read(LunarClientService.SettingsRoot);

        // 内存：Lunar 全局 > 官方启动器 > PCL 全局 > 内置默认
        //
        // 官方启动器排在 PCL 全局前面，是因为两边共用 ~/.lunarclient 时，用户在
        // Lunar 启动器里调过的内存才是他真正的意图。以前不看这个文件，
        // 就变成了「Lunar 里设的 8G，换 PCL 启动只有 3G」。
        int memory;
        LunarSettingSource memorySource;
        if (lunarConfig.MemoryMb > 0)
        {
            memory = ClampMemory(lunarConfig.MemoryMb);
            memorySource = LunarSettingSource.LunarGlobal;
        }
        else if (native.HasMemory)
        {
            memory = ClampMemory(native.AllocatedMemoryMb);
            memorySource = LunarSettingSource.LunarLauncher;
        }
        else if (pclGlobalMemoryMb > 0)
        {
            memory = ClampMemory(pclGlobalMemoryMb);
            memorySource = LunarSettingSource.PclGlobal;
        }
        else
        {
            memory = DefaultMemoryMb;
            memorySource = LunarSettingSource.Default;
        }

        // JVM 参数同理：PCL 侧没填就沿用官方启动器里那一行
        var jvmRaw = lunarConfig.JvmArgs;
        var jvmSource = string.IsNullOrWhiteSpace(jvmRaw)
            ? LunarSettingSource.Default
            : LunarSettingSource.LunarGlobal;
        if (string.IsNullOrWhiteSpace(jvmRaw) && native.HasJvmArgs)
        {
            jvmRaw = native.JvmArgs;
            jvmSource = LunarSettingSource.LunarLauncher;
        }

        // 版本覆盖：只盖它明确写了的字段
        var over = GetOverride(subVersion);
        if (over.Enabled)
        {
            if (over.MemoryMb > 0)
            {
                memory = ClampMemory(over.MemoryMb);
                memorySource = LunarSettingSource.VersionOverride;
            }

            if (!string.IsNullOrWhiteSpace(over.JvmArgs))
            {
                jvmRaw = over.JvmArgs;
                jvmSource = LunarSettingSource.VersionOverride;
            }
        }

        return new LunarEffectiveSettings(
            memory,
            SplitArguments(jvmRaw),
            NormalizeJavaPath(lunarConfig.JavaPath),
            memorySource,
            jvmSource);
    }

    /// <summary>
    ///     把堆上限夹到 <see cref="MinMemoryMb" />～<see cref="MaxMemoryMb" /> 之间。
    /// </summary>
    public static int ClampMemory(int memoryMb)
    {
        if (memoryMb <= 0) return DefaultMemoryMb;
        return Math.Clamp(memoryMb, MinMemoryMb, MaxMemoryMb);
    }

    /// <summary>
    ///     只有文件确实存在才认这个覆盖值，写错了就退回 Lunar 自带的 JRE。
    /// </summary>
    public static string? NormalizeJavaPath(string? javaPath)
    {
        if (string.IsNullOrWhiteSpace(javaPath)) return null;
        var trimmed = javaPath.Trim().Trim('"');
        return File.Exists(trimmed) ? trimmed : null;
    }

    /// <summary>
    ///     把一行参数切成独立的元素。
    ///     <para>
    ///         必须切：Lunar 那边是往 <c>ProcessStartInfo.ArgumentList</c> 里逐项塞的，
    ///         整串塞进去会被当成一个参数，JVM 直接报 <c>Unrecognized option</c>。
    ///         引号内的空格不切，<c>\"</c> 视为字面引号。
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inQuotes)
            {
                if (c == '\\' && i + 1 < raw.Length && raw[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                hasContent = true; // 空引号 "" 也要算一个参数
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (hasContent || current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasContent = false;
                }

                continue;
            }

            current.Append(c);
        }

        if (hasContent || current.Length > 0) result.Add(current.ToString());
        return result;
    }

    #endregion

    #region 展示

    /// <summary>
    ///     把来源翻译成界面能直接显示的一句话。
    /// </summary>
    public static string DescribeSource(LunarSettingSource source) => source switch
    {
        LunarSettingSource.PclGlobal => "跟随 PCL 全局设置",
        LunarSettingSource.LunarLauncher => "Lunar 启动器的设置",
        LunarSettingSource.LunarGlobal => "Lunar 全局设置",
        LunarSettingSource.VersionOverride => "此版本的单独设置",
        _ => "内置默认"
    };

    #endregion
}
