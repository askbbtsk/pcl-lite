
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp.Tools;

/// <summary>
/// 游戏进程控制：启动、停止、看状态。
/// </summary>
internal static class GameTools
{
    public static void Register(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_launch_game",
            Title = "启动游戏",
            Description =
                "启动 Minecraft。不传 instance 就用启动器当前选中的实例。\n\n"
                + "启动是异步的：这个工具返回时游戏还在准备中（校验文件、拼启动参数、拉起 Java）。"
                + "要确认真的起来了，返回后隔几秒调 pcl_game_status，或者用 pcl_log_tail 看日志有没有报错。\n\n"
                + "如果返回 ok 为 false，先看 error：常见原因是实例文件缺失、Java 路径无效、"
                + "或者用户已经点过一次启动按钮（正在启动中不能重复触发）。",
            InputSchema = new McpParams()
                .Str("instance", "要启动的实例名。留空使用启动器当前选中的实例。")
                .StrList("extraArgs", "追加到 Java 命令行的额外参数，例如 [\"-Dfoo=bar\"]。一般不需要。")
                .Build(),
            Required = McpCapability.GameControl,
            Handler = async (args, _) =>
            {
                if (McpHostBridge.LaunchGameAsync is null)
                    return McpResults.Error(McpHostBridge.RequireHostAttached());

                var instance = args.Str("instance");
                var extraArgs = args.StrList("extraArgs") ?? [];

                try
                {
                    return await McpHostBridge.LaunchGameAsync(instance, extraArgs);
                }
                catch (Exception ex)
                {
                    return McpResults.Error($"启动失败：{ex.Message}",
                        "用 pcl_analyze_instance 检查实例是否有致命问题。");
                }
            }
        });

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_stop_game",
            Title = "停止游戏",
            Description =
                "强制结束 Minecraft 进程。\n\n"
                + "注意这是强制结束，游戏不会保存世界——只在游戏卡死、或者要改被占用的 jar 文件时用。"
                + "正常关游戏应该让玩家自己在游戏里退出。",
            InputSchema = new McpParams().Build(),
            Required = McpCapability.GameControl,
            Destructive = true,
            Handler = async (_, _) =>
            {
                if (McpHostBridge.StopGameAsync is null)
                    return McpResults.Error(McpHostBridge.RequireHostAttached());

                try
                {
                    return await McpHostBridge.StopGameAsync();
                }
                catch (Exception ex)
                {
                    return McpResults.Error($"停止失败：{ex.Message}");
                }
            }
        });

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_game_status",
            Title = "游戏运行状态",
            Description =
                "查询 Minecraft 现在是否在运行：进程 ID、启动耗时、是否卡在启动阶段、当前实例。\n\n"
                + "调用 pcl_launch_game 之后隔几秒调一次，用来确认「到底起来了没有」。",
            InputSchema = new McpParams().Build(),
            Required = McpCapability.Read,
            Handler = (_, _) =>
            {
                if (McpHostBridge.GetGameState is null)
                    return Task.FromResult<object?>(McpResults.Error(McpHostBridge.RequireHostAttached()));

                var state = McpHostBridge.GetGameState();
                return Task.FromResult<object?>(McpResults.Ok(
                    ("state", state),
                    ("currentInstance", McpHostBridge.GetSelectedInstance?.Invoke())));
            }
        });

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_server_status",
            Title = "MCP 服务状态",
            Description =
                "查看 MCP 服务自身的状态：端口、端点地址、是否要求口令、各类工具的数量。\n\n"
                + "还会返回 clientConfig —— 一段可以直接粘进 AI 客户端 mcpServers 的 JSON。"
                + "用户问「怎么连」的时候把这个原样给他，不要自己拼 URL。\n\n"
                + "排查「客户端连不上」时先调这个确认服务确实在跑、端口对不对。",
            InputSchema = new McpParams().Build(),
            Required = McpCapability.Read,
            Handler = (_, _) =>
            {
                var options = McpServerService.Snapshot();
                var catalogRef = McpServerService.Catalog;

                return Task.FromResult<object?>(McpResults.Ok(
                    ("running", McpServerService.IsRunning),
                    ("endpoint", McpServerService.EndpointUrl(options)),
                    ("port", options.Port),
                    ("authRequired", options.AuthRequired),
                    ("startedAt", McpServerService.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("lastError", McpServerService.LastError),
                    ("toolCount", catalogRef.Tools.Count),
                    ("resourceCount", catalogRef.Resources.Count),
                    ("promptCount", catalogRef.Prompts.Count),
                    ("permissions", new Dictionary<string, object?>
                    {
                        ["write"] = options.AllowWrite,
                        ["gameControl"] = options.AllowGameControl,
                        ["launcherConfig"] = options.AllowLauncherConfig
                    }),
                    ("clientConfig", McpServerService.BuildClientConfig(options)),
                    ("tools", catalogRef.Tools.Tools.Select(t => t.Name).OrderBy(n => n).ToList())));
            }
        });
    }
}
