using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Updates;
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
/// <code>askbbtsk/pcl-lite/releases/latest</code>
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
            string owner, repo;
            try
            {
                (owner, repo) = UpdateManifestPointer.Parse(pointer);
            }
            catch (FormatException ex)
            {
                throw new UpdateSourceException($"更新清单里的仓库地址有问题：{ex.Message}", ex);
            }

            _inner = new UpdatesGitHubModel($"更新清单 → {owner}/{repo}", owner, repo);
            _pointer = pointer;
            ModBase.Log($"[Update] 更新清单指向 {owner}/{repo}");
        }

        _fetchedAt = DateTime.UtcNow;
        return _inner;
    }

    /// <summary>
    /// 单个通道最多等多久。必须给死，否则碰到被墙的域名会一直挂在 TLS 握手上
    /// （实测 raw.githubusercontent 能挂到一分半还不报错），用户就一直转圈。
    /// </summary>
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 拉清单原文。**所有通道并发发出去，谁先回来用谁**（hedged request）。
    /// <para>
    /// 串行试的写法在国内很难受：排在前面的通道一旦被墙，要等到超时才能换下一个，
    /// 用户看到的就是「检查更新转圈十几秒」。并发抢答的代价只是多发几个小请求，
    /// 换来的是「最快那条通道的延迟」——正常几百毫秒就出结果。
    /// </para>
    /// <para>
    /// 每个通道单独 6 秒超时、不重试；第一个成功的立刻返回并取消其余请求。
    /// 全部失败才算这个源不可用（上层会退到直接读当前仓库的 Releases）。
    /// </para>
    /// </summary>
    private static string _FetchPointer()
    {
        return Task.Run(() => _FetchPointerAsync()).GetAwaiter().GetResult();
    }

    private static async Task<string> _FetchPointerAsync()
    {
        using var cts = new CancellationTokenSource();

        var pending = new List<(string Name, Task<string> Task)>();

        // 所有锚点的所有通道一起发出去，谁先回来用谁。
        foreach (var (name, url, isApi) in _ManifestChannels())
        {
            var capturedUrl = url;
            // Contents API 要 Accept 头才会直接回原文，否则回来的是带 base64 的 JSON
            var accept = isApi ? "application/vnd.github.raw" : null;
            pending.Add((name, Task.Run(() => _Get(capturedUrl, accept, cts.Token), cts.Token)));
        }

        var failures = new List<(string Channel, string Reason)>();
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending.Select(x => x.Task)).ConfigureAwait(false);
                var name = pending.First(x => x.Task == done).Name;
                pending.RemoveAll(x => x.Task == done);

                try
                {
                    var pointer = UpdateManifestPointer.Extract(await done.ConfigureAwait(false));
                    ModBase.Log($"[Update] 更新清单已从「{name}」取得", ModBase.LogLevel.Developer);
                    return pointer;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    // 已经被别的通道抢答成功，剩下的请求被取消是预期行为，不算错误、不写日志
                    failures.Add((name, "已被更快的通道取代"));
                }
                catch (FormatException ex)
                {
                    // 清单内容不对（比如反代回了 404 正文）：这条通道算失败，换下一条
                    failures.Add((name, ex.Message));
                    ModBase.Log($"[Update] 更新清单通道失败：{name} → {ex.Message}", ModBase.LogLevel.Developer);
                }
                catch (Exception ex)
                {
                    failures.Add((name, ex.Message));
                    ModBase.Log(ex, $"[Update] 更新清单通道失败：{name}", ModBase.LogLevel.Developer);
                }
            }
        }
        finally
        {
            // 赢家已经拿到手，剩下的请求没必要继续占着带宽
            cts.Cancel();

            // 被取消的请求会抛 OperationCanceledException，而它们已经不在 await 链上了。
            // 不接住就变成「未观察的异常」，等 GC 时被 TaskScheduler 记一笔，白白脏日志。
            foreach (var (_, task) in pending)
                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        // 所有通道往往死在同一个原因上（比如仓库被隐藏 → 全是 HTTP 404）。
        // 按原因归并，日志里一句话就能看出「是整条链路的问题，不是某条通道抽风」。
        var grouped = failures
            .GroupBy(f => f.Reason)
            .Select(g => $"{g.Key}（{string.Join("、", g.Select(f => f.Channel))}）");

        throw new UpdateSourceException(
            $"无法获取更新清单（{ProjectLinks.ServiceRepository}）：{string.Join("；", grouped)}");
    }

    /// <summary>
    /// 清单的全部候选通道：每个锚点 × {Contents API, raw 直连, raw+各反代, jsDelivr}。
    /// <para>
    /// 反代列表直接复用下载用的那份，这样反代失效时只需要改
    /// <see cref="UpdatesGitHubModel.DownloadProxies"/> 一处，下载和清单同时生效。
    /// </para>
    /// <para>
    /// 既然是并发抢答，顺序只影响日志可读性，不影响速度。
    /// 多个锚点时通道名前面会带上 <c>owner/repo</c>，否则日志里分不清是哪个锚点回来的。
    /// </para>
    /// </summary>
    private static IEnumerable<(string Name, string Url, bool IsApi)> _ManifestChannels()
    {
        var anchors = ProjectLinks.ServiceAnchors();
        var multiAnchor = anchors.Count > 1;

        foreach (var (owner, repo) in anchors)
        {
            var tag = multiAnchor ? $"{owner}/{repo} " : string.Empty;
            var raw = ProjectLinks.ManifestRaw(owner, repo);

            // Contents API 和取 Release 用同一个域名，常常是最快的那条
            yield return ($"{tag}Contents API", ProjectLinks.ManifestApi(owner, repo), true);
            yield return ($"{tag}raw 直连", raw, false);

            foreach (var proxy in UpdatesGitHubModel.DownloadProxies)
                yield return ($"{tag}{_ProxyName(proxy)}", proxy + raw, false);

            yield return ($"{tag}jsDelivr", ProjectLinks.ManifestMirror(owner, repo), false);
        }
    }

    /// <summary>日志里只说通道名（域名），不要把整条带反代的长 URL 糊上去。</summary>
    private static string _ProxyName(string proxy) =>
        Uri.TryCreate(proxy, UriKind.Absolute, out var uri) ? uri.Host : proxy;

    /// <summary>
    /// 取一段文本。**每个通道只试一次、6 秒超时**——清单拉不到就赶紧换下一个，
    /// 不能让用户对着转圈等一分半。
    /// </summary>
    private static string _Get(string url, string? accept = null, CancellationToken outerToken = default)
    {
        var request = HttpRequest.Create(url);
        if (accept is not null) request = request.WithHeader("Accept", accept);

        using var timeoutCts = new CancellationTokenSource(ChannelTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            // enableLogging: false —— 抢答会一次发六七条请求，逐条打「Send / Try / End」太吵；
            // 更关键的是被赢家取消的那几条本来就不该在日志里留下异常堆栈。
            response = request
                .SendAsync(retryTimes: 1, enableLogging: false, cancellationToken: linked.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            // 区分「被赢家取消」和「自己超时」：前者是预期行为，原样抛出去由上层静默忽略
            if (outerToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{ChannelTimeout.TotalSeconds:0} 秒内没有响应");
        }

        using (response)
        {
            // 反代挂了会回 404 / 502，响应体往往是「404: Not Found」这种纯文本。
            // 不看状态码就往下走的话，这段文本会被当成指针，
            // 日志里就变成「清单里的仓库地址看不懂」——把真正的原因（通道挂了）掩盖掉了。
            if (!response.IsSuccess)
                throw new UpdateSourceException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());

            var body = response.AsString();
            return accept is null ? body : _UnwrapContentsApi(body);
        }
    }

    /// <summary>
    /// Contents API 正常会按 Accept 直接返回原文；万一头部被忽略，
    /// 返回的是带 base64 <c>content</c> 的 JSON，这里自己解一次。
    /// </summary>
    private static string _UnwrapContentsApi(string body)
    {
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

    // 指针的解析与校验在 PCL.Core 里（UpdateManifestPointer），
    // 那份逻辑被单元测试盯着——它曾经把反代返回的「404: Not Found」当成有效指针。
}
