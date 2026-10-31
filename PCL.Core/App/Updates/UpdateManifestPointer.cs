using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace PCL.Core.App.Updates;

/// <summary>
/// 更新清单（<c>update.json</c>）里那行「仓库指针」的解析与校验。
/// <para>
/// 指针长这样：<c>askbbtsk/pcl-lite/releases/latest</c>。
/// 这段逻辑被单独拎出来是为了能被单元测试覆盖——它曾经在实战里翻过车：
/// 反代返回 HTTP 404 时响应体是 <c>404: Not Found</c>，被当成「纯文本指针」原样接受，
/// 于是日志里冒出「仓库地址『404: Not Found』看不懂」，把真正的问题（通道挂了）掩盖掉了。
/// </para>
/// <para>
/// 所以这里的规矩是：<b>先校验形状，再当指针用</b>。长得不像 owner/repo 的内容一律拒绝，
/// 让上层把它当成「这个通道失败」而不是「清单写错了」。
/// </para>
/// </summary>
public static class UpdateManifestPointer
{
    /// <summary>
    ///     指针里可以写的「就是本项目自己的仓库」简写。
    ///     <para>
    ///         为什么要有它：<c>update.json</c> 是<b>每个仓库都要放一份</b>的公开标记，
    ///         如果里面写死 <c>owner/repo</c>，那么别人复刻这个仓库、或者我们自己换账号之后，
    ///         这份文件的内容就不一样了——每次从上游同步都会在它上面产生一次无意义的冲突，
    ///         逼着人手动忽略。
    ///     </para>
    ///     <para>
    ///         写成 <c>{"update": "self"}</c> 之后，<b>所有仓库里的内容一模一样</b>，
    ///         合并永远不会冲突；真正的仓库地址由客户端自己的
    ///         <see cref="ProjectLinks.GitHubOwner" /> / <see cref="ProjectLinks.GitHubRepo" /> 决定。
    ///     </para>
    /// </summary>
    public const string SelfKeyword = "self";

    /// <summary>指针长度上限。正常也就几十个字符，超过这个长度必然是别的什么东西。</summary>
    private const int MaxLength = 200;

    /// <summary>报错时把原文截断到多少字符，避免把一整页 HTML 塞进日志。</summary>
    private const int PreviewLength = 60;

    /// <summary>JSON 对象里常见的键名，按优先级排列。</summary>
    private static readonly string[] KnownKeys = ["update", "repository", "repo", "target", "url", "latest"];

    /// <summary>
    /// 从清单原文里取出指针。
    /// <para>
    /// 兼容四种写法，免得以后手改 json 时因为多写一对括号就全线报错：
    /// 裸字符串 <c>"askbbtsk/pcl-lite/releases/latest"</c>、
    /// 单字段对象 <c>{"update": "askbbtsk/pcl-lite/releases/latest"}</c>、
    /// 以及干脆没写 JSON 的纯文本。
    /// </para>
    /// </summary>
    /// <exception cref="FormatException">原文不是清单、或取出的内容不像仓库指针。</exception>
    public static string Extract(string? body)
    {
        var text = (body ?? string.Empty).Trim().TrimStart('\uFEFF').Trim();
        if (text.Length == 0)
            throw new FormatException("清单是空的。");

        // 反代 / 镜像出错时经常回一页 HTML，直接点出来，别让 JSON 解析器背锅
        if (text[0] is '<')
            throw new FormatException($"返回的是网页而不是清单（{_Preview(text)}），多半是通道被墙或仓库不可见。");

        string candidate;

        if (text[0] is '{' or '[' or '"')
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (Exception ex)
            {
                throw new FormatException($"清单不是合法的 JSON（{_Preview(text)}）。", ex);
            }

            candidate = node switch
            {
                JsonValue value when value.TryGetValue<string>(out var direct) => direct?.Trim() ?? string.Empty,
                JsonObject obj => _FromObject(obj),
                _ => string.Empty
            };
        }
        else
        {
            // 纯文本：整段就是一个指针
            candidate = text;
        }

        if (candidate.Length == 0)
            throw new FormatException($"清单里没有写仓库地址（{_Preview(text)}）。");

        // self 在这里就展开，让上层拿到的永远是具体的 owner/repo，
        // 日志和缓存键（_trustedSources 用 Pointer 当键）里不会出现「self」这种看不懂的东西
        candidate = ResolveSelf(candidate);

        if (!LooksLikePointer(candidate))
            throw new FormatException(
                $"清单内容「{_Preview(candidate)}」不是仓库指针，应该写成 owner/repo，例如 askbbtsk/pcl-lite。");

        return candidate;
    }

    /// <summary>
    ///     把 <c>self</c> 展开成本项目自己的发布仓库指针；其它内容原样返回。
    ///     <para>
    ///         展开结果是 <c>owner/repo/releases/latest</c>，其中 owner/repo 取自
    ///         <see cref="ProjectLinks" />——也就是这个 exe 自己认定的仓库。
    ///         所以复刻仓库的人只要不改代码，这份 json 就永远是同一份内容，不会冲突；
    ///         要发自己的版本时也只改 <see cref="ProjectLinks.GitHubOwner" /> 两个常量，
    ///         <c>update.json</c> 一个字都不用动。
    ///     </para>
    /// </summary>
    public static string ResolveSelf(string? pointer)
    {
        var text = (pointer ?? string.Empty).Trim().Trim('"', '\'').Trim();
        if (!text.Equals(SelfKeyword, StringComparison.OrdinalIgnoreCase)) return text;
        return $"{ProjectLinks.GitHubOwner}/{ProjectLinks.GitHubRepo}/releases/latest";
    }

    /// <summary>
    /// 判断一段文本像不像 <c>owner/repo[/...]</c>（或完整的 GitHub URL）。
    /// <para>
    /// 这是整个类里唯一「防呆」的地方，宁严勿松：
    /// 指针里不该有空白、尖括号、花括号，也不该出现冒号（<c>404: Not Found</c> 就是靠这条被挡下的）。
    /// </para>
    /// </summary>
    public static bool LooksLikePointer(string? pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer)) return false;

        var text = pointer.Trim().Trim('"', '\'').Trim();
        if (text.Length is 0 or > MaxLength) return false;

        // self 是合法写法，具体地址由 ResolveSelf 展开
        if (text.Equals(SelfKeyword, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
            if (c is '<' or '>' or '{' or '}' or '[' or ']' or '\\' or '?' or '#') return false;
        }

        // 完整 URL：只要路径里有两段就够了
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
            text = uri.AbsolutePath;
        }

        text = text.Trim('/');
        if (text.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            text = text["github.com/".Length..];

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        // owner / repo 各自不该带冒号（"404:" 这类）、也不该是 . 或 ..
        return parts.Take(2).All(part =>
            part.Length > 0 &&
            part is not ("." or "..") &&
            !part.Contains(':'));
    }

    /// <summary>
    /// 把 <c>askbbtsk/pcl-lite/releases/latest</c> 拆成 owner 与 repo。
    /// <para>
    /// 后面那段 <c>releases/latest</c> 只是给人看的，程序不依赖它——
    /// 具体取哪一版由 Release 自己的 prerelease 标记决定。
    /// 完整 URL 形式 <c>https://github.com/…</c> 也一并支持。
    /// </para>
    /// </summary>
    /// <exception cref="FormatException">指针不合法。</exception>
    public static (string Owner, string Repo) Parse(string? pointer)
    {
        var text = ResolveSelf(pointer).Trim('"', '\'').Trim();
        if (text.Length == 0)
            throw new FormatException("清单里没有写仓库地址。");

        // 去掉协议与域名，只留路径
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(text, UriKind.Absolute, out var uri))
            text = uri.AbsolutePath;

        text = text.Trim('/');
        if (text.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            text = text["github.com/".Length..];

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new FormatException(
                $"仓库地址「{_Preview(text)}」看不懂，应该写成 owner/repo，例如 askbbtsk/pcl-lite。");

        var owner = parts[0].Trim();
        var repo = parts[1].Trim();

        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];

        if (owner.Length == 0 || repo.Length == 0)
            throw new FormatException(
                $"仓库地址「{_Preview(text)}」看不懂，应该写成 owner/repo，例如 askbbtsk/pcl-lite。");

        return (owner, repo);
    }

    /// <summary>
    /// 从 JSON 对象里挑出那个指针：常见键名优先，都没有就要求「只有一个字符串值」，
    /// 免得以后给清单加了别的字段就静默取错。
    /// </summary>
    private static string _FromObject(JsonObject obj)
    {
        foreach (var key in KnownKeys)
            if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                return s.Trim();

        var strings = new List<string>();
        foreach (var pair in obj)
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                strings.Add(s);

        return strings.Count == 1 ? strings[0].Trim() : string.Empty;
    }

    /// <summary>日志 / 异常里只放一小段原文，够定位就行。</summary>
    private static string _Preview(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= PreviewLength ? flat : flat[..PreviewLength] + "…";
    }
}
