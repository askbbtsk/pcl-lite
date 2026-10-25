using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;

namespace PCL;

/// <summary>
/// 从 <c>Pcl-Lite-service</c> 仓库的 <c>update.json</c> 读取「当前发布仓库在哪儿」的更新源。
/// <para>
/// 存在的意义只有一个：<b>发布仓库可以搬家，已经装出去的旧版本不会失联</b>。
/// 客户端里写死的不是「哪个仓库有 Release」，而是这份清单的地址；
/// 清单里只有一行指针，指向当前的发布仓库：
/// </para>
/// <code>Pcl-Lite/Pcl-lite/releases/latest</code>
/// <para>
/// 版本号、tag、资产直链、SHA-256 全部由那个仓库的 Release 自己提供——
/// 清单不重复这些数据，所以<b>发版时根本不用改它</b>，只有仓库搬家时才需要改一行。
/// </para>
/// <para>
/// 解析出仓库后，真正的取版本工作交给 <see cref="UpdatesGitHubModel"/>：
/// 它本来就会读 Release 资产上的 <c>digest</c> 拿哈希、按架构挑附件、
/// 并在 API 限流时降级到 Atom 订阅源。
/// </para>
/// </summary>
public class UpdatesManifestModel : IUpdateSource
{
    /// <summary>
    /// 清单自身的缓存有效期。比内层 Release 缓存长一些：
    /// 清单只有仓库搬家时才会变，没必要频繁问。
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private DateTime _fetchedAt = DateTime.MinValue;

    private UpdatesGitHubModel? _inner;

    /// <summary>上次解析出的指针原文，用来判断清单有没有变。</summary>
    private string _pointer = string.Empty;

    public UpdatesManifestModel(string? sourceName = null)
    {
        SourceName = sourceName ?? "更新清单";
    }

    public string SourceName { get; set; }

    /// <summary>
    /// 清单地址是编译期写死的常量，不依赖用户配置，所以永远「可用」。
    /// 真连不上时由 <see cref="RefreshCache"/> 抛异常，交给上层换下一个源。
    /// </summary>
    public bool IsAvailable() => true;

    public bool RefreshCache(bool force = false)
    {
        return _EnsureInner(force).RefreshCache(force);
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        return _EnsureInner(false).GetLatestVersion(channel, arch);
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        return _EnsureInner(false).IsLatest(channel, arch, currentVersion, currentVersionCode);
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        return _EnsureInner(false).GetAnnouncementList();
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        return _EnsureInner(false).GetDownloadLoader(channel, arch, output);
    }

    /// <summary>
    /// 拿到（必要时重建）内层源。清单没变就复用，变了就按新仓库重建。
    /// </summary>
    private UpdatesGitHubModel _EnsureInner(bool force)
    {
        if (!force && _inner is not null && DateTime.UtcNow - _fetchedAt < CacheTtl)
            return _inner;

        var pointer = _FetchPointer();
        if (_inner is null || !pointer.Equals(_pointer, StringComparison.OrdinalIgnoreCase))
        {
            var (owner, repo) = ParsePointer(pointer);
            _inner = new UpdatesGitHubModel($"更新清单 → {owner}/{repo}", owner, repo);
            _pointer = pointer;
            ModBase.Log($"[Update] 更新清单指向 {owner}/{repo}");
        }

        _fetchedAt = DateTime.UtcNow;
        return _inner;
    }

    /// <summary>
    /// 拉清单原文，按顺序试一串通道：
    /// <list type="number">
    /// <item>raw.githubusercontent 直连——最快，不吃 API 限流</item>
    /// <item>一串 GitHub 反代 + raw——国内直连 raw 基本不通，反代是主要通道</item>
    /// <item>jsDelivr CDN</item>
    /// <item>GitHub Contents API——兜底，api.github.com 的可达性通常最好</item>
    /// </list>
    /// 全挂才算这个源不可用（上层会退到直接读当前仓库的 Releases）。
    /// </summary>
    private static string _FetchPointer()
    {
        var errors = new List<string>();

        foreach (var url in _ManifestUrls())
        {
            try
            {
                var pointer = ExtractPointer(_Get(url));
                if (string.IsNullOrWhiteSpace(pointer))
                    throw new UpdateSourceException("清单内容是空的");

                return pointer;
            }
            catch (Exception ex)
            {
                errors.Add($"{_DescribeChannel(url)} → {ex.Message}");
                ModBase.Log(ex, $"[Update] 更新清单通道失败：{url}", ModBase.LogLevel.Developer);
            }
        }

        try
        {
            var pointer = ExtractPointer(_GetFromContentsApi());
            if (!string.IsNullOrWhiteSpace(pointer)) return pointer;
            errors.Add("Contents API → 清单内容是空的");
        }
        catch (Exception ex)
        {
            errors.Add($"Contents API → {ex.Message}");
            ModBase.Log(ex, "[Update] 更新清单通道失败：Contents API", ModBase.LogLevel.Developer);
        }

        throw new UpdateSourceException(
            $"无法获取更新清单（{ProjectLinks.ServiceRepository}）：{string.Join("；", errors)}");
    }

    /// <summary>
    /// 清单的所有候选地址。反代列表直接复用下载用的那份，
    /// 这样反代失效时只需要改 <see cref="UpdatesGitHubModel.DownloadProxies"/> 一处。
    /// </summary>
    private static IEnumerable<string> _ManifestUrls()
    {
        var raw = ProjectLinks.UpdateManifest;

        yield return raw;

        foreach (var proxy in UpdatesGitHubModel.DownloadProxies)
            yield return proxy + raw;

        yield return ProjectLinks.UpdateManifestMirror;
    }

    /// <summary>日志里只说通道名，不要把整条带反代的长 URL 糊上去。</summary>
    private static string _DescribeChannel(string url)
    {
        if (url.Equals(ProjectLinks.UpdateManifest, StringComparison.Ordinal)) return "raw 直连";
        if (url.Equals(ProjectLinks.UpdateManifestMirror, StringComparison.Ordinal)) return "jsDelivr";
        return url.StartsWith("https://raw.", StringComparison.OrdinalIgnoreCase) ? "raw" : url.Split('/')[2];
    }

    private static string _Get(string url)
    {
        return HttpRequest.Create(url).SendAsync().GetAwaiter().GetResult().AsString();
    }

    /// <summary>
    /// 走 Contents API 取清单。优先让服务端直接返回原文；
    /// 万一 Accept 头被忽略，返回的是带 base64 <c>content</c> 的 JSON，这里自己解一次。
    /// </summary>
    private static string _GetFromContentsApi()
    {
        var body = HttpRequest.Create(ProjectLinks.UpdateManifestApi)
            .WithHeader("Accept", "application/vnd.github.raw")
            .SendAsync()
            .GetAwaiter()
            .GetResult()
            .AsString();

        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('{')) return body;

        try
        {
            if (JsonNode.Parse(trimmed) is JsonObject obj &&
                obj["content"]?.GetValue<string>() is { Length: > 0 } base64)
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Replace("\n", "").Replace("\r", "")));
        }
        catch (Exception)
        {
            // 解析不出来就按原文返回，让上层的 ExtractPointer 去判空
        }

        return body;
    }

    /// <summary>
    /// 从清单原文里取出指针。
    /// <para>
    /// 兼容三种写法，免得以后手改 json 时因为多写一对括号就全线报错：
    /// 裸字符串 <c>"Pcl-Lite/Pcl-lite/releases/latest"</c>、
    /// 单字段对象 <c>{"update": "Pcl-Lite/Pcl-lite/releases/latest"}</c>、
    /// 以及干脆没写 JSON 的纯文本。
    /// </para>
    /// </summary>
    internal static string ExtractPointer(string body)
    {
        var text = body.Trim().TrimStart('\uFEFF').Trim();
        if (text.Length == 0) return string.Empty;

        // 纯文本（不是 JSON）：整段就是一个指针
        if (text[0] is not ('{' or '[' or '"'))
            return text;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            throw new UpdateSourceException(
                "更新清单不是合法的 JSON。请确认 Pcl-Lite-service 仓库是公开的，且 main 分支下存在 update.json。", ex);
        }

        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var direct):
                return direct?.Trim() ?? string.Empty;

            case JsonObject obj:
            {
                // 常见键名优先；都没有就取唯一那个字符串值
                foreach (var key in new[] { "update", "repository", "repo", "target", "url", "latest" })
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                        return s.Trim();

                var strings = obj
                    .Select(pair => pair.Value)
                    .OfType<JsonValue>()
                    .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                return strings.Count == 1 ? strings[0]!.Trim() : string.Empty;
            }

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// 把 <c>Pcl-Lite/Pcl-lite/releases/latest</c> 拆成 owner 与 repo。
    /// <para>
    /// 后面那段 <c>releases/latest</c> 只是给人看的，程序不依赖它——
    /// 具体取哪一版由 Release 自己的 prerelease 标记决定。
    /// 完整 URL 形式 <c>https://github.com/…</c> 也一并支持。
    /// </para>
    /// </summary>
    internal static (string Owner, string Repo) ParsePointer(string pointer)
    {
        var text = pointer.Trim().Trim('"', '\'').Trim();
        if (text.Length == 0)
            throw new UpdateSourceException("更新清单里没有写仓库地址。");

        // 去掉协议与域名，只留路径
        if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(text, UriKind.Absolute, out var uri))
            text = uri.AbsolutePath;

        text = text.Trim('/');
        // 去掉可能带的 github.com 前缀
        if (text.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            text = text["github.com/".Length..];

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new UpdateSourceException(
                $"更新清单里的仓库地址「{pointer}」看不懂，应该写成 owner/repo，例如 Pcl-Lite/Pcl-lite。");

        var owner = parts[0].Trim();
        var repo = parts[1].Trim();

        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];

        if (owner.Length == 0 || repo.Length == 0)
            throw new UpdateSourceException(
                $"更新清单里的仓库地址「{pointer}」看不懂，应该写成 owner/repo，例如 Pcl-Lite/Pcl-lite。");

        return (owner, repo);
    }
}
