using PCL.Core.Mcp.Analysis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp.Tools;

/// <summary>
/// 分析工具：实例体检、mod 冲突、崩溃定位。
/// </summary>
internal static class AnalysisTools
{
    public static void Register(McpServerCatalog catalog)
    {
        RegisterAnalyzeInstance(catalog);
        RegisterAnalyzeMods(catalog);
        RegisterAnalyzeCrash(catalog);
        RegisterFindMod(catalog);
    }

    #region 实例体检

    private static void RegisterAnalyzeInstance(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_analyze_instance",
            Title = "实例体检",
            Description =
                "对实例做一次完整体检，回答「这个实例现在能不能正常启动」。\n\n"
                + "检查项：\n"
                + "- 同一个 mod 装了多份（必然崩）\n"
                + "- 加载器混装（Fabric 实例里放了 Forge mod）\n"
                + "- 前置 mod 缺失（从每个 jar 的依赖声明反推）\n"
                + "- mod 声明的 MC 版本与实例不符\n"
                + "- Java 版本是否满足 MC 版本要求\n"
                + "- 内存分配是否与 mod 数量匹配\n\n"
                + "返回里的 issues 已按严重程度排序（error → warning → info），每条都带 suggestion。"
                + "summary 是一句话结论，可以直接转述给用户。\n\n"
                + "这是排查「游戏起不来 / 起起来就崩」的第一个工具，比翻日志快得多。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);

                var analysis = InstanceAnalyzer.Analyze(location!);
                return Task.FromResult<object?>(ToDictionary(analysis));
            }
        });
    }

    private static Dictionary<string, object?> ToDictionary(InstanceAnalysis analysis) => new()
    {
        ["ok"] = true,
        ["summary"] = analysis.Summary,
        ["instance"] = analysis.Instance,
        ["instanceDirectory"] = analysis.InstanceDirectory,
        ["gameRoot"] = analysis.GameRoot,
        ["mcVersion"] = analysis.McVersion,
        ["loader"] = analysis.Loader,
        ["loaderVersion"] = analysis.LoaderVersion,
        ["javaPath"] = analysis.JavaPath,
        ["ramMb"] = analysis.RamMb,
        ["stats"] = new Dictionary<string, object?>
        {
            ["modTotal"] = analysis.ModTotal,
            ["modEnabled"] = analysis.ModEnabled,
            ["modDisabled"] = analysis.ModDisabled,
            ["modSize"] = InstanceAnalyzer.FormatSize(analysis.TotalModBytes),
            ["resourcePacks"] = analysis.ResourcePackCount,
            ["saves"] = analysis.SaveCount,
            ["loaderHistogram"] = analysis.LoaderHistogram
        },
        ["errorCount"] = analysis.Issues.Count(i => i.Severity == "error"),
        ["warningCount"] = analysis.Issues.Count(i => i.Severity == "warning"),
        ["issues"] = analysis.Issues.Select(i => new Dictionary<string, object?>
        {
            ["severity"] = i.Severity,
            ["code"] = i.Code,
            ["title"] = i.Title,
            ["detail"] = i.Detail,
            ["suggestion"] = i.Suggestion,
            ["files"] = i.Files
        }).ToList(),
        ["duplicates"] = analysis.Duplicates.Select(d => new Dictionary<string, object?>
        {
            ["modId"] = d.Identity,
            ["files"] = d.Files
        }).ToList()
    };

    #endregion

    #region mod 层面分析

    private static void RegisterAnalyzeMods(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_analyze_mods",
            Title = "mod 冲突与依赖分析",
            Description =
                "只看 mod 层面的问题，不检查 Java/内存这些环境项。\n\n"
                + "输出三块：\n"
                + "- duplicates：同一个 modId 出现多份，附全部文件名\n"
                + "- missingDependencies：哪些 mod 的哪个前置没装\n"
                + "- versionMismatch：哪些 mod 声明的 MC 版本与实例不符\n\n"
                + "和 pcl_analyze_instance 的区别：这个更聚焦，返回更短，适合在改完 mod 之后复查。"
                + "分析完可以直接按返回的文件名调 pcl_disable_mod / pcl_delete_mod。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Bool("includeDisabled", "是否把已禁用的 mod 也算进来，默认只看启用的。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var includeDisabled = args.Bool("includeDisabled");
                var all = ModDirectoryScanner.Scan(ModDirectoryScanner.Mods(loc), readMetadata: true);
                var mods = includeDisabled ? all : all.Where(m => m.Enabled).ToList();

                var duplicates = mods
                    .Where(m => !string.IsNullOrWhiteSpace(m.Identity))
                    .GroupBy(m => m.Identity, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => new Dictionary<string, object?>
                    {
                        ["modId"] = g.Key,
                        ["files"] = g.Select(m => m.FileName).ToList(),
                        ["versions"] = g.Select(m => m.Metadata?.Version ?? "").Where(v => v.Length > 0).ToList()
                    })
                    .ToList();

                var present = mods.Select(m => m.Identity)
                    .Where(id => id.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var missing = new List<Dictionary<string, object?>>();
                foreach (var mod in mods)
                {
                    if (mod.Metadata is null) continue;
                    foreach (var dependency in mod.Metadata.Depends)
                    {
                        var id = dependency.Split(' ', 2)[0].Trim();
                        if (id.Length == 0) continue;
                        if (IsBuiltinDependency(id)) continue;
                        if (present.Contains(id)) continue;
                        if (string.Equals(id, mod.Identity, StringComparison.OrdinalIgnoreCase)) continue;

                        missing.Add(new Dictionary<string, object?>
                        {
                            ["dependency"] = id,
                            ["requiredBy"] = mod.Identity,
                            ["file"] = mod.FileName
                        });
                    }
                }

                var mismatched = mods
                    .Where(m => m.Metadata is not null
                                && m.Metadata.McVersion.Length > 0
                                && loc.McVersion.Length > 0
                                && !InstanceAnalyzer.VersionRangeMatches(m.Metadata.McVersion, loc.McVersion))
                    .Select(m => new Dictionary<string, object?>
                    {
                        ["file"] = m.FileName,
                        ["modId"] = m.Metadata!.ModId,
                        ["declared"] = m.Metadata.McVersion,
                        ["instanceVersion"] = loc.McVersion
                    })
                    .ToList();

                var loaderMismatch = mods
                    .Where(m => m.Metadata is not null
                                && m.Metadata.Loader is not ("Unknown" or "Vanilla" or "ResourcePack" or "Shader")
                                && loc.Loader.Length > 0
                                && !LoaderCompatible(loc.Loader, m.Metadata.Loader))
                    .Select(m => new Dictionary<string, object?>
                    {
                        ["file"] = m.FileName,
                        ["loader"] = m.Metadata!.Loader,
                        ["instanceLoader"] = loc.Loader
                    })
                    .ToList();

                var unreadable = all.Where(m => m.Metadata is null).Select(m => m.FileName).ToList();

                var totalIssues = duplicates.Count + missing.Count + mismatched.Count + loaderMismatch.Count;
                var summary = totalIssues == 0
                    ? $"扫了 {mods.Count} 个 mod，没有发现重复、缺失依赖或版本不匹配。"
                    : $"发现 {duplicates.Count} 组重复、{missing.Count} 条缺失依赖、"
                      + $"{mismatched.Count} 个版本不符、{loaderMismatch.Count} 个加载器不符。";

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("summary", summary),
                    ("modCount", mods.Count),
                    ("duplicates", duplicates),
                    ("missingDependencies", missing),
                    ("versionMismatch", mismatched),
                    ("loaderMismatch", loaderMismatch),
                    ("unreadableJars", unreadable),
                    ("note", unreadable.Count > 0
                        ? "unreadableJars 里的文件没解析出元数据，可能是加密过的 jar、或者根本不是 mod。"
                        : null)));
            }
        });
    }

    #endregion

    #region 崩溃分析

    private static void RegisterAnalyzeCrash(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_analyze_crash",
            Title = "分析崩溃日志",
            Description =
                "读最近一次崩溃报告（优先 crash-reports，其次 logs/latest.log），按已知模式给出结论。\n\n"
                + "能识别：mod 重复、前置缺失、类/方法找不到（版本不匹配）、内存不足、"
                + "显卡驱动问题、本地库不匹配、Mixin 冲突、版本 jar 损坏、栈溢出保护终止。\n\n"
                + "返回里的 hints 按严重程度排序，每条带 suggestion；suspectMods 是从堆栈里猜出来的可疑 mod 包名，"
                + "可以作为二分排查的起点。tail 是日志最后 40 行，匹配不到已知模式时用它兜底。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Str("file", "直接指定一个日志文件路径（crash-report 或 latest.log）。传了就忽略 instance。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var explicitFile = args.Str("file");
                CrashReport? report;

                if (!string.IsNullOrWhiteSpace(explicitFile))
                {
                    if (!File.Exists(explicitFile))
                        return Task.FromResult<object?>(McpResults.Error($"文件不存在：{explicitFile}"));
                    report = CrashLogAnalyzer.AnalyzeFile(explicitFile, Path.GetFileName(explicitFile));
                }
                else
                {
                    var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                    if (error is not null) return Task.FromResult<object?>(error);
                    report = CrashLogAnalyzer.AnalyzeInstance(location!);

                    if (report is null)
                        return Task.FromResult<object?>(McpResults.Error(
                            "这个实例下没有找到崩溃报告，也没有 latest.log。",
                            "可能游戏还没启动过。先 pcl_launch_game，然后再来分析。"));
                }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("summary", report.Summary),
                    ("source", report.Source),
                    ("sourcePath", report.SourcePath),
                    ("description", report.Description),
                    ("exceptionType", report.ExceptionType),
                    ("exceptionMessage", report.ExceptionMessage),
                    ("causes", report.Causes),
                    ("suspectMods", report.SuspectMods),
                    ("stackHighlights", report.StackHighlights),
                    ("hints", report.Hints.Select(h => new Dictionary<string, object?>
                    {
                        ["severity"] = h.Severity,
                        ["title"] = h.Title,
                        ["detail"] = h.Detail,
                        ["suggestion"] = h.Suggestion
                    }).ToList()),
                    ("tail", report.Tail)));
            }
        });
    }

    #endregion

    #region 搜索

    private static void RegisterFindMod(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_find_mod",
            Title = "搜索 mod",
            Description =
                "按关键词在实例的 mod 里搜索，匹配文件名、modId、显示名、描述。\n\n"
                + "回答「我装没装 XX」「哪个 mod 提供了 YY 功能」这类问题时用。\n"
                + "只搜文件名和元数据，不读 mod 里的代码。",
            InputSchema = new McpParams()
                .Str("query", "关键词，不区分大小写。", required: true)
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Bool("includeDisabled", "是否搜已禁用的 mod，默认搜。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var query = args.Str("query", "").Trim();
                if (query.Length == 0)
                    return Task.FromResult<object?>(McpResults.Error("缺少 query 参数。"));

                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var includeDisabled = args.Bool("includeDisabled", true);
                var mods = ModDirectoryScanner.Scan(ModDirectoryScanner.Mods(loc), readMetadata: true);
                if (!includeDisabled) mods = mods.Where(m => m.Enabled).ToList();

                var hits = mods.Where(m =>
                        m.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (m.Metadata?.ModId.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (m.Metadata?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (m.Metadata?.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Select(m => new Dictionary<string, object?>
                    {
                        ["fileName"] = m.FileName,
                        ["enabled"] = m.Enabled,
                        ["modId"] = m.Metadata?.ModId ?? "",
                        ["name"] = m.Metadata?.Name ?? "",
                        ["version"] = m.Metadata?.Version ?? "",
                        ["description"] = Truncate(m.Metadata?.Description ?? "", 160)
                    })
                    .ToList();

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("query", query),
                    ("count", hits.Count),
                    ("matches", hits)));
            }
        });
    }

    #endregion

    #region 辅助

    private static readonly HashSet<string> Builtin = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "forge", "neoforge", "fabricloader", "fabric-loader", "fabric",
        "quilt_loader", "quilt", "liteloader", "optifine", "mcp", "javafml", "lowcodefml",
        "mixinextras", "mixinextras-fabric", "modmenu"
    };

    private static bool IsBuiltinDependency(string id) => Builtin.Contains(id);

    private static bool LoaderCompatible(string instanceLoader, string modLoader)
    {
        if (instanceLoader.Contains("Quilt", StringComparison.OrdinalIgnoreCase)
            && modLoader.Equals("Fabric", StringComparison.OrdinalIgnoreCase)) return true;
        return instanceLoader.Contains(modLoader, StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    #endregion
}
