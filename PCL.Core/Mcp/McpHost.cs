using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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

    // 三个权限位的默认值都是 false：漏配时宁可直接拒绝，也不要默默放行。
    // 真正的取值来自用户在设置页里的勾选，见 McpServerService。
    public bool AllowWrite { get; init; }

    public bool AllowGameControl { get; init; }

    public bool AllowLauncherConfig { get; init; }

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

    /// <summary>请求体上限。JSON-RPC 请求不该有这么大，超过多半是打错或恶意。</summary>
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    /// <summary>同时处理的请求上限。HttpListener 那边是无界 Task.Run，这里兜住。</summary>
    private const int MaxConcurrentRequests = 4;

    /// <summary>单个请求的最长处理时间，防止扫描几百个 jar 时一直挂着。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentRequests, MaxConcurrentRequests);

    /// <summary>在飞的请求 id → 取消源，供 notifications/cancelled 命中。</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);

    private int _requestCount;

    /// <summary>累计处理过的 POST 请求数，设置页用它显示「服务确实在被用」。</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    public McpHost(ushort port, McpServerCatalog catalog, Func<McpOptions> optionsProvider)
        // 同时绑 v4 与 v6 回环：客户端把 localhost 解析成 ::1 时也能连上
        : base([IPAddress.Loopback, IPAddress.IPv6Loopback], port)
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
            ["requestCount"] = RequestCount,
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

        Interlocked.Increment(ref _requestCount);

        if (request.ContentLength64 > MaxBodyBytes)
            return Json(
                new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"请求体过大（{request.ContentLength64} 字节），上限 {MaxBodyBytes} 字节。"
                },
                HttpStatusCode.RequestEntityTooLarge);

        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            // 分块传输没有 Content-Length，光看头部拦不住，读的时候也要有上限
            var buffer = new char[MaxBodyBytes];
            var read = 0;
            int chunk;
            while (read < buffer.Length && (chunk = await reader.ReadAsync(buffer, read, buffer.Length - read)) > 0)
                read += chunk;

            if (read >= buffer.Length && !reader.EndOfStream)
                return Json(
                    new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = $"请求体超过 {MaxBodyBytes} 字节上限，已拒绝。"
                    },
                    HttpStatusCode.RequestEntityTooLarge);

            body = new string(buffer, 0, read);
        }

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

        // 并发闸门：客户端狂发请求时不要把线程池和 UI 线程一起拖死
        if (!await _concurrency.WaitAsync(TimeSpan.FromSeconds(30)))
            return Json(
                new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "服务正忙：同时处理的请求已达上限，请稍后重试。"
                },
                HttpStatusCode.ServiceUnavailable);

        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout);

            // JSON-RPC 批量请求
            if (parsed is JsonArray batch)
            {
                var responses = new JsonArray();
                foreach (var item in batch)
                {
                    if (item is not JsonObject obj) continue;
                    var single = await DispatchAsync(obj, timeout.Token);
                    if (single is not null) responses.Add(single);
                }

                if (responses.Count == 0)
                    return Empty(HttpStatusCode.Accepted);
                return Json(responses);
            }

            if (parsed is not JsonObject requestObject)
                return JsonRpcError(null, -32600, "请求必须是 JSON 对象。");

            var response = await DispatchAsync(requestObject, timeout.Token);
            if (response is null)
                // 通知：按规范返回 202 且无响应体
                return Empty(HttpStatusCode.Accepted);
            return Json(response);
        }
        finally
        {
            _concurrency.Release();
        }
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

        // 登记在飞请求，让 notifications/cancelled 能真的把它中断
        var idKey = id?.ToString();
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (idKey is not null) _inFlight[idKey] = requestCts;

        try
        {
            JsonNode? result;
            try
            {
                result = method switch
                {
                    "initialize" => HandleInitialize(request),
                    "notifications/initialized" => null,
                    "notifications/cancelled" => CancelRequest(request),
                    "ping" => new JsonObject(),
                    "tools/list" => HandleToolsList(),
                    "tools/call" => await HandleToolsCallAsync(request, requestCts.Token),
                    "resources/list" => HandleResourcesList(),
                    "resources/read" => await HandleResourcesReadAsync(request, requestCts.Token),
                    "prompts/list" => HandlePromptsList(),
                    "prompts/get" => HandlePromptsGet(request),
                    "logging/setLevel" => new JsonObject(),
                    _ => null
                };
            }
            catch (McpRpcException rpc)
            {
                // 协议级错误：让客户端看到标准的 JSON-RPC error，而不是一个「空资源」
                if (isNotification) return null;
                return JsonRpcErrorNode(id, rpc.Code, rpc.Message);
            }
            catch (OperationCanceledException)
            {
                if (isNotification) return null;
                return JsonRpcErrorNode(id, -32800, "请求已取消或超时。");
            }
            catch (Exception ex)
            {
                LogWrapper.Error(ex, "Mcp", $"处理 {method} 失败");
                if (isNotification) return null;
                // 不把堆栈或内部路径回给客户端，细节只写日志
                return JsonRpcErrorNode(id, -32603, "服务端内部错误，详情见启动器日志。");
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
        finally
        {
            if (idKey is not null) _inFlight.TryRemove(idKey, out _);
        }
    }

    /// <summary>处理 <c>notifications/cancelled</c>：按 requestId 找到在飞请求并取消。</summary>
    private JsonNode? CancelRequest(JsonObject request)
    {
        var requestId = request["params"]?["requestId"];
        if (requestId is null) return null;

        var key = requestId.ToString();
        if (!_inFlight.TryGetValue(key, out var cts)) return null;

        try
        {
            cts.Cancel();
            LogWrapper.Info("Mcp", $"客户端取消了请求 {key}");
        }
        catch (ObjectDisposedException)
        {
            // 请求刚好已经结束，忽略
        }

        return null;
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
            // 工具名不存在属于调用参数错误，按规范走 JSON-RPC -32602，
            // 而不是包成一个「执行失败」的 tool result——客户端会把它当成工具跑挂了。
            throw new McpRpcException(-32602,
                $"未知工具：{name}。可用工具见 tools/list，共 {_catalog.Tools.Count} 个。");

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
        var mutating = (tool.Required & (McpCapability.Write | McpCapability.GameControl | McpCapability.LauncherConfig)) != 0;
        var auditArguments = mutating ? McpAudit.Summarize(argumentsNode as JsonObject) : null;
        try
        {
            output = await tool.Handler(new McpArguments(argumentsElement), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (mutating) McpAudit.Record(name, auditArguments, false, "请求被取消");
            throw;
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Mcp", $"工具 {name} 执行失败");
            if (mutating) McpAudit.Record(name, auditArguments, false, ex.Message);
            return ToolResult(
                new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Message, ["tool"] = name },
                true);
        }

        if (mutating)
        {
            var ok = output is not Dictionary<string, object?> dict
                     || !dict.TryGetValue("ok", out var flag)
                     || flag is not false;
            McpAudit.Record(name, auditArguments, ok, ok ? null : "工具返回 ok=false");
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
            // 之前返回 contents: [] 加一个自定义 _error 字段，
            // 客户端只会当成「这个资源是空的」，没人会去读下划线开头的字段。
            throw new McpRpcException(-32602, $"未找到资源：{uri}。可用资源见 resources/list。");

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
            // 同理：空 messages 会被当成「这个提示词没有内容」，而不是「名字错了」
            throw new McpRpcException(-32602, $"未找到提示词：{name}。可用提示词见 prompts/list。");

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
        return TokenEquals(provided, options.Token!);
    }

    /// <summary>
    ///     只从请求头取口令。
    ///     <para>
    ///         刻意不支持 <c>?token=</c>：查询串会进代理日志、浏览器历史、
    ///         以及任何会记录 URL 的中间件，等于把口令写在明处。
    ///     </para>
    /// </summary>
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
        return string.IsNullOrWhiteSpace(custom) ? null : custom.Trim();
    }

    /// <summary>
    ///     定长比较口令。
    ///     <para>
    ///         直接比字符串会因为「长度不等就提前返回」泄露口令长度，
    ///         所以两边都先过一遍 SHA-256，再对固定长度的摘要做定长比较。
    ///     </para>
    /// </summary>
    private static bool TokenEquals(string provided, string expected)
    {
        Span<byte> left = stackalloc byte[32];
        Span<byte> right = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(provided), left);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), right);
        return CryptographicOperations.FixedTimeEquals(left, right);
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
            ["error"] = "访问口令不正确。请在请求头带上 Authorization: Bearer <口令>，或 X-Mcp-Token。"
                        + "出于安全考虑，不支持用 ?token= 查询参数传递口令。"
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
        var node = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            },
            ["isError"] = isError
        };

        // 支持结构化输出的客户端可以直接取字段，不用再解析那段 text
        if (JsonSerializer.SerializeToNode(payload, CompactJson) is JsonObject structured)
            node["structuredContent"] = structured;

        return node;
    }

    /// <summary>
    ///     协议级错误。抛出后由 <see cref="DispatchAsync" /> 转成标准的 JSON-RPC error 对象，
    ///     而不是伪装成一个「内容为空」的正常结果。
    /// </summary>
    private sealed class McpRpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
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
