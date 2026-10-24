using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.IO.Net.Http;
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
/// MCP 服务的运行参数快照。每次请求都会重新取一次，
/// 这样用户在设置里改了密码或关掉写权限，下一次调用立刻生效，不需要重启服务。
/// </summary>
public sealed record McpOptions
{
    public bool Enabled { get; init; }

    /// <summary>监听端口。固定绑定 127.0.0.1，不接受外部连接。</summary>
    public int Port { get; init; } = McpDefaults.DefaultPort;

    /// <summary>访问密码。为空表示不校验（本机使用场景）。</summary>
    public string? Token { get; init; }

    public bool AllowWrite { get; init; } = true;

    public bool AllowGameControl { get; init; } = true;

    public bool AllowLauncherConfig { get; init; } = true;

    public bool AuthRequired => !string.IsNullOrWhiteSpace(Token);
}

public static class McpDefaults
{
    public const int DefaultPort = 38765;
    public const string EndpointPath = "/mcp";
    public const string ServerName = "pcl-lite";

    /// <summary>
    /// 对外声明的服务端版本。直接跟随启动器版本，避免两处各写一份而对不上。
    /// </summary>
    public static string ServerVersion => PCL.Core.App.Basics.VersionName;
}

/// <summary>
/// 基于本地 HTTP 的 MCP 服务端。
/// <para>
/// 只监听回环地址，所以外部网络永远碰不到；认证是可选的 Bearer 口令，
/// 用来区分「同一台机器上的其他程序」——没有口令时任何本机进程都能连，
/// 因此默认关闭、且默认要求用户自己决定要不要设口令。
/// </para>
/// </summary>
public sealed class McpHost : HttpServer
{
    private const string CorsOrigin = "*";

    private static readonly JsonSerializerOptions CompactJson = new(JsonCompat.SerializerOptions)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions PrettyJson = new(CompactJson) { WriteIndented = true };

    private readonly McpServerCatalog _catalog;
    private readonly Func<McpOptions> _optionsProvider;

    public McpHost(ushort port, McpServerCatalog catalog, Func<McpOptions> optionsProvider)
        : base([IPAddress.Loopback], port)
    {
        _catalog = catalog;
        _optionsProvider = optionsProvider;
    }

    protected override void Init()
    {
        Register(HttpMethod.Post, McpDefaults.EndpointPath, HandlePostAsync);
        Register(HttpMethod.Get, McpDefaults.EndpointPath, HandleGetAsync);
        Register(HttpMethod.Get, "/health", HandleHealthAsync);
        Register(HttpMethod.Options, "*", _ => Task.FromResult(Empty(HttpStatusCode.NoContent)));
        Register(HttpMethod.Get, "/", HandleHealthAsync);
    }

    #region HTTP 入口

    private Task<HttpRouteResponse> HandleHealthAsync(HttpListenerRequest request)
    {
        var options = _optionsProvider();
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["server"] = McpDefaults.ServerName,
            ["version"] = McpDefaults.ServerVersion,
            ["tools"] = _catalog.Tools.Count,
            ["resources"] = _catalog.Resources.Count,
            ["prompts"] = _catalog.Prompts.Count,
            ["authRequired"] = options.AuthRequired,
            ["endpoint"] = $"http://127.0.0.1:{Port}{McpDefaults.EndpointPath}"
        };
        return Task.FromResult(Json(payload));
    }

    /// <summary>
    /// Streamable HTTP 规范里 GET 用来开 SSE 通知流。本服务没有服务端主动推送的需求，
    /// 直接回 405 并说明原因，比挂一条永远不说话的连接更好排查。
    /// </summary>
    private Task<HttpRouteResponse> HandleGetAsync(HttpListenerRequest request)
    {
        if (!Authorize(request))
            return Task.FromResult(Unauthorized());

        var body = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = "本服务不提供 SSE 通知流，请用 POST 发送 JSON-RPC 请求。",
            ["endpoint"] = $"http://127.0.0.1:{Port}{McpDefaults.EndpointPath}"
        };
        return Task.FromResult(Json(body, HttpStatusCode.MethodNotAllowed));
    }

    private async Task<HttpRouteResponse> HandlePostAsync(HttpListenerRequest request)
    {
        if (!Authorize(request))
            return Unauthorized();

        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            body = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(body))
            return JsonRpcError(null, -32600, "请求体为空。");

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body, JsonCompat.NodeOptions, JsonCompat.DocumentOptions);
        }
        catch (JsonException ex)
        {
            return JsonRpcError(null, -32700, $"JSON 解析失败：{ex.Message}");
        }

        var cancellationToken = CancellationToken.None;

        // JSON-RPC 批量请求
        if (parsed is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch)
            {
                if (item is not JsonObject obj) continue;
                var single = await DispatchAsync(obj, cancellationToken);
                if (single is not null) responses.Add(single);
            }

            if (responses.Count == 0)
                return Empty(HttpStatusCode.Accepted);
            return Json(responses);
        }

        if (parsed is not JsonObject requestObject)
            return JsonRpcError(null, -32600, "请求必须是 JSON 对象。");

        var response = await DispatchAsync(requestObject, cancellationToken);
        if (response is null)
            // 通知：按规范返回 202 且无响应体
            return Empty(HttpStatusCode.Accepted);
        return Json(response);
    }

    #endregion

    #region JSON-RPC 分发

    private async Task<JsonNode?> DispatchAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var method = request["method"]?.GetValue<string>();
        var id = request["id"]?.DeepClone();
        var isNotification = !request.ContainsKey("id");

        if (string.IsNullOrWhiteSpace(method))
        {
            if (isNotification) return null;
            return JsonRpcErrorNode(id, -32600, "缺少 method 字段。");
        }

        JsonNode? result;
        try
        {
            result = method switch
            {
                "initialize" => HandleInitialize(request),
                "notifications/initialized" => null,
                "notifications/cancelled" => null,
                "ping" => new JsonObject(),
                "tools/list" => HandleToolsList(),
                "tools/call" => await HandleToolsCallAsync(request, cancellationToken),
                "resources/list" => HandleResourcesList(),
                "resources/read" => await HandleResourcesReadAsync(request, cancellationToken),
                "prompts/list" => HandlePromptsList(),
                "prompts/get" => HandlePromptsGet(request),
                "logging/setLevel" => new JsonObject(),
                _ => null
            };
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Mcp", $"处理 {method} 失败");
            if (isNotification) return null;
            return JsonRpcErrorNode(id, -32603, $"服务端内部错误：{ex.Message}");
        }

        if (method is "notifications/initialized" or "notifications/cancelled")
            return null;

        if (result is null)
        {
            if (isNotification) return null;
            return JsonRpcErrorNode(id, -32601, $"不支持的方法：{method}");
        }

        if (isNotification) return null;

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
    }

    private JsonNode HandleInitialize(JsonObject request)
    {
        var requested = request["params"]?["protocolVersion"]?.GetValue<string>();
        string[] supported = ["2025-06-18", "2025-03-26", "2024-11-05"];
        var version = supported.Contains(requested) ? requested! : supported[0];

        var capabilities = new JsonObject
        {
            ["tools"] = new JsonObject { ["listChanged"] = false },
            ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
            ["prompts"] = new JsonObject { ["listChanged"] = false }
        };

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = capabilities,
            ["serverInfo"] = new JsonObject
            {
                ["name"] = McpDefaults.ServerName,
                ["title"] = "PCL Lite",
                ["version"] = McpDefaults.ServerVersion
            },
            ["instructions"] = McpInstructions.Text
        };
    }

    private JsonNode HandleToolsList()
    {
        var tools = new JsonArray();
        foreach (var tool in _catalog.Tools.ListProtocolJson())
            tools.Add(JsonSerializer.SerializeToNode(tool, CompactJson));
        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonNode> HandleToolsCallAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var parameters = request["params"] as JsonObject;
        var name = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult(new Dictionary<string, object?> { ["ok"] = false, ["error"] = "缺少工具名。" }, true);

        var tool = _catalog.Tools.Find(name);
        if (tool is null)
            return ToolResult(
                new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"未知工具：{name}",
                    ["available"] = _catalog.Tools.Tools.Select(t => t.Name).ToList()
                },
                true);

        var options = _optionsProvider();
        var denial = CheckCapability(tool, options);
        if (denial is not null)
            return ToolResult(new Dictionary<string, object?> { ["ok"] = false, ["error"] = denial }, true);

        var argumentsNode = parameters?["arguments"];
        JsonElement argumentsElement;
        if (argumentsNode is null)
        {
            argumentsElement = JsonDocument.Parse("{}").RootElement;
        }
        else
        {
            using var doc = JsonDocument.Parse(argumentsNode.ToJsonString(CompactJson));
            argumentsElement = doc.RootElement.Clone();
        }

        object? output;
        try
        {
            output = await tool.Handler(new McpArguments(argumentsElement), cancellationToken);
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Mcp", $"工具 {name} 执行失败");
            return ToolResult(
                new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Message, ["tool"] = name },
                true);
        }

        return ToolResult(output ?? new Dictionary<string, object?> { ["ok"] = true }, false);
    }

    private JsonNode HandleResourcesList()
    {
        var resources = new JsonArray();
        foreach (var resource in _catalog.Resources)
            resources.Add(new JsonObject
            {
                ["uri"] = resource.Uri,
                ["name"] = resource.Name,
                ["description"] = resource.Description,
                ["mimeType"] = resource.MimeType
            });
        return new JsonObject { ["resources"] = resources };
    }

    private async Task<JsonNode> HandleResourcesReadAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var uri = request["params"]?["uri"]?.GetValue<string>();
        var resource = uri is null ? null : _catalog.FindResource(uri);
        if (resource is null)
            return new JsonObject
            {
                ["contents"] = new JsonArray(),
                ["_error"] = $"未找到资源：{uri}"
            };

        var text = await resource.Reader(cancellationToken);
        return new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = resource.Uri,
                    ["mimeType"] = resource.MimeType,
                    ["text"] = text
                }
            }
        };
    }

    private JsonNode HandlePromptsList()
    {
        var prompts = new JsonArray();
        foreach (var prompt in _catalog.Prompts)
            prompts.Add(JsonSerializer.SerializeToNode(prompt.ToProtocolJson(), CompactJson));
        return new JsonObject { ["prompts"] = prompts };
    }

    private JsonNode HandlePromptsGet(JsonObject request)
    {
        var parameters = request["params"] as JsonObject;
        var name = parameters?["name"]?.GetValue<string>();
        var prompt = name is null ? null : _catalog.FindPrompt(name);
        if (prompt is null)
            return new JsonObject
            {
                ["description"] = $"未找到提示词：{name}",
                ["messages"] = new JsonArray()
            };

        var arguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (parameters?["arguments"] is JsonObject argsObject)
            foreach (var (key, value) in argsObject)
                arguments[key] = value?.GetValue<string>();

        return new JsonObject
        {
            ["description"] = prompt.Description,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = prompt.Render(arguments)
                    }
                }
            }
        };
    }

    #endregion

    #region 辅助

    private static string? CheckCapability(McpToolDefinition tool, McpOptions options)
    {
        if (tool.Required.HasFlag(McpCapability.Write) && !options.AllowWrite)
            return $"「{tool.Title ?? tool.Name}」需要写入游戏目录的权限，但该权限已在设置中关闭。"
                   + "请让用户在「设置 → 启动器 → MCP 服务」中打开「允许修改游戏文件」。";

        if (tool.Required.HasFlag(McpCapability.GameControl) && !options.AllowGameControl)
            return $"「{tool.Title ?? tool.Name}」需要控制游戏进程的权限，但该权限已在设置中关闭。"
                   + "请让用户在「设置 → 启动器 → MCP 服务」中打开「允许启动/停止游戏」。";

        if (tool.Required.HasFlag(McpCapability.LauncherConfig) && !options.AllowLauncherConfig)
            return $"「{tool.Title ?? tool.Name}」需要修改启动器配置的权限，但该权限已在设置中关闭。"
                   + "请让用户在「设置 → 启动器 → MCP 服务」中打开「允许修改启动器设置」。";

        return null;
    }

    private bool Authorize(HttpListenerRequest request)
    {
        var options = _optionsProvider();
        if (!options.AuthRequired) return true;

        var provided = ExtractToken(request);
        if (string.IsNullOrEmpty(provided)) return false;
        // 定长比较，避免用 == 泄露前缀匹配长度
        return CryptographicEquals(provided, options.Token!);
    }

    private static string? ExtractToken(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(header))
        {
            const string prefix = "Bearer ";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return header[prefix.Length..].Trim();
            return header.Trim();
        }

        var custom = request.Headers["X-Mcp-Token"];
        if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();

        return request.QueryString["token"]?.Trim();
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static HttpRouteResponse Empty(HttpStatusCode status) => new()
    {
        StatusCode = status,
        Headers = CorsHeaders()
    };

    private static HttpRouteResponse Unauthorized()
    {
        var body = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = "访问口令不正确。请在请求头带上 Authorization: Bearer <口令>，或用 ?token=<口令> 传参。"
        };
        return Json(body, HttpStatusCode.Unauthorized);
    }

    private static HttpRouteResponse JsonRpcError(JsonNode? id, int code, string message)
        => Json(JsonRpcErrorNode(id, code, message));

    private static JsonObject JsonRpcErrorNode(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    private static JsonNode ToolResult(object? payload, bool isError)
    {
        var text = JsonSerializer.Serialize(payload, PrettyJson);
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            },
            ["isError"] = isError
        };
    }

    private static HttpRouteResponse Json(object? payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = payload is JsonNode node
            ? node.ToJsonString(CompactJson)
            : JsonSerializer.Serialize(payload, CompactJson);

        var response = HttpRouteResponse.Text(text, "application/json; charset=utf-8", Encoding.UTF8);
        response.StatusCode = status;
        response.Headers = CorsHeaders();
        return response;
    }

    private static Dictionary<string, string> CorsHeaders() => new()
    {
        ["Access-Control-Allow-Origin"] = CorsOrigin,
        ["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Mcp-Token, Mcp-Session-Id",
        ["Access-Control-Allow-Methods"] = "POST, GET, OPTIONS",
        ["Cache-Control"] = "no-store"
    };

    #endregion
}
