using System.IO;
using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp.Analysis;

/// <summary>体检报告里的一条问题。</summary>
public sealed record AnalysisIssue
{
    /// <summary>error / warning / info</summary>
    public required string Severity { get; init; }

    /// <summary>稳定的问题代码，便于程序判断，如 <c>duplicate-mod</c>。</summary>
    public required string Code { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public IReadOnlyList<string> Files { get; init; } = [];

    public string? Suggestion { get; init; }
}

/// <summary>同一个 mod 出现多份。</summary>
public sealed record DuplicateGroup(string Identity, IReadOnlyList<string> Files, bool HasEnabled);

/// <summary>实例体检结果。</summary>
public sealed record InstanceAnalysis
{
    public required string Instance { get; init; }
    public string InstanceDirectory { get; init; } = "";
    public string GameRoot { get; init; } = "";
    public string McVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string JavaPath { get; init; } = "";
    public int RamMb { get; init; }
    public int ModTotal { get; init; }
    public int ModEnabled { get; init; }
    public int ModDisabled { get; init; }
    public int ResourcePackCount { get; init; }
    public int SaveCount { get; init; }
    public long TotalModBytes { get; init; }
    public IReadOnlyList<AnalysisIssue> Issues { get; init; } = [];
    public IReadOnlyList<DuplicateGroup> Duplicates { get; init; } = [];

    /// <summary>实际扫到的加载器分布，用来发现「Fabric 实例里混进了 Forge mod」。</summary>
    public IReadOnlyDictionary<string, int> LoaderHistogram { get; init; } =
        new Dictionary<string, int>();

    /// <summary>一句话结论，直接给模型当摘要用。</summary>
    public string Summary { get; init; } = "";
}

/// <summary>
/// 实例体检。
/// <para>
/// 目标不是「把所有细节都列出来」，而是回答一个具体问题：这个实例现在能不能正常启动，
/// 如果不行最可能是哪里坏了。所以每条 issue 都带 severity 和可执行的建议。
/// </para>
/// </summary>
public static class InstanceAnalyzer
{
    /// <summary>加载器自带、不需要额外装 jar 的依赖名。</summary>
    private static readonly HashSet<string> BuiltinDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "forge", "neoforge", "fabricloader", "fabric-loader", "fabric",
        "quilt_loader", "quilt", "liteloader", "optifine", "mcp", "javafml", "lowcodefml",
        "mixinextras", "mixinextras-fabric", "modmenu"
    };

    public static InstanceAnalysis Analyze(InstanceLocation location)
    {
        var modsDirectory = ModDirectoryScanner.Mods(location);
        var mods = ModDirectoryScanner.Scan(modsDirectory, readMetadata: true);

        var issues = new List<AnalysisIssue>();
        var duplicates = new List<DuplicateGroup>();

        var enabledMods = mods.Where(m => m.Enabled).ToList();
        var disabledMods = mods.Where(m => !m.Enabled).ToList();

        // ---- 1. 目录存在性 ----
        if (!Directory.Exists(modsDirectory))
            issues.Add(new AnalysisIssue
            {
                Severity = "info",
                Code = "mods-directory-missing",
                Title = "没有 mods 目录",
                Detail = $"该实例下不存在 {modsDirectory}，说明它是纯原版实例，或者 mod 装在别的实例目录里。",
                Suggestion = "如果这个实例本该有 mod，检查「版本隔离」设置——开了隔离时 mods 在实例目录内，没开时在 .minecraft 根目录下。"
            });

        // ---- 2. 重复 mod ----
        foreach (var group in enabledMods.GroupBy(m => m.Identity, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() <= 1) continue;
            duplicates.Add(new DuplicateGroup(
                group.Key,
                group.Select(m => m.FileName).ToList(),
                true));
            issues.Add(new AnalysisIssue
            {
                Severity = "error",
                Code = "duplicate-mod",
                Title = $"mod「{group.Key}」存在 {group.Count()} 份，游戏一定会崩",
                Detail = "同一个 modId 被加载两次会抛 DuplicateModsFoundException（Fabric）"
                         + "或直接导致 mod 注册表冲突（Forge）。",
                Files = group.Select(m => m.FileName).ToList(),
                Suggestion = "只保留其中一份：通常是版本号更高的那个。其余用 pcl_disable_mod 或 pcl_delete_mod 处理。"
            });
        }

        // ---- 3. 加载器混装 ----
        var histogram = enabledMods
            .Where(m => m.Metadata is not null && m.Metadata.Loader is not ("Unknown" or "Vanilla"))
            .GroupBy(m => m.Metadata!.Loader)
            .ToDictionary(g => g.Key, g => g.Count());

        var instanceLoader = location.Loader;
        if (histogram.Count > 0 && instanceLoader.Length > 0)
        {
            var expected = NormalizeLoader(instanceLoader);
            var foreign = histogram
                .Where(kv => kv.Key != expected && !IsLoaderCompatible(expected, kv.Key))
                .ToList();
            if (foreign.Count > 0)
                issues.Add(new AnalysisIssue
                {
                    Severity = "error",
                    Code = "loader-mismatch",
                    Title = $"实例是 {instanceLoader}，但装了 {string.Join("、", foreign.Select(f => $"{f.Value} 个 {f.Key} mod"))}",
                    Detail = "不同加载器的 mod 互不兼容，游戏会在 mod 加载阶段直接报错退出。",
                    Files = enabledMods
                        .Where(m => m.Metadata is not null
                                    && foreign.Any(f => f.Key == m.Metadata.Loader))
                        .Select(m => m.FileName)
                        .Take(20)
                        .ToList(),
                    Suggestion = "把不匹配的 mod 禁用掉，或者换一个对应加载器的版本重新下载。"
                });
        }

        // ---- 4. 依赖缺失 ----
        var presentIds = enabledMods
            .Select(m => m.Identity)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new List<(string Dependent, string Dependency)>();
        foreach (var mod in enabledMods)
        {
            if (mod.Metadata is null) continue;
            foreach (var dependency in mod.Metadata.Depends)
            {
                var id = dependency.Split(' ', 2)[0].Trim();
                if (id.Length == 0) continue;
                if (BuiltinDependencies.Contains(id)) continue;
                if (presentIds.Contains(id)) continue;
                // 自己依赖自己（部分 mod 会写）忽略
                if (string.Equals(id, mod.Identity, StringComparison.OrdinalIgnoreCase)) continue;
                missing.Add((mod.Identity, id));
            }
        }

        foreach (var group in missing.GroupBy(x => x.Dependency, StringComparer.OrdinalIgnoreCase))
        {
            var dependents = group.Select(x => x.Dependent).Distinct().ToList();
            issues.Add(new AnalysisIssue
            {
                Severity = "warning",
                Code = "missing-dependency",
                Title = $"缺少依赖「{group.Key}」，有 {dependents.Count} 个 mod 需要它",
                Detail = $"依赖它的 mod：{string.Join("、", dependents.Take(10))}",
                Suggestion = $"在下载页搜索 {group.Key} 并装到同一个实例。"
                             + "装好后再跑一次体检确认。"
            });
        }

        // ---- 5. MC 版本不匹配 ----
        var versionMismatches = new List<string>();
        foreach (var mod in enabledMods)
        {
            var declared = mod.Metadata?.McVersion ?? "";
            if (declared.Length == 0) continue;
            if (location.McVersion.Length == 0) continue;
            if (VersionRangeMatches(declared, location.McVersion)) continue;
            versionMismatches.Add($"{mod.FileName}（声明 {declared}）");
        }

        if (versionMismatches.Count > 0)
            issues.Add(new AnalysisIssue
            {
                Severity = "warning",
                Code = "mc-version-mismatch",
                Title = $"{versionMismatches.Count} 个 mod 声明的 MC 版本与实例（{location.McVersion}）不符",
                Detail = "版本不符的 mod 可能加载失败，也可能加载成功但在运行时随机报错，"
                         + "这类问题最难查，建议先排除。",
                Files = versionMismatches.Take(20).ToList(),
                Suggestion = "确认这些 mod 是否真的有对应版本；没有的话先禁用。"
            });

        // ---- 6. 资源占用建议 ----
        var totalBytes = mods.Sum(m => m.SizeBytes);
        if (enabledMods.Count >= 120 && location.RamMb is > 0 and < 6144)
            issues.Add(new AnalysisIssue
            {
                Severity = "info",
                Code = "ram-might-be-low",
                Title = $"启用了 {enabledMods.Count} 个 mod，但只分配了 {location.RamMb} MB 内存",
                Detail = "大型整合包在 120 个 mod 以上时，4 GB 以下很容易在生成世界或切换维度时 OOM。",
                Suggestion = "把内存调到 6144~8192 MB 试试；如果本来就有 16 GB 以上物理内存，8 GB 更稳。"
            });

        if (totalBytes > 3L * 1024 * 1024 * 1024)
            issues.Add(new AnalysisIssue
            {
                Severity = "info",
                Code = "mods-are-huge",
                Title = $"mods 目录占用 {FormatSize(totalBytes)}",
                Detail = "体积过大通常是整合包自带的资源类 mod（材质、音效、结构）导致的，启动时会明显变慢。",
                Suggestion = "确认这些大体积 mod 都是必需的；不需要的可以禁用。"
            });

        // ---- 7. 禁用项过多 ----
        if (disabledMods.Count > 0 && disabledMods.Count >= enabledMods.Count)
            issues.Add(new AnalysisIssue
            {
                Severity = "info",
                Code = "many-disabled-mods",
                Title = $"有 {disabledMods.Count} 个 mod 处于禁用状态，比启用的还多",
                Detail = "长期禁用的 mod 会一直占磁盘，也容易在下次整理时搞混版本。",
                Suggestion = "确认不需要的可以直接删除；只是暂时排查的留着无妨。"
            });

        // ---- 8. Java 版本 ----
        var javaIssue = CheckJava(location);
        if (javaIssue is not null) issues.Add(javaIssue);

        var order = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 1, ["info"] = 2 };
        var sortedIssues = issues.OrderBy(i => order.GetValueOrDefault(i.Severity, 3)).ToList();

        var errorCount = sortedIssues.Count(i => i.Severity == "error");
        var warningCount = sortedIssues.Count(i => i.Severity == "warning");
        var summary = errorCount > 0
            ? $"发现 {errorCount} 个会导致启动失败的问题，{warningCount} 个警告。先处理 error 级的。"
            : warningCount > 0
                ? $"没有致命问题，但有 {warningCount} 个警告需要留意。"
                : "没发现明显问题，实例结构看起来是健康的。";

        return new InstanceAnalysis
        {
            Instance = location.Name,
            InstanceDirectory = location.InstanceDirectory,
            GameRoot = location.GameRoot,
            McVersion = location.McVersion,
            Loader = location.Loader,
            LoaderVersion = location.LoaderVersion,
            JavaPath = location.JavaPath,
            RamMb = location.RamMb,
            ModTotal = mods.Count,
            ModEnabled = enabledMods.Count,
            ModDisabled = disabledMods.Count,
            ResourcePackCount = ModDirectoryScanner.ScanEntries(ModDirectoryScanner.ResourcePacks(location)).Count,
            SaveCount = ModDirectoryScanner.ScanEntries(ModDirectoryScanner.Saves(location)).Count(x => x.IsDirectory),
            TotalModBytes = totalBytes,
            Issues = sortedIssues,
            Duplicates = duplicates,
            LoaderHistogram = histogram,
            Summary = summary
        };
    }

    private static AnalysisIssue? CheckJava(InstanceLocation location)
    {
        if (string.IsNullOrWhiteSpace(location.JavaPath)) return null;
        if (location.McVersion.Length == 0) return null;

        var recommended = RecommendedJavaMajor(location.McVersion);
        if (recommended == 0) return null;

        var actual = ParseJavaMajorFromPath(location.JavaPath);
        if (actual == 0) return null;

        if (actual >= recommended) return null;

        return new AnalysisIssue
        {
            Severity = "error",
            Code = "java-too-old",
            Title = $"Java 版本过低：实例用的是 Java {actual}，MC {location.McVersion} 至少需要 Java {recommended}",
            Detail = $"当前 Java 路径：{location.JavaPath}",
            Suggestion = $"在「设置 → Java」里换一个 Java {recommended} 或更高的运行时。"
        };
    }

    /// <summary>按 MC 版本推断最低 Java 大版本。</summary>
    public static int RecommendedJavaMajor(string mcVersion)
    {
        var parts = mcVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || major != 1) return 0;
        if (!int.TryParse(parts[1], out var minor)) return 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;

        // 1.20.5 起要求 Java 21；1.21 及以后同样。
        // 原来的写法是 minor >= 20 && patch >= 5，导致 1.21 / 1.21.4 落到下面返回 17，
        // 建议值本身就是错的。
        if (minor >= 21) return 21;
        if (minor == 20 && patch >= 5) return 21;
        if (minor >= 18) return 17;
        if (minor == 17) return 16;
        return 8;
    }

    /// <summary>从 Java 路径里猜大版本，猜不到返回 0。</summary>
    public static int ParseJavaMajorFromPath(string javaPath)
    {
        if (string.IsNullOrWhiteSpace(javaPath)) return 0;

        // 老版命名是 1.8 这种：jre1.8.0_51、jdk1.7.0_80。
        // 先处理它，否则下面那条正则会抓到 "jre1" 里的 1，落到 5..30 之外直接返回 0，
        // 于是「Java 8 跑 1.20」这种最该报的问题被整段跳过。
        var legacy = Regex.Match(javaPath, @"(?:jdk|jre|java)[-_]?1\.(\d+)", RegexOptions.IgnoreCase);
        if (legacy.Success && int.TryParse(legacy.Groups[1].Value, out var legacyMinor) && legacyMinor <= 8)
            return legacyMinor == 1 ? 1 : legacyMinor;

        var match = Regex.Match(javaPath, @"(?:jdk|jre|java)[-_]?(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var major))
            return major is >= 5 and <= 30 ? major : 0;

        // jdk-21.0.10.7-hotspot 这种
        var alt = Regex.Match(javaPath, @"\b(1[789]|2[0-9])\b");
        return alt.Success && int.TryParse(alt.Groups[1].Value, out var v) ? v : 0;
    }

    private static string NormalizeLoader(string loader)
    {
        if (loader.Contains("NeoForge", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
        if (loader.Contains("Forge", StringComparison.OrdinalIgnoreCase)) return "Forge";
        if (loader.Contains("Quilt", StringComparison.OrdinalIgnoreCase)) return "Quilt";
        if (loader.Contains("Fabric", StringComparison.OrdinalIgnoreCase)) return "Fabric";
        if (loader.Contains("LiteLoader", StringComparison.OrdinalIgnoreCase)) return "LiteLoader";
        return loader;
    }

    private static bool IsLoaderCompatible(string instanceLoader, string modLoader)
    {
        // Quilt 能跑 Fabric mod
        if (instanceLoader == "Quilt" && modLoader == "Fabric") return true;
        // 资源包/光影不是加载器
        return modLoader is "ResourcePack" or "Shader" or "Vanilla";
    }

    /// <summary>
    /// 判断一个 mod 声明的 MC 版本范围是否覆盖实例版本。
    /// 只做「能确定的判断」：范围里出现的 1.x.y 与实例版本精确比对，
    /// 出现 1.x 这种两段式就按前缀比。拿不准时一律认为匹配，避免误报。
    /// </summary>
    public static bool VersionRangeMatches(string declared, string actual)
    {
        if (declared.Length == 0 || actual.Length == 0) return true;

        var versions = Regex.Matches(declared, @"\b1\.\d{1,2}(?:\.\d{1,2})?\b")
            .Select(m => m.Value)
            .ToList();
        if (versions.Count == 0) return true;

        foreach (var version in versions)
        {
            if (string.Equals(version, actual, StringComparison.OrdinalIgnoreCase)) return true;
            // 两段式：1.20 匹配 1.20.1
            if (version.Count(c => c == '.') == 1 && actual.StartsWith(version + ".", StringComparison.OrdinalIgnoreCase))
                return true;
            // 三段式：1.20.1 与 1.20 也认为兼容（部分 mod 会写全版本）
            if (actual.Count(c => c == '.') == 1 && version.StartsWith(actual + ".", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:0.##} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };
}
