using System.Text;
using System.Text.Json;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp;

/// <summary>
/// 内置 MCP 服务的生命周期管理。
/// </summary>
public static class McpServerService
{
    private static readonly object Gate = new();
    private static McpHost? _host;
    private static McpServerCatalog? _catalog;

    /// <summary>服务当前是否在监听。</summary>
    public static bool IsRunning
    {
        get
        {
            lock (Gate) return _host is not null;
        }
    }

    /// <summary>最近一次启动失败的原因。成功启动时为 <see langword="null" />。</summary>
    public static string? LastError { get; private set; }

    /// <summary>服务启动时间。</summary>
    public static DateTime? StartedAt { get; private set; }

    /// <summary>已处理的请求数，用于在设置页显示「服务确实在被用」。</summary>
    public static long RequestCount;

    public static McpServerCatalog Catalog
    {
        get
        {
            lock (Gate)
                return _catalog ??= McpToolRegistration.BuildCatalog();
        }
    }

    /// <summary>从当前配置读出运行参数。</summary>
    public static McpOptions Snapshot()
    {
        var port = Config.Mcp.Port;
        if (port is < 1024 or > 65535) port = McpDefaults.DefaultPort;

        return new McpOptions
        {
            Enabled = Config.Mcp.Enabled,
            Port = port,
            Token = Config.Mcp.Token,
            AllowWrite = Config.Mcp.AllowWrite,
            AllowGameControl = Config.Mcp.AllowGameControl,
            AllowLauncherConfig = Config.Mcp.AllowLauncherConfig
        };
    }

    public static string EndpointUrl(McpOptions options)
        => $"http://127.0.0.1:{options.Port}{McpDefaults.EndpointPath}";

    /// <summary>
    /// 按当前配置启停服务。配置没变时什么都不做，可以放心反复调用。
    /// </summary>
    public static void ApplyFromConfig()
    {
        var options = Snapshot();
        if (options.Enabled)
        {
            if (IsRunning && CurrentPort == options.Port) return;
            Restart();
        }
        else
        {
            Stop();
        }
    }

    private static int CurrentPort
    {
        get
        {
            lock (Gate) return _host?.Port ?? 0;
        }
    }

    public static bool Start()
    {
        lock (Gate)
        {
            if (_host is not null) return true;

            var options = Snapshot();
            if (!options.Enabled)
            {
                LastError = "MCP 服务未启用。";
                return false;
            }

            try
            {
                var host = new McpHost((ushort)options.Port, Catalog, Snapshot);
                host.Start();
                _host = host;
                StartedAt = DateTime.Now;
                LastError = null;
                LogWrapper.Info("Mcp", $"MCP 服务已启动：{EndpointUrl(options)}（{Catalog.Tools.Count} 个工具，口令{(options.AuthRequired ? "已设置" : "未设置")}）");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogWrapper.Error(ex, "Mcp", $"MCP 服务启动失败（端口 {options.Port}）");
                return false;
            }
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_host is null) return;
            try
            {
                _host.Stop();
                _host.Dispose();
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "Mcp", "MCP 服务停止时出现异常");
            }
            finally
            {
                _host = null;
                StartedAt = null;
                LogWrapper.Info("Mcp", "MCP 服务已停止");
            }
        }
    }

    public static void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>
    /// 生成可直接粘进 MCP 客户端配置的 JSON。
    /// 同时给出 HTTP 与 stdio 两种接法：不是所有客户端都支持 HTTP。
    /// </summary>
    public static string BuildClientConfig(McpOptions? options = null)
    {
        options ??= Snapshot();
        var url = EndpointUrl(options);
        var headers = new Dictionary<string, string>();
        if (options.AuthRequired) headers["Authorization"] = $"Bearer {options.Token}";

        var httpEntry = new Dictionary<string, object?>
        {
            ["type"] = "http",
            ["url"] = url
        };
        if (headers.Count > 0) httpEntry["headers"] = headers;

        var root = new Dictionary<string, object?>
        {
            ["mcpServers"] = new Dictionary<string, object?>
            {
                ["pcl-lite"] = httpEntry
            }
        };

        var builder = new StringBuilder();
        builder.AppendLine(JsonSerializer.Serialize(root, new JsonSerializerOptions(JsonCompat.SerializerOptions)
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        if (!options.Enabled)
            builder.AppendLine()
                .AppendLine("// 注意：MCP 服务当前处于关闭状态，请先在设置里打开开关。");
        return builder.ToString();
    }

    /// <summary>
    /// 生成一段自检命令，用户可以直接贴进终端确认服务活着。
    /// </summary>
    public static string BuildProbeCommand(McpOptions? options = null)
    {
        options ??= Snapshot();
        var url = EndpointUrl(options);
        var auth = options.AuthRequired ? $" -H \"Authorization: Bearer {options.Token}\"" : "";
        return $"curl -s{auth} {url.Replace(McpDefaults.EndpointPath, "/health")}";
    }
}

/// <summary>
/// 给模型看的全局说明，会在 initialize 响应的 instructions 字段里返回。
/// <para>
/// 这段文字决定模型「第一印象」——写清楚有哪些能力、什么不能碰、出错先看什么，
/// 比在几十个工具描述里重复同一件事有效得多。
/// </para>
/// </summary>
public static class McpInstructions
{
    public const string Text =
        """
        你是通过 MCP 连接到 PCL Lite（一个 Minecraft 启动器）的助手。当前启动器正在用户的电脑上运行。

        ## 你能做什么
        - 查看游戏实例：名称、版本、加载器、Java、内存分配、目录位置。
        - 管理实例里的文件：列出 / 启用 / 禁用 / 移动 / 删除 mod（jar）、资源包、光影、存档。
        - 启动和停止游戏，并读取启动日志。
        - 分析：实例体检、重复或冲突的 mod、缺失的依赖、崩溃日志定位。

        ## 必须遵守的规则
        1. 破坏性操作（删除、覆盖）之前，先用只读工具确认目标，并在回复里明确说出「要动哪个文件、为什么」。
        2. 不要猜测实例名或文件名，一律先用列表类工具取真实值。
        3. 用户没提到具体实例时，先用 `pcl_list_instances` 看有哪些，再问或者用当前选中实例。
        4. 修改类工具返回的 ok 为 false 时，把 error 原样转述给用户，不要自己编造成功。

        ## 推荐排查顺序
        游戏起不来 → `pcl_analyze_instance` → `pcl_analyze_crash` → `pcl_log_tail`
        mod 有问题 → `pcl_list_mods`（看重复）→ `pcl_analyze_mods`（看依赖和冲突）
        启动卡住 → `pcl_game_status` → `pcl_log_tail` 看最后一行停在哪

        ## 文件约定
        - mod 的启用 / 禁用是靠文件名后缀实现的：`xxx.jar` 是启用，`xxx.jar.disabled` 是禁用。
        - 实例目录默认在 `.minecraft/versions/<实例名>/`；开了版本隔离时，mods 等目录在实例目录下，
          否则在 `.minecraft/` 下。工具会自动判断，不需要你拼路径。
        """;
}
