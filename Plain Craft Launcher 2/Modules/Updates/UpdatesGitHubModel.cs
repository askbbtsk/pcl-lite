using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PCL.Core.App;
using PCL.Core.App.Updates;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
/// 从 GitHub Releases 读取更新的更新源。
/// <para>
/// 上游用的是自建的 MinIO 格式（<c>apiv2/cache.json</c> + 一堆清单文件），
/// 从 fork 出来的人一般没有这套服务，所以这里直接用 GitHub 的 Releases API。
/// </para>
/// <para>
/// 通道对应关系：按版本后缀算出不稳定等级（正式 0 / pre 1 / snapshot 2 / nightly 3），
/// 取「等级 &lt;= 通道等级」里版本号最大的那个。通道是包含式的，激进通道也收稳定版。
/// 版本号取 <c>tag_name</c>，会自动去掉 <c>v</c> 前缀。
/// </para>
/// </summary>
public class UpdatesGitHubModel : IUpdateSource
{
    private const int PageSize = 20;

    /// <summary>
    /// GitHub 反代，只作为<b>后备</b>：官方直链打不开（被墙、超时、404）时才轮到它们。
    /// <para>
    /// 顺序即优先级。走反代等于把发布包交给别人的服务器中转一遍，能直连就不要绕，
    /// 所以 <see cref="GetDownloadLoader"/> 里官方地址永远排在最前面。
    /// 反代服务时好时坏，失效了改这个列表即可。
    /// </para>
    /// </summary>
    internal static readonly string[] DownloadProxies =
    [
        "https://cors.isteed.cc/",
        "https://gh-proxy.com/",
        "https://gitproxy.click/",
    ];

    private JsonArray? _releases;

    /// <summary>
    ///     上次成功拉取版本列表的时间。用来判断缓存是否还有效。
    /// </summary>
    private DateTime _fetchedAt = DateTime.MinValue;

    /// <summary>
    ///     版本列表的缓存有效期。
    ///     <para>
    ///     原本这里没有有效期，进程活多久缓存就活多久。结果是：启动器在发版之前就开着的话，
    ///     之后不管点多少次「重新检查」都只会看到发版前的那份列表，必须重启才认新版本。
    ///     给个短有效期，让自动检查也能在一个合理时间后自己刷新。
    ///     </para>
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 实际去问哪个仓库。默认取 <see cref="ProjectLinks"/> 里的当前仓库，
    /// 但 <see cref="UpdatesManifestModel"/> 会传入更新清单里写的仓库——
    /// 这样清单把仓库指到哪儿，这里就问哪儿。
    /// </summary>
    private readonly string _owner;

    private readonly string _repo;

    public UpdatesGitHubModel(string? sourceName = null, string? owner = null, string? repo = null)
    {
        SourceName = sourceName ?? "GitHub Releases";
        _owner = string.IsNullOrWhiteSpace(owner) ? ProjectLinks.GitHubOwner : owner.Trim();
        _repo = string.IsNullOrWhiteSpace(repo) ? ProjectLinks.GitHubRepo : repo.Trim();
    }

    public string SourceName { get; set; }

    private string _ReleasesApi => $"https://api.github.com/repos/{_owner}/{_repo}/releases";

    private string _Repository => $"https://github.com/{_owner}/{_repo}";

    public bool IsAvailable() => !string.IsNullOrWhiteSpace(_owner) && !string.IsNullOrWhiteSpace(_repo);

    public bool RefreshCache(bool force = false)
    {
        _EnsureConfigured();

        // 缓存还在有效期内就直接用，省一次请求（也省 GitHub 的限流额度）。
        // force 用于用户主动点「重新检查」，必须真的去问一次。
        if (!force && _releases is not null && DateTime.UtcNow - _fetchedAt < CacheTtl)
            return true;

        try
        {
            var json = HttpRequest.Create($"{_ReleasesApi}?per_page={PageSize}")
                .WithHeader("Accept", "application/vnd.github+json")
                .SendAsync()
                .GetAwaiter()
                .GetResult()
                .AsString();

            if (JsonNode.Parse(json) is JsonArray releases)
            {
                if (releases.Count == 0)
                    throw new UpdateSourceException("这个仓库还没有发布任何 Release。");

                _releases = releases;
                _fetchedAt = DateTime.UtcNow;
                return true;
            }

            // 返回的是对象而不是数组：403 限流、仓库不存在、令牌失效都在这个分支
            ModBase.Log($"GitHub Releases API 不可用：{_DescribeBadResponse(json)}", ModBase.LogLevel.Developer);
        }
        catch (Exception ex) when (ex is not UpdateSourceException)
        {
            ModBase.Log(ex, "GitHub Releases API 请求失败", ModBase.LogLevel.Developer);
        }

        // API 走不通。最常见的情形是用户挂着代理，出口 IP 把未认证的
        // 60 次/小时限流打满了（直连反而没事）。这时换 Atom 订阅源：
        // 它挂在 github.com 下，不走 REST API，不受该限流约束。
        return _RefreshCacheFromAtom();
    }

    /// <summary>
    /// 用 Atom 订阅源（<c>releases.atom</c>）顶替 API 拉取版本列表。
    /// 不走 REST API，因而不受 60 次/小时的限流约束，是 API 挂掉时的备用通道。
    /// 局限：不区分 prerelease、不含资产列表（资产改从发布页现抓）。
    /// </summary>
    private bool _RefreshCacheFromAtom()
    {
        _EnsureConfigured();

        try
        {
            var atom = HttpRequest.Create($"{_Repository}/releases.atom")
                .SendAsync()
                .GetAwaiter()
                .GetResult()
                .AsString();

            var entries = _ParseAtom(atom);
            if (entries.Count == 0)
                throw new UpdateSourceException("Atom 订阅源里没有任何 Release。");

            _releases = entries;
            _fetchedAt = DateTime.UtcNow;
            ModBase.Log("已降级使用 Atom 订阅源获取版本列表", ModBase.LogLevel.Developer);
            return true;
        }
        catch (Exception ex)
        {
            throw new UpdateSourceException(
                "无法获取版本信息：GitHub API 不可用（挂代理时出口 IP 可能已触发每小时 60 次的限流），" +
                $"Atom 订阅源也失败了（{ex.Message}）。请稍后再试，或检查网络连接。");
        }
    }

    /// <summary>
    /// 解析 releases.atom，转成和 API 同构的 JsonArray。
    /// tag 从 entry 的 alternate 链接里取（…/releases/tag/v0.1.0-beta），
    /// 正文取 content 并剥掉 HTML 标签。
    /// </summary>
    private static JsonArray _ParseAtom(string atom)
    {
        var doc = XDocument.Parse(atom);
        var ns = (XNamespace)"http://www.w3.org/2005/Atom";
        var result = new JsonArray();

        foreach (var entry in doc.Descendants(ns + "entry"))
        {
            var link = entry.Element(ns + "link")?.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(link)) continue;

            var tagIndex = link.LastIndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
            if (tagIndex < 0) continue;
            var tag = Uri.UnescapeDataString(link[(tagIndex + 5)..]);
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var body = _StripHtml(entry.Element(ns + "content")?.Value ?? string.Empty);

            var release = new JsonObject
            {
                ["tag_name"] = tag,
                ["body"] = body,
                ["draft"] = false,
                // Atom 不提供 prerelease 标记，但 tag 本身就写着：v1.2.1-beta。
                // 按 tag 猜一下，否则走降级通道时正式版通道会把 beta 当成正式版推给用户。
                ["prerelease"] = _LooksPrerelease(tag),
            };
            result.Add(release);
        }

        return result;
    }

    /// <summary>把 Atom 的 HTML 正文转成纯文本（保留段落换行）。</summary>
    private static string _StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</p\s*>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }

    /// <summary>
    /// 从发布页的资产列表（expanded_assets）抓附件下载地址。
    /// Atom 订阅源不含资产，API 又挂的时候靠它拿到下载地址；
    /// 同样挂在 github.com 下，不受 REST API 限流约束。
    /// </summary>
    private JsonArray _FetchAssetsFromPage(string tag)
    {
        var html = HttpRequest.Create($"{_Repository}/releases/expanded_assets/{tag}")
            .SendAsync()
            .GetAwaiter()
            .GetResult()
            .AsString();

        var assets = new JsonArray();
        foreach (Match match in Regex.Matches(html, @"href=""(/[^""]+/releases/download/[^""]+)"""))
        {
            var url = "https://github.com" + WebUtility.HtmlDecode(match.Groups[1].Value);
            if (assets.Any(x => x?["browser_download_url"]?.GetValue<string>() == url)) continue;
            assets.Add(new JsonObject { ["browser_download_url"] = url });
        }

        return assets;
    }

    /// <summary>
    /// GitHub 出错时返回的是对象而不是数组，最常见的是 403 限流
    /// （未认证请求按出口 IP 计，每 IP 每小时 60 次，挂代理时很容易被打满）。
    /// 把原始原因翻成人话，比笼统地说「仓库地址填错了」有用得多。
    /// </summary>
    private static string _DescribeBadResponse(string json)
    {
        string? apiMessage = null;
        try
        {
            if (JsonNode.Parse(json) is JsonObject obj)
                apiMessage = obj["message"]?.GetValue<string>();
        }
        catch (Exception)
        {
            // 压根不是 JSON（例如代理返回的错误页），走下面的兜底文案
        }

        if (!string.IsNullOrWhiteSpace(apiMessage))
            return apiMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                ? "GitHub 接口触发了限流（403）：同一出口 IP 每小时只能请求 60 次，请稍后再试。"
                : $"GitHub 返回了错误：{apiMessage}";

        return "GitHub Releases 接口返回了非预期的内容，可能是仓库地址填错了。";
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        _EnsureConfigured();
        var release = _PickRelease(channel);
        return new VersionDataModel
        {
            VersionName = _NormalizeTag(release["tag_name"]?.GetValue<string>()),
            // GitHub 没有内部版本代号的概念，而 IsLatest 只比较 SemVer，所以这里填 0。
            VersionCode = 0,
            Changelog = release["body"]?.GetValue<string>() ?? string.Empty,
            // 有官方 digest 就带上，没有就留空（调用方会跳过校验）
            Sha256 = _TryGetSha256(release, arch),
            Source = SourceName
        };
    }

    /// <summary>
    ///     取目标附件的 SHA-256。GitHub 现在会在资产上带 <c>digest</c> 字段（<c>sha256:...</c>），
    ///     有就拿来做完整性校验，没有（Atom 降级通道、老接口）就返回空串让调用方跳过校验。
    ///     <para>
    ///         只认 .exe 附件：zip 的 digest 是压缩包本身的，解出来的 exe 哈希对不上。
    ///     </para>
    /// </summary>
    private static string _TryGetSha256(JsonObject release, UpdateArch arch)
    {
        if (release["assets"] is not JsonArray assets || assets.Count == 0) return string.Empty;
        try
        {
            var url = _PickAsset(assets, arch);
            if (!url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return string.Empty;

            var asset = assets.OfType<JsonObject>().FirstOrDefault(x =>
                x["browser_download_url"]?.GetValue<string>() == url);
            var digest = asset?["digest"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(digest)) return string.Empty;

            const string prefix = "sha256:";
            return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? digest[prefix.Length..].Trim().ToLowerInvariant()
                : string.Empty;
        }
        catch (Exception)
        {
            // 挑不到附件不该影响「检查更新」本身
            return string.Empty;
        }
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var latest = GetLatestVersion(channel, arch);
        return currentVersion >= SemVer.Parse(latest.VersionName);
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        // GitHub Releases 没有公告机制，返回空列表让调用方跳过。
        return new VersionAnnouncementDataModel { Content = [] };
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var loaders = new List<ModLoader.LoaderBase>();
        var tempPath = $@"{ModBase.pathTemp}Cache\Update\Download\";

        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(
            Lang.Text("Update.Task.GetVersionInfo"), load =>
            {
                var release = _PickRelease(channel);
                var assets = release["assets"] as JsonArray;
                if (assets is null || assets.Count == 0)
                {
                    // Atom 降级通道没有资产列表，去发布页现抓
                    var tag = release["tag_name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(tag))
                        throw new UpdateSourceException("Release 没有 tag，无法定位资产。");
                    assets = _FetchAssetsFromPage(tag);
                }
                if (assets.Count == 0)
                    throw new UpdateSourceException("这个 Release 没有任何附件。");

                var url = _PickAsset(assets, arch);
                var fileName = url[(url.LastIndexOf('/') + 1)..];
                tempPath += fileName;

                // 官方直链优先、反代兜底。
                // 顺序在 DownloadCoreAsync 里就是优先级：挨个试，前一个失败才换下一个。
                // 直连能通就用直连——反代是第三方中转，没必要把发布包先过一道别人的手。
                var urls = new List<string> { url };
                urls.AddRange(DownloadProxies.Select(proxy => proxy + url));
                load.output = [new DownloadFile(urls.Distinct(), tempPath)];
            }));

        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadFile"), []));

        loaders.Add(new ModLoader.LoaderTask<string, int>(
            Lang.Text("Update.Task.ApplyFile"), _ => _ApplyDownloaded(tempPath, output)));

        return loaders;
    }

    /// <summary>
    ///     从 tag 猜它是不是预发布版。只用于 Atom 降级通道——那条路拿不到 prerelease 字段。
    ///     SemVer 里 <c>-</c> 之后就是预发布标识（<c>1.2.1-beta</c>），<c>+</c> 之后是构建元数据。
    /// </summary>
    private static bool _LooksPrerelease(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        return value.Contains('-');
    }

    /// <summary>
    /// 仓库地址还是占位符时，给一条能直接照着改的提示，
    /// 而不是让用户看到 GitHub API 返回的 404。
    /// </summary>
    private void _EnsureConfigured()
    {
        if (IsAvailable()) return;
        throw new UpdateSourceException(
            $"更新仓库地址不完整（{_owner}/{_repo}）。请检查 PCL.Core/App/ProjectLinks.cs 中的 " +
            "GitHubOwner 与 GitHubRepo，或 Pcl-Lite-service 仓库 update.json 里写的仓库路径。");
    }

    /// <summary>
    /// 按通道挑一个 Release。跳过草稿；stable 通道还要跳过 prerelease。
    /// </summary>
    /// <summary>
    ///     在缓存里挑出当前通道能收到的最新一版。
    ///     <para>
    ///         判定条件是「版本等级 &lt;= 通道等级」，所以激进通道**也包含**稳定版：
    ///         选了「始终最新」的人不会因为手里是 nightly 就收不到新发的正式版。
    ///     </para>
    ///     <para>
    ///         排序按 SemVer 降序，不用 API 返回顺序——GitHub 是按创建时间倒序给的，
    ///         补发一个旧版本的预发布包就会把顺序搞乱，进而把旧版当成最新推出去。
    ///     </para>
    /// </summary>
    private JsonObject _PickRelease(UpdateChannel channel)
    {
        if (_releases is null) RefreshCache();

        var candidates = new List<(SemVer Version, JsonObject Release)>();
        foreach (var item in _releases!)
        {
            if (item is not JsonObject release) continue;
            if (release["draft"]?.GetValue<bool>() == true) continue;

            var tag = release["tag_name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tag)) continue;

            string versionName;
            int level;
            try
            {
                versionName = _NormalizeTag(tag);
                level = UpdateChannelLevels.FromVersionName(versionName);
            }
            catch (Exception)
            {
                // tag 解析不了就跳过这一条，不要因为一个脏 tag 让整次检查更新失败
                continue;
            }

            // tag 里没写后缀、但被标成 prerelease 的，按预览级处理：
            // 宁可少收，也不要把预发布包当正式版推给只想要稳定版的用户。
            if (level == UpdateChannelLevels.Stable && release["prerelease"]?.GetValue<bool>() == true)
                level = UpdateChannelLevels.Preview;

            if (level > (int)channel) continue;
            if (!SemVer.TryParse(versionName, out var semVer) || semVer is null) continue;

            candidates.Add((semVer, release));
        }

        if (candidates.Count == 0)
            throw new UpdateSourceException(channel == UpdateChannel.stable
                ? "仓库里没有正式版 Release，只有预发布版本。"
                : "仓库里没有任何符合当前更新通道的 Release。");

        return candidates.OrderByDescending(x => x.Version).First().Release;
    }

    /// <summary>
    /// 在附件里挑一个可执行文件。优先匹配架构关键字，其次优先 zip。
    /// </summary>
    private static string _PickAsset(JsonArray assets, UpdateArch arch)
    {
        var candidates = assets
            .OfType<JsonObject>()
            .Select(x => x["browser_download_url"]?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Where(x => x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            throw new UpdateSourceException("这个 Release 的附件里没有 .zip 或 .exe。");

        var archKey = arch == UpdateArch.arm64 ? "arm64" : "x64";
        var matched = candidates.FirstOrDefault(x =>
            x.Contains(archKey, StringComparison.OrdinalIgnoreCase));
        if (matched is not null) return matched;

        // 没标架构就优先用 zip（里面装着 exe），再退到任意一个
        return candidates.FirstOrDefault(x => x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
               ?? candidates[0];
    }

    private static void _ApplyDownloaded(string path, string output)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs);
            var entry = zip.Entries
                            .FirstOrDefault(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new Exception(Lang.Text("Update.Error.FileNotFound"));
            entry.ExtractToFile(output, true);
            return;
        }

        File.Copy(path, output, true);
    }

    /// <summary>
    /// 把 tag 规范化成 SemVer。GitHub 上常见 <c>v0.1.0-beta</c> 这种写法，要把 <c>v</c> 去掉。
    /// 另外有人写 tag 时偷懒只写 <c>v1.0</c> 甚至 <c>v1</c>（缺 minor/patch），
    /// 这时按 SemVer 惯例补零成 <c>1.0.0</c>，而不是直接报错说版本源不可用。
    /// </summary>
    private static string _NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new UpdateSourceException("Release 没有 tag，无法确定版本号。");

        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        // 先拆出预发布与构建元数据，它们内部允许带点，不能参与核心版本号的分段
        var core = value;
        var suffix = string.Empty;
        var metaIndex = value.IndexOfAny(['-', '+']);
        if (metaIndex >= 0)
        {
            core = value[..metaIndex];
            suffix = value[metaIndex..];
        }

        var parts = core.Split('.');
        if (parts.Length == 0 || parts.Length > 3 || !parts.All(x => x.All(char.IsAsciiDigit)))
            throw new UpdateSourceException($"Release 的 tag「{tag}」不是合法的 SemVer，无法比较版本。");

        // 缺段补零：1 → 1.0.0，1.0 → 1.0.0
        while (parts.Length < 3) parts = [.. parts, "0"];
        var normalized = string.Join('.', parts) + suffix;

        if (!SemVer.TryParse(normalized, out _))
            throw new UpdateSourceException($"Release 的 tag「{tag}」不是合法的 SemVer，无法比较版本。");

        return normalized;
    }
}
