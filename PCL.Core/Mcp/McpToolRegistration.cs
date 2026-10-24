using System.Text.Json;
using PCL.Core.Mcp.Analysis;
using PCL.Core.Mcp.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp;

/// <summary>
/// 把所有工具、资源、提示词装配成一个可用的目录。
/// </summary>
internal static class McpToolRegistration
{
    public static McpServerCatalog BuildCatalog()
    {
        var catalog = new McpServerCatalog();

        InstanceTools.Register(catalog);
        ModTools.Register(catalog);
        TrashTools.Register(catalog);
        GameFileTools.Register(catalog);
        GameTools.Register(catalog);
        AnalysisTools.Register(catalog);
        LauncherTools.Register(catalog);

        RegisterResources(catalog);
        RegisterPrompts(catalog);

        return catalog;
    }

    #region 资源

    private static void RegisterResources(McpServerCatalog catalog)
    {
        catalog.Resources.Add(new McpResourceDefinition
        {
            Uri = "pcl://instances",
            Name = "全部实例",
            Description = "启动器里所有 Minecraft 实例的清单，带 MC 版本、加载器、mod 数量。",
            Reader = _ =>
            {
                var names = McpHostBridge.ListInstances?.Invoke() ?? [];
                var items = new List<Dictionary<string, object?>>();
                foreach (var name in names)
                {
                    var location = McpHostBridge.LocateInstance?.Invoke(name);
                    if (location is null) continue;
                    var mods = ModDirectoryScanner.Scan(ModDirectoryScanner.Mods(location), readMetadata: false);
                    items.Add(new Dictionary<string, object?>
                    {
                        ["name"] = location.Name,
                        ["mcVersion"] = location.McVersion,
                        ["loader"] = location.Loader,
                        ["loaderVersion"] = location.LoaderVersion,
                        ["mods"] = mods.Count,
                        ["modsEnabled"] = mods.Count(m => m.Enabled),
                        ["path"] = location.InstanceDirectory
                    });
                }

                return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["current"] = McpHostBridge.GetSelectedInstance?.Invoke(),
                    ["count"] = items.Count,
                    ["instances"] = items
                }, ResourceJson));
            }
        });

        catalog.Resources.Add(new McpResourceDefinition
        {
            Uri = "pcl://instance/current",
            Name = "当前实例",
            Description = "启动器当前选中实例的详情与体检结果。",
            Reader = _ =>
            {
                var name = McpHostBridge.GetSelectedInstance?.Invoke();
                if (string.IsNullOrWhiteSpace(name))
                    return Task.FromResult("{\"error\":\"启动器当前没有选中实例\"}");

                var location = McpHostBridge.LocateInstance?.Invoke(name);
                if (location is null)
                    return Task.FromResult($"{{\"error\":\"找不到实例 {name}\"}}");

                var analysis = InstanceAnalyzer.Analyze(location);
                return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["instance"] = analysis.Instance,
                    ["mcVersion"] = analysis.McVersion,
                    ["loader"] = analysis.Loader,
                    ["loaderVersion"] = analysis.LoaderVersion,
                    ["mods"] = new Dictionary<string, object?>
                    {
                        ["total"] = analysis.ModTotal,
                        ["enabled"] = analysis.ModEnabled,
                        ["disabled"] = analysis.ModDisabled
                    },
                    ["summary"] = analysis.Summary,
                    ["issues"] = analysis.Issues.Select(i => new Dictionary<string, object?>
                    {
                        ["severity"] = i.Severity,
                        ["title"] = i.Title,
                        ["suggestion"] = i.Suggestion
                    }).ToList()
                }, ResourceJson));
            }
        });

        catalog.Resources.Add(new McpResourceDefinition
        {
            Uri = "pcl://mcp/status",
            Name = "MCP 服务状态",
            Description = "服务端口、权限开关、工具清单。",
            Reader = _ =>
            {
                var options = McpServerService.Snapshot();
                return Task.FromResult(JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["running"] = McpServerService.IsRunning,
                    ["endpoint"] = McpServerService.EndpointUrl(options),
                    ["authRequired"] = options.AuthRequired,
                    ["permissions"] = new Dictionary<string, object?>
                    {
                        ["write"] = options.AllowWrite,
                        ["gameControl"] = options.AllowGameControl,
                        ["launcherConfig"] = options.AllowLauncherConfig
                    },
                    ["tools"] = McpServerService.Catalog.Tools.Tools.Select(t => t.Name).OrderBy(n => n).ToList()
                }, ResourceJson));
            }
        });
    }

    private static readonly JsonSerializerOptions ResourceJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    #endregion

    #region 提示词

    private static void RegisterPrompts(McpServerCatalog catalog)
    {
        catalog.Prompts.Add(new McpPromptDefinition
        {
            Name = "analyze-instance",
            Title = "体检一个实例",
            Description = "按固定顺序检查实例健康度，并用人话解释每条问题的原因和修法。",
            Arguments =
            [
                new McpPromptArgument { Name = "instance", Description = "实例名，留空用当前选中实例", Required = false }
            ],
            Render = args => $"""
                帮我给一个 Minecraft 实例做体检。

                按这个顺序做，每一步都真的调用工具，不要凭印象回答：

                1. 如果没给实例名，先调 `pcl_list_instances`，从返回里挑 current 那个。
                2. 调 `pcl_analyze_instance`。
                3. 如果 errorCount 大于 0，重点讲 error 级的；否则讲 warning 级；都没有就说健康。
                4. 每条问题按「现象 → 原因 → 怎么修」三段说清楚，不要只复述工具返回的英文。
                5. 最后给一个按优先级排的下一步清单。

                实例：{args.GetValueOrDefault("instance") ?? "（当前选中）"}
                """
        });

        catalog.Prompts.Add(new McpPromptDefinition
        {
            Name = "fix-crash",
            Title = "定位崩溃原因",
            Description = "从崩溃报告出发，逐层排除，最后给出可执行的修复动作。",
            Arguments =
            [
                new McpPromptArgument { Name = "instance", Description = "实例名", Required = false },
                new McpPromptArgument { Name = "symptom", Description = "用户描述的现象，例如「点启动后闪退」", Required = false }
            ],
            Render = args => $"""
                游戏崩了，帮我定位原因。

                排查顺序（按这个来，不要跳步）：

                1. `pcl_analyze_crash` 拿崩溃报告，先看 hints 和 suspectMods。
                2. 如果第一条 hint 的 severity 是 error，直接按它的 suggestion 处理，不用继续往下查。
                3. 如果 hints 为空或只有 info，调 `pcl_analyze_instance` 看实例本身有没有结构性问题
                   （重复 mod、缺前置、Java 版本过低）。
                4. 如果两边都没结论，调 `pcl_log_tail` 看启动器日志的最后 60 行，找第一个报错。
                5. 结论用中文说：坏在哪、为什么会坏、用户下一步该做什么。不要贴大段英文堆栈。

                实例：{args.GetValueOrDefault("instance") ?? "（当前选中）"}
                现象：{args.GetValueOrDefault("symptom") ?? "（用户没说，从日志里推断）"}
                """
        });

        catalog.Prompts.Add(new McpPromptDefinition
        {
            Name = "organize-mods",
            Title = "整理 mod",
            Description = "清理重复、补齐依赖、禁用不匹配的 mod。先列清单让用户确认，再动手。",
            Arguments =
            [
                new McpPromptArgument { Name = "instance", Description = "实例名", Required = false },
                new McpPromptArgument { Name = "goal", Description = "整理目标，例如「让它能启动」「减小体积」", Required = false }
            ],
            Render = args => $"""
                帮我整理这个实例的 mod。

                步骤：

                1. `pcl_list_mods` 拿到全部 mod（含已禁用的）。
                2. `pcl_analyze_mods` 看重复、缺依赖、版本不符、加载器不符。
                3. **先别动手**。把发现的问题分成三档列给用户：
                   - 必须处理（会导致启动失败）
                   - 建议处理（可能引起运行时问题）
                   - 可以不管（只是占空间）
                4. 用户确认后，逐个执行 `pcl_disable_mod` / `pcl_delete_mod`，每步都确认返回的 ok 为 true。
                   删除默认是软删除（进 PCL/McpTrash），不要传 hard=true，除非用户明确要求彻底删除。
                5. 全部改完再跑一次 `pcl_analyze_mods` 验证。

                实例：{args.GetValueOrDefault("instance") ?? "（当前选中）"}
                目标：{args.GetValueOrDefault("goal") ?? "让实例更干净、能稳定启动"}
                """
        });

        catalog.Prompts.Add(new McpPromptDefinition
        {
            Name = "launch-and-verify",
            Title = "启动并确认真的跑起来",
            Description = "启动游戏，等一会儿，用状态和日志确认它是真的进主界面了，而不是卡住或闪退。",
            Arguments =
            [
                new McpPromptArgument { Name = "instance", Description = "实例名", Required = false }
            ],
            Render = args => $"""
                帮我启动游戏，并确认它真的跑起来了。

                1. 先 `pcl_game_status`，如果已经在跑，告诉用户不用重复启动。
                2. `pcl_launch_game` 启动。
                3. 等 15 秒，再 `pcl_game_status` 看进程是否存活、是否还卡在启动阶段。
                4. 如果没起来，`pcl_log_tail` 看日志最后 30 行，判断是卡在下载资源、还是报错退出。
                5. 如果崩了，转 `pcl_analyze_crash`。
                6. 最后告诉用户：起来了没有、卡在哪一步、需不需要做什么。

                实例：{args.GetValueOrDefault("instance") ?? "（当前选中）"}
                """
        });

        catalog.Prompts.Add(new McpPromptDefinition
        {
            Name = "explain-mod",
            Title = "解释一个 mod",
            Description = "查清楚一个 mod 是干什么的、需要什么前置、跟当前实例版本是否匹配。",
            Arguments =
            [
                new McpPromptArgument { Name = "query", Description = "mod 名字或关键词", Required = true },
                new McpPromptArgument { Name = "instance", Description = "实例名", Required = false }
            ],
            Render = args => $"""
                帮我查清楚一个 mod。

                1. `pcl_find_mod` 用关键词找，拿到准确文件名。
                2. `pcl_mod_info` 读它的元数据。
                3. 用中文说清楚：
                   - 它解决什么问题（从 description 归纳，不要照抄英文）
                   - 需要什么前置（depends 里除了 minecraft/forge/fabric 之外的）
                   - 跟当前实例的 MC 版本、加载器是否匹配
                   - 现在是不是禁用状态
                4. 如果它缺前置，直接告诉我该装哪个。

                关键词：{args.GetValueOrDefault("query")}
                实例：{args.GetValueOrDefault("instance") ?? "（当前选中）"}
                """
        });
    }

    #endregion
}
