using System;
using System.Collections.Generic;
using System.IO;
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
/// 从清单仓库读取「当前该去哪些仓库拿版本」的更新源。
/// <para>
/// 存在两个文件，读的顺序是 <c>updatev2.json</c> → <c>update.json</c>：
/// </para>
/// <list type="bullet">
/// <item><c>updatev2.json</c>：多源清单，一次列多个仓库，每个带 tier/weight。
/// 客户端并发问所有源，<b>按权重从高到低取第一个拿到版本的源</b>；权重打平才比版本号。</item>
/// <item><c>update.json</c>：v1 的单行指针，<b>老客户端只认这个</b>，所以它必须原样留着。
/// 新客户端在 v2 拉不到时退回到它，行为与加多源之前完全一致。</item>
/// </list>
/// <para>
/// 为什么多源：单指针只表达得了「去问这一个仓库」。那个仓库整体出事
/// （组织被标记、账号被暂停、Release 被删）时客户端没有任何退路——
/// 2026-10 就这么断过一次。多源之后只要还有一个源活着，更新链路就通。
/// </para>
/// <para>
/// 为什么「权重优先于版本号」：权重是发布者亲手写在清单里的，版本号是远端仓库说了算的。
/// 反过来的话，清单里只要混进一个别的项目（手滑粘错仓库，或者某个仓库被接管后
/// 发了个大版本号的假包），它就会因为版本号更高直接赢，客户端于是去下载并执行别人的 exe。
/// 而「官方源落后、镜像已经同步」这个场景不该靠版本号解决——那该由发布者
/// 把镜像的权重写对，或者直接给镜像提版权重。
/// </para>
/// <para>
/// 至于「这个仓库到底是不是本项目的」，由 <c>update.json</c> 身份校验负责
/// （见 <see cref="_IsTrustedSource"/>），不靠版本号猜。
/// </para>
/// </summary>
public class UpdatesManifestModel : IUpdateSource
{
    /// <summary>
    /// 清单自身的缓存有效期。比内层 Release 缓存长一些：
    /// 清单只在仓库搬家 / 加源时才会变，没必要频繁问。
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();

    /// <summary>拉清单是阻塞的（网络），用闸门保证同一时刻只有一次在跑，避免并发拉五六遍。</summary>
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <summary>本次解析出来的源，按权重降序。</summary>
    private List<UpdateSourceEntry> _entries = [];

    private DateTime _fetchedAt = DateTime.MinValue;

    /// <summary>每个源对应一个 GitHub 源实例（各自维护自己的 Release 列表缓存）。</summary>
    private List<UpdatesGitHubModel> _models = [];

    /// <summary>
    /// 源仓库身份校验的结果缓存（键是 <c>owner/repo</c>）。
    /// 一次检查更新里最多校验一两次，但同一个源会被反复问到，缓存下来省掉重复请求。
    /// 强制刷新时清空。
    /// </summary>
    private readonly Dictionary<string, bool> _trustedSources = new(StringComparer.OrdinalIgnoreCase);

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
        _EnsureLoaded(force);

        List<UpdatesGitHubModel> models;
        lock (_lock) models = _models.ToList();

        var anyOk = false;
        foreach (var model in models)
            try
            {
                anyOk |= model.RefreshCache(force);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Update] 源 {model.SourceName} 刷新失败", ModBase.LogLevel.Developer);
            }

        return anyOk;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        return _PickBest(channel, arch).Version;
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        // 和 GetLatestVersion 用同一个结论，否则会出现「显示已是最新、点检查又提示有新版」。
        // 判据是「远程没有比当前更新的」，不能写成 IsAtLeast(remote, current)：
        // 那样 1.6.1-hotfix-1 ≥ 1.6.1 会直接把有更新判成已是最新
        return UpdateVersion.IsUpToDate(_PickBest(channel, arch).Version.VersionName, currentVersion.ToString());
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        _EnsureLoaded(false);

        // 公告只从权重最高的源取：这是「官方说法」，不该被镜像的旧公告盖掉
        foreach (var model in _OrderedModels())
            try
            {
                return model.GetAnnouncementList();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[Update] {model.SourceName} 取公告失败", ModBase.LogLevel.Developer);
            }

        return new VersionAnnouncementDataModel { Content = [] };
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        // 下载也必须走「刚才挑出最新版的那个源」，否则会出现
        // 「提示 1.5.13，下载下来是 1.5.12 的资产」这种自相矛盾的情况
        var best = _PickBest(channel, arch);
        return best.Model.GetDownloadLoader(channel, arch, output);
    }

    // ─────────────────────────────────────────────────────────────
    //  加载清单
    // ─────────────────────────────────────────────────────────────

    private bool _IsCacheFresh()
    {
        lock (_lock) return _models.Count > 0 && DateTime.UtcNow - _fetchedAt < CacheTtl;
    }

    private void _EnsureLoaded(bool force)
    {
        if (!force && _IsCacheFresh()) return;

        _loadGate.Wait();
        try
        {
            // 排队期间可能已经有人拉好了
            if (!force && _IsCacheFresh()) return;

            var (entries, models) = _Load(force);

            lock (_lock)
            {
                _entries = entries;
                _models = models;
                _fetchedAt = DateTime.UtcNow;

                // 强制刷新时把身份校验结果也丢掉：源列表可能整个换了，
                // 缓存下来的「这个仓库可信」不能跨源列表复用
                if (force) _trustedSources.Clear();
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// 先试多源清单，失败退回 v1 单指针。两条都失败才抛。
    /// <para>
    /// 「用哪份」的判断在 <see cref="UpdateManifestPlanner.Build"/> 里（纯函数，被单元测试盯着）；
    /// 这里只负责把两份原文拉下来。
    /// </para>
    /// </summary>
    private (List<UpdateSourceEntry> Entries, List<UpdatesGitHubModel> Models) _Load(bool force)
    {
        // 1) 先试多源清单
        string? v2Body = null;
        string? v2Failure = null;

        try
        {
            v2Body = _FetchManifestText(ProjectLinks.ManifestV2FileName);
        }
        catch (Exception ex)
        {
            v2Failure = ex.Message;
            // 不是错误级别：updatev2.json 还没推上去、或者这个锚点没有这个文件都是正常情况，
            // 退回 v1 之后一切照旧。用 Developer 级别免得日志里天天飘红。
            ModBase.Log(ex, "[Update] 多源清单不可用，尝试单指针 update.json", ModBase.LogLevel.Developer);
        }

        UpdateManifestPlan? plan = null;
        if (v2Body is not null)
            try
            {
                plan = UpdateManifestPlanner.Build(v2Body, null);
            }
            catch (FormatException ex)
            {
                // 拿到了但内容不可用（写坏了 / 被反代回了 HTML），照样退回 v1
                v2Failure = ex.Message;
                ModBase.Log(ex, "[Update] 多源清单内容无法解析，尝试单指针 update.json", ModBase.LogLevel.Developer);
            }

        // 2) 退回单指针
        if (plan is null)
        {
            string v1Body;
            try
            {
                v1Body = _FetchManifestText(ProjectLinks.ManifestV1FileName);
            }
            catch (Exception ex)
            {
                throw new UpdateSourceException(
                    $"更新清单不可用：多源清单（{v2Failure ?? "未获取"}）；单指针清单（{ex.Message}）", ex);
            }

            try
            {
                plan = UpdateManifestPlanner.Build(null, v1Body) with { V2Failure = v2Failure };
            }
            catch (FormatException ex)
            {
                throw new UpdateSourceException(
                    $"更新清单不可用：多源清单（{v2Failure ?? "未获取"}）；{ex.Message}", ex);
            }
        }

        foreach (var problem in plan.Problems)
            ModBase.Log($"[Update] 多源清单跳过了条目：{problem}", ModBase.LogLevel.Developer);

        var models = plan.Sources
            .Select(entry => new UpdatesGitHubModel(entry.Pointer, entry.Owner, entry.Repo))
            .ToList();

        if (plan.UsedV2)
            ModBase.Log($"[Update] 多源清单已加载，共 {plan.Sources.Count} 个源：" +
                        string.Join("、", plan.Sources.Select(x => x.ToString())));
        else
            ModBase.Log($"[Update] 已回退到单指针清单（{plan.V2Failure}），指向 {plan.Sources[0].Pointer}");

        return (plan.Sources.ToList(), models);
    }

    /// <summary>按权重降序取一份快照，供只该问一个源的地方用。</summary>
    private List<UpdatesGitHubModel> _OrderedModels()
    {
        _EnsureLoaded(false);
        lock (_lock)
        {
            // _models 与 _entries 同序（_Load 里按权重降序建的）
            return _models.ToList();
        }
    }

    /// <summary>
    /// 挑出这次该用哪个源。
    /// <para>
    /// 规则是<b>分档淘汰</b>：把源按权重分成若干档，从最高档开始问；
    /// <b>只要最高档里有一个源把版本报回来了，就当场定下来，低权重的那几档根本不问</b>。
    /// 低权重不是「并列候选」，是「高权重全挂了才轮到」的替补——替补上场就意味着前面的人受伤了，
    /// 不该出现「官方的包好好的，却因为镜像报了个更大的版本号被选中」这种情况。
    /// </para>
    /// <para>
    /// 只有同一档内部（权重相等，本来就是同一个项目的几个镜像）才比版本号，谁新用谁。
    /// </para>
    /// <para>
    /// 落到非官方源之前要先确认那个仓库确实是本项目的（见 <see cref="_IsTrustedSource"/>）。
    /// 所以官方源正常时零额外请求、零额外延迟，只有真的要落到镜像上才会多读一次
    /// 镜像仓库里的 <c>update.json</c>。
    /// </para>
    /// </summary>
    /// <exception cref="UpdateSourceException">所有档位都拿不到版本，或都过不了身份校验。</exception>
    private (VersionDataModel Version, UpdatesGitHubModel Model) _PickBest(UpdateChannel channel, UpdateArch arch)
    {
        _EnsureLoaded(false);

        List<(UpdateSourceEntry Entry, UpdatesGitHubModel Model)> pairs;
        lock (_lock) pairs = _entries.Zip(_models).ToList();

        if (pairs.Count == 0)
            throw new UpdateSourceException("更新清单里没有任何可用的源。");

        var failures = new List<(string Source, string Reason)>();

        // 整次挑选的总预算。低权重档是「前面的都挂了」才会走到的路，
        // 不能让它们把「检查更新」拖成十几秒
        var deadline = DateTime.UtcNow + PickBudget;

        // 按权重分档，从高到低逐档问（分档规则在 UpdateSourceSelector 里，被单元测试盯着）
        var tiers = UpdateSourceSelector.GroupByWeight(
            pairs.Select((pair, index) => (pair.Entry.Weight, index)));

        foreach (var tier in tiers)
        {
            var group = tier.Indices.Select(i => pairs[i]).ToList();
            var candidates = _QueryGroup(group, channel, arch, failures, deadline);

            // 这一档全军覆没 → 才轮到下一档
            if (candidates.Count == 0) continue;

            // 同档内比版本号（规则在 UpdateSourceSelector 里，纯函数、被单元测试盯着）
            var order = UpdateSourceSelector.OrderByPreference(
                candidates.Select((x, i) => new UpdateSourceSelector.Candidate(x.Data.VersionName, x.Entry.Weight, i)));

            foreach (var index in order)
            {
                var candidate = candidates[index];

                // 判据是 tier 而不是权重：权重只决定「先问谁」，决定不了「信不信」。
                // 一个权重 100 但 tier 写着 mirror 的仓库，跟权重 40 的 mirror 一样要验
                if (!_IsOfficial(candidate.Entry) && !_IsTrustedSource(candidate.Entry))
                {
                    failures.Add((candidate.Model.SourceName, "仓库里没有可用的 update.json，身份未确认"));
                    continue;
                }

                ModBase.Log($"[Update] 取 {candidate.Data.VersionName}（来自 {candidate.Model.SourceName}，" +
                            $"权重 {candidate.Entry.Weight}" +
                            $"{(candidate.Entry.Tier.Length > 0 ? "，" + candidate.Entry.Tier : string.Empty)}）",
                    ModBase.LogLevel.Developer);

                return (candidate.Data, candidate.Model);
            }

            // 这一档都过不了身份校验 → 继续往下一档找
        }

        throw new UpdateSourceException(
            $"所有更新源都不可用（{string.Join("；", failures.Select(f => $"{f.Source}: {f.Reason}"))}）。");
    }

    /// <summary>
    /// 把同一权重档的源并发问一遍，返回拿到版本的。
    /// <para>
    /// 档内并发是为了让「这一档能不能用」尽快有结论：档内串行的话，
    /// 第一个被墙就要等到超时才知道下一个行不行，用户看到的是转圈十几秒。
    /// 档与档之间是串行的——高权重档回来了，低权重档压根不发请求。
    /// </para>
    /// </summary>
    private static List<(VersionDataModel Data, UpdatesGitHubModel Model, UpdateSourceEntry Entry)> _QueryGroup(
        IReadOnlyList<(UpdateSourceEntry Entry, UpdatesGitHubModel Model)> group,
        UpdateChannel channel, UpdateArch arch,
        List<(string Source, string Reason)> failures, DateTime deadline)
    {
        var tasks = new Task<(UpdatesGitHubModel Model, UpdateSourceEntry Entry, VersionDataModel? Data, Exception? Error)>[group.Count];
        for (var i = 0; i < group.Count; i++)
        {
            var (entry, model) = group[i];
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    return (model, entry, model.GetLatestVersion(channel, arch), (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (model, entry, (VersionDataModel?)null, ex);
                }
            });
        }

        // 剩下的预算。单个源的 HTTP 超时已经存在，但反代可能挂到更久，
        // 不能让「检查更新」被一个半死的源拖住十几分钟
        var budget = deadline - DateTime.UtcNow;
        if (budget < TimeSpan.Zero) budget = TimeSpan.Zero;

        try
        {
            Task.WaitAll(tasks, budget);
        }
        catch (Exception)
        {
            // 超时的任务不用管，下面按已完成的结果算
        }

        var candidates = new List<(VersionDataModel Data, UpdatesGitHubModel Model, UpdateSourceEntry Entry)>();
        foreach (var task in tasks)
        {
            if (!task.IsCompletedSuccessfully)
            {
                failures.Add(("(超时)", $"{budget.TotalSeconds:0} 秒内没有结果"));
                continue;
            }

            var result = task.Result;
            if (result.Error is not null || result.Data is null)
            {
                failures.Add((result.Model.SourceName, result.Error?.Message ?? "没有版本"));
                continue;
            }

            candidates.Add((result.Data, result.Model, result.Entry));
        }

        return candidates;
    }

    /// <summary>挑源的总时间预算。</summary>
    private static readonly TimeSpan PickBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 这个源是不是「官方源」（<c>tier: official</c>）。
    /// <para>
    /// 官方源<b>不做身份校验</b>：它就是身份本身。而且这一步不能省——校验要发一次网络请求，
    /// 走的是 raw / jsDelivr 这些在国内经常连不上的域名。如果官方源也要验，
    /// 那么「raw 被墙」会连带把官方源判成不可信，用户就彻底收不到更新了，
    /// 而这恰恰是最需要更新能走通的时候。
    /// </para>
    /// <para>
    /// 但官方仓库根目录仍然<b>必须</b>放着 <c>update.json</c>：那是别人（以及我们自己把它当镜像列进
    /// 清单时）判断「这个仓库属于本项目」的唯一依据。客户端不查，不代表可以没有。
    /// </para>
    /// </summary>
    private static bool _IsOfficial(UpdateSourceEntry entry) =>
        entry.Tier.Equals(UpdateManifestV2.TierOfficial, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 确认某个仓库确实是本项目的镜像（只对非官方源调用）。
    /// <para>
    /// 判据只有一个：<b>该仓库根目录下有一份能解析成合法指针的 <c>update.json</c></b>。
    /// 这是发布者亲手放进去的标记——清单里手滑粘错一个别的项目、或者某个仓库被接管后
    /// 发了大版本号的假包，都不会恰好带着这个文件。
    /// </para>
    /// <para>
    /// 不用「版本号看起来对不对」来判断：版本号是远端说了算的，
    /// 而且官方源落后、镜像超前时按版本号判断会直接误杀。
    /// </para>
    /// </summary>
    private bool _IsTrustedSource(UpdateSourceEntry entry)
    {
        lock (_lock)
        {
            if (_trustedSources.TryGetValue(entry.Pointer, out var cached)) return cached;
        }

        bool trusted;
        try
        {
            var body = _FetchFirstText(
                _ChannelsFor(entry.Owner, entry.Repo, ProjectLinks.ManifestV1FileName, false),
                $"{entry.Pointer} 的 update.json");
            UpdateManifestPointer.Extract(body);
            trusted = true;
            ModBase.Log($"[Update] 已确认 {entry.Pointer} 是本项目的镜像（仓库内有 update.json）",
                ModBase.LogLevel.Developer);
        }
        catch (Exception ex)
        {
            trusted = false;
            ModBase.Log(ex, $"[Update] 无法确认 {entry.Pointer} 是本项目的仓库，跳过该源",
                ModBase.LogLevel.Developer);
        }

        lock (_lock) _trustedSources[entry.Pointer] = trusted;
        return trusted;
    }

    // ─────────────────────────────────────────────────────────────
    //  拉清单原文
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 单个通道最多等多久。必须给死，否则碰到被墙的域名会一直挂在 TLS 握手上
    /// （实测 raw.githubusercontent 能挂到一分半还不报错），用户就一直转圈。
    /// </summary>
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 拉清单仓库里的某个文件（update.json / updatev2.json）。
    /// </summary>
    private static string _FetchManifestText(string fileName)
    {
        var local = _ReadLocalManifest(fileName);
        if (local is not null)
            return local;
        return Task.Run(() => _FetchFirstText(_ManifestChannels(fileName), fileName)).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     本地清单覆盖文件的默认文件名。放在 exe 同目录（或者用环境变量 <c>PCL_UPDATE_MANIFEST</c> 指路径）。
    ///     <para>
    ///         给 fork / 自建仓库的人用：把 <c>sources</c> 写成自己的仓库，本地构建就会去查自己的 Release，
    ///         不用再去改受版本控制的 <c>update.json</c>——那个文件改了也没用，
    ///         客户端读的是<b>锚点仓库</b>里那份，不是自己仓库里那份（见 docs/UPDATE.md）。
    ///     </para>
    ///     <para>
    ///         文件名已进 <c>.gitignore</c>，正式发版也不会有这个文件，所以对线上行为零影响。
    ///     </para>
    /// </summary>
    private const string LocalManifestFileName = "update.local.json";

    /// <summary>
    ///     读本地清单覆盖。没有就返回 <see langword="null" />，调用方照常走网络。
    ///     <para>只覆盖多源清单：单指针那份是留给老客户端读的，本地覆盖它没有意义。</para>
    /// </summary>
    private static string? _ReadLocalManifest(string fileName)
    {
        if (fileName != ProjectLinks.ManifestV2FileName)
            return null;

        try
        {
            var path = Environment.GetEnvironmentVariable("PCL_UPDATE_MANIFEST");
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(AppContext.BaseDirectory, LocalManifestFileName);
            else if (!Path.IsPathRooted(path))
                path = Path.Combine(AppContext.BaseDirectory, path);

            if (!File.Exists(path))
                return null;

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            ModBase.Log($"[Update] 使用本地清单覆盖：{path}", ModBase.LogLevel.Developer);
            return text;
        }
        catch (Exception ex)
        {
            // 本地文件读坏了不该影响更新，退回网络
            ModBase.Log(ex, "[Update] 本地清单覆盖读取失败，改用网络清单", ModBase.LogLevel.Developer);
            return null;
        }
    }

    /// <summary>
    /// 从一堆候选通道里抢答，谁先回一段能用的内容就用谁。
    /// <para>
    /// 串行试的写法在国内很难受：排在前面的通道一旦被墙，要等到超时才能换下一个，
    /// 用户看到的就是「检查更新转圈十几秒」。并发抢答的代价只是多发几个小请求，
    /// 换来的是「最快那条通道的延迟」——正常几百毫秒就出结果。
    /// </para>
    /// <para>
    /// 每个通道单独 6 秒超时、不重试；第一个成功的立刻返回并取消其余请求。
    /// </para>
    /// </summary>
    /// <param name="channels">候选通道。</param>
    /// <param name="label">日志里怎么称呼这次请求。</param>
    private static string _FetchFirstText(IEnumerable<(string Name, string Url, bool IsApi)> channels, string label)
    {
        return Task.Run(() => _FetchFirstTextAsync(channels, label)).GetAwaiter().GetResult();
    }

    private static async Task<string> _FetchFirstTextAsync(
        IEnumerable<(string Name, string Url, bool IsApi)> channels, string label)
    {
        using var cts = new CancellationTokenSource();

        var pending = new List<(string Name, Task<string> Task)>();

        // 所有通道一起发出去，谁先回来用谁。
        foreach (var (name, url, isApi) in channels)
        {
            var capturedUrl = url;
            // Contents API 要 Accept 头才会直接回原文，否则回来的是带 base64 的 JSON
            var accept = isApi ? "application/vnd.github.raw" : null;
            pending.Add((name, Task.Run(() => _Get(capturedUrl, accept, cts.Token), cts.Token)));
        }

        if (pending.Count == 0)
            throw new UpdateSourceException($"没有可用的通道去取 {label}。");

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
                    var text = await done.ConfigureAwait(false);
                    ModBase.Log($"[Update] {label} 已从「{name}」取得", ModBase.LogLevel.Developer);
                    return text;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    // 已经被别的通道抢答成功，剩下的请求被取消是预期行为，不算错误、不写日志
                    failures.Add((name, "已被更快的通道取代"));
                }
                catch (Exception ex)
                {
                    failures.Add((name, ex.Message));
                    ModBase.Log(ex, $"[Update] {label} 通道失败：{name}", ModBase.LogLevel.Developer);
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

        throw new UpdateSourceException($"无法获取 {label}：{string.Join("；", grouped)}");
    }

    /// <summary>
    /// 清单锚点的全部候选通道：每个锚点 × {Contents API, raw 直连, raw+各反代, jsDelivr}。
    /// </summary>
    private static IEnumerable<(string Name, string Url, bool IsApi)> _ManifestChannels(string fileName)
    {
        var anchors = ProjectLinks.ServiceAnchors();
        var multiAnchor = anchors.Count > 1;

        foreach (var (owner, repo) in anchors)
            foreach (var channel in _ChannelsFor(owner, repo, fileName, multiAnchor))
                yield return channel;
    }

    /// <summary>
    /// 某一个仓库的某个文件的全部候选通道。
    /// <para>
    /// 反代列表直接复用下载用的那份，这样反代失效时只需要改
    /// <see cref="UpdatesGitHubModel.DownloadProxies"/> 一处，下载和清单同时生效。
    /// </para>
    /// <para>
    /// 既然是并发抢答，顺序只影响日志可读性，不影响速度。
    /// 多个锚点时通道名前面会带上 <c>owner/repo</c>，否则日志里分不清是哪个锚点回来的。
    /// </para>
    /// </summary>
    private static IEnumerable<(string Name, string Url, bool IsApi)> _ChannelsFor(
        string owner, string repo, string fileName, bool tagWithAnchor)
    {
        var tag = tagWithAnchor ? $"{owner}/{repo} " : string.Empty;
        var raw = ProjectLinks.ManifestRaw(owner, repo, fileName);

        // Contents API 和取 Release 用同一个域名，常常是最快的那条
        yield return ($"{tag}Contents API", ProjectLinks.ManifestApi(owner, repo, fileName), true);
        yield return ($"{tag}raw 直连", raw, false);

        foreach (var proxy in UpdatesGitHubModel.DownloadProxies)
            yield return ($"{tag}{_ProxyName(proxy)}", proxy + raw, false);

        yield return ($"{tag}jsDelivr", ProjectLinks.ManifestMirror(owner, repo, fileName), false);
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

    // 指针的解析与校验在 PCL.Core 里（UpdateManifestPointer / UpdateManifestV2），
    // 那份逻辑被单元测试盯着——它曾经把反代返回的「404: Not Found」当成有效指针。
}
