using System.Text.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp;

/// <summary>
/// 工具对宿主能力的要求。调用前会按用户配置逐项校验，任何一项被关掉都直接拒绝。
/// </summary>
[Flags]
public enum McpCapability
{
    /// <summary>无特殊要求。</summary>
    None = 0,

    /// <summary>读取游戏目录、日志、jar 元数据。始终允许，不受开关限制。</summary>
    Read = 1 << 0,

    /// <summary>在游戏目录内移动、重命名、删除、写入文件。</summary>
    Write = 1 << 1,

    /// <summary>启动或停止 Minecraft 进程。</summary>
    GameControl = 1 << 2,

    /// <summary>修改启动器自身的配置项。</summary>
    LauncherConfig = 1 << 3
}

/// <summary>
/// MCP 工具的入参 JSON Schema 构造器。
/// <para>
/// 之所以手写而不是靠反射生成：schema 里的 description 会原样喂给模型，
/// 描述质量直接决定模型会不会用错参数，所以每个字段都要能单独雕琢。
/// </para>
/// </summary>
public sealed class McpParams
{
    private readonly Dictionary<string, object?> _properties = new();
    private readonly List<string> _required = [];

    private McpParams Add(string name, Dictionary<string, object?> schema, bool required)
    {
        _properties[name] = schema;
        if (required) _required.Add(name);
        return this;
    }

    public McpParams Str(string name, string description, bool required = false,
        IReadOnlyList<string>? enums = null)
    {
        var schema = new Dictionary<string, object?> { ["type"] = "string", ["description"] = description };
        if (enums is { Count: > 0 }) schema["enum"] = enums;
        return Add(name, schema, required);
    }

    public McpParams Int(string name, string description, bool required = false, int? min = null, int? max = null)
    {
        var schema = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = description };
        if (min is { } lo) schema["minimum"] = lo;
        if (max is { } hi) schema["maximum"] = hi;
        return Add(name, schema, required);
    }

    public McpParams Num(string name, string description, bool required = false)
        => Add(name, new Dictionary<string, object?> { ["type"] = "number", ["description"] = description }, required);

    public McpParams Bool(string name, string description, bool required = false)
        => Add(name, new Dictionary<string, object?> { ["type"] = "boolean", ["description"] = description }, required);

    public McpParams StrList(string name, string description, bool required = false)
        => Add(
            name,
            new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new Dictionary<string, object?> { ["type"] = "string" }
            },
            required);

    public McpParams AnyObj(string name, string description, bool required = false)
        => Add(
            name,
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["description"] = description,
                ["additionalProperties"] = true
            },
            required);

    public Dictionary<string, object?> Build()
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = _properties
        };
        if (_required.Count > 0) schema["required"] = _required;
        // 让模型知道「参数表是封闭的」，减少它自己编字段
        schema["additionalProperties"] = false;
        return schema;
    }
}

/// <summary>
/// 一次工具调用的入参。对 <see cref="JsonElement"/> 做了一层容错包装：
/// 名字大小写不敏感、类型能宽松转换（模型经常把 3 写成 "3"）。
/// </summary>
public sealed class McpArguments(JsonElement root)
{
    private Dictionary<string, JsonElement>? _index;

    public JsonElement Root => root;

    private Dictionary<string, JsonElement> Index
    {
        get
        {
            if (_index is not null) return _index;
            var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (root.ValueKind == JsonValueKind.Object)
                foreach (var prop in root.EnumerateObject())
                    map[prop.Name] = prop.Value;
            return _index = map;
        }
    }

    public bool Has(string name) => Index.ContainsKey(name);

    public JsonElement? Raw(string name)
        => Index.TryGetValue(name, out var v) && v.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? v
            : null;

    public string? Str(string name)
    {
        var v = Raw(name);
        if (v is not { } el) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public string Str(string name, string fallback) => Str(name) ?? fallback;

    public bool Bool(string name, bool fallback = false)
    {
        var v = Raw(name);
        if (v is not { } el) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : fallback,
            JsonValueKind.Number => el.TryGetInt64(out var n) ? n != 0 : fallback,
            _ => fallback
        };
    }

    public int? Int(string name)
    {
        var v = Raw(name);
        if (v is not { } el) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        return int.TryParse(el.ToString(), out var parsed) ? parsed : null;
    }

    public int Int(string name, int fallback) => Int(name) ?? fallback;

    public IReadOnlyList<string>? StrList(string name)
    {
        var v = Raw(name);
        if (v is not { } el) return null;
        if (el.ValueKind == JsonValueKind.String)
            return [el.GetString() ?? string.Empty];
        if (el.ValueKind != JsonValueKind.Array) return null;
        return el.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
            .ToList();
    }

    public IReadOnlyList<int>? IntList(string name)
    {
        var v = Raw(name);
        if (v is not { } el || el.ValueKind != JsonValueKind.Array) return null;
        var list = new List<int>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
                list.Add(n);
        return list;
    }
}

/// <summary>工具处理器。</summary>
public delegate Task<object?> McpToolHandler(McpArguments args, CancellationToken cancellationToken);

/// <summary>
/// 一个 MCP 工具的定义。
/// </summary>
public sealed class McpToolDefinition
{
    public required string Name { get; init; }

    /// <summary>给 MCP 客户端做分组用的短标题（可选）。</summary>
    public string? Title { get; init; }

    /// <summary>工具说明。这段文字会原样进模型上下文，务必写清「什么时候该用、有什么副作用」。</summary>
    public required string Description { get; init; }

    public required Dictionary<string, object?> InputSchema { get; init; }

    public required McpToolHandler Handler { get; init; }

    public McpCapability Required { get; init; } = McpCapability.Read;

    /// <summary>是否为破坏性操作（删除、覆盖）。MCP 客户端会用它做二次确认提示。</summary>
    public bool Destructive { get; init; }

    public Dictionary<string, object?> ToProtocolJson() =>
        new()
        {
            ["name"] = Name,
            ["title"] = Title ?? Name,
            ["description"] = Description,
            ["inputSchema"] = InputSchema,
            ["annotations"] = new Dictionary<string, object?>
            {
                ["readOnlyHint"] = Required == McpCapability.Read,
                ["destructiveHint"] = Destructive,
                ["openWorldHint"] = false
            }
        };
}

/// <summary>
/// 工具注册表。所有工具在静态构造时一次性注册，之后只读。
/// </summary>
public sealed class McpRegistry
{
    private readonly Dictionary<string, McpToolDefinition> _tools = new(StringComparer.Ordinal);

    public IReadOnlyCollection<McpToolDefinition> Tools => _tools.Values;

    public int Count => _tools.Count;

    public void Register(McpToolDefinition tool)
    {
        if (!_tools.TryAdd(tool.Name, tool))
            throw new InvalidOperationException($"MCP 工具名重复：{tool.Name}");
    }

    public McpToolDefinition? Find(string name) => _tools.GetValueOrDefault(name);

    public List<Dictionary<string, object?>> ListProtocolJson()
        => _tools.Values.Select(t => t.ToProtocolJson()).ToList();
}
