
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp;

/// <summary>MCP 资源：一个可以被客户端按 URI 直接读取的只读数据源。</summary>
public sealed class McpResourceDefinition
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string MimeType { get; init; } = "application/json";
    public required Func<CancellationToken, Task<string>> Reader { get; init; }
}

/// <summary>MCP 提示词模板的入参声明。</summary>
public sealed class McpPromptArgument
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool Required { get; init; }
}

/// <summary>
/// MCP 提示词模板。
/// <para>
/// 这是「把调试方法论固化下来」的地方：与其指望模型自己想到先看日志再看配置，
/// 不如给一条写死的排查路径，模型照着走就不会漏步骤。
/// </para>
/// </summary>
public sealed class McpPromptDefinition
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<McpPromptArgument> Arguments { get; init; } = [];

    /// <summary>生成提示词正文。参数以名字索引，缺省值由实现自己兜。</summary>
    public required Func<IReadOnlyDictionary<string, string?>, string> Render { get; init; }

    public Dictionary<string, object?> ToProtocolJson() => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = Description,
        ["arguments"] = Arguments.Select(a => new Dictionary<string, object?>
        {
            ["name"] = a.Name,
            ["description"] = a.Description,
            ["required"] = a.Required
        }).ToList()
    };
}

/// <summary>工具、资源、提示词三件套的集合。</summary>
public sealed class McpServerCatalog
{
    public McpRegistry Tools { get; } = new();
    public List<McpResourceDefinition> Resources { get; } = [];
    public List<McpPromptDefinition> Prompts { get; } = [];

    public McpResourceDefinition? FindResource(string uri)
        => Resources.FirstOrDefault(r => string.Equals(r.Uri, uri, StringComparison.OrdinalIgnoreCase));

    public McpPromptDefinition? FindPrompt(string name)
        => Prompts.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}
