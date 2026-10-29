using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.App.Lunar;

/// <summary>
///     一个版本可选的一种 loader（服务端叫 module）。
///     <para>
///         同一个游戏版本可以有好几套：Lunar 自己在 <c>lunar</c> 里绑了 OptiFine，
///         <c>lunar-noOF</c> 是不带任何额外 mod 的原版，
///         另外还有 <c>fabric</c> 与 <c>forge</c>。它们的
///         <c>mainClass</c>、JRE 与 JVM 参数完全相同，只有附件清单不一样。
///     </para>
/// </summary>
/// <param name="Id">请求 <c>/launcher/launch</c> 时填进 <c>module</c> 字段的值。</param>
/// <param name="Name">服务端给的名字，例如 <c>Lunar + OptiFine</c>。</param>
/// <param name="Description">服务端的补充说明，可能为空。</param>
/// <param name="IsDefault">官方默认选中的那个。1.17.1 默认是 <c>fabric</c>，不是 <c>lunar</c>。</param>
/// <param name="Sort">服务端排序权重，越小越靠前。</param>
public sealed record LunarModule(
    string Id,
    string Name,
    string Description,
    bool IsDefault,
    int Sort)
{
    /// <summary>界面上显示的文字。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

/// <summary>
///     Lunar Client 的一个可启动子版本。
/// </summary>
/// <param name="Id">子版本号，例如 <c>1.21.4</c>、<c>26.3</c>。</param>
/// <param name="ParentId">所属大版本，例如 <c>1.21</c>。</param>
/// <param name="ParentName">大版本名，例如 <c>The Tricky Trials Update</c>。</param>
/// <param name="AssetIndexId">原版资源索引名，1.8.9 是 <c>1.8</c>，1.21.4 是 <c>19</c>。</param>
/// <param name="AssetIndexUrl">索引文件地址。</param>
/// <param name="AssetIndexSha1">索引文件 SHA-1。</param>
/// <param name="Modules">这个版本可选的 loader，按服务端 <c>sort</c> 升序。可能为空。</param>
public sealed record LunarVersion(
    string Id,
    string ParentId,
    string ParentName,
    string AssetIndexId,
    string AssetIndexUrl,
    string AssetIndexSha1,
    IReadOnlyList<LunarModule> Modules)
{
    /// <summary>下拉框里显示的文字。</summary>
    public string DisplayName => Id;

    /// <summary>
    ///     官方默认的 loader。服务端没标默认时退回列表里的第一个，
    ///     列表为空时返回 <see langword="null" />（调用方要自己兜底）。
    /// </summary>
    public LunarModule? DefaultModule =>
        Modules.FirstOrDefault(x => x.IsDefault) ?? Modules.FirstOrDefault();

    /// <summary>
    ///     版本号排序用的键。按段比较数字，<c>26.3</c> 比 <c>1.21.11</c> 新，
    ///     <c>1.21.11</c> 比 <c>1.21.4</c> 新——字符串比较会把后者判反。
    /// </summary>
    internal static int Compare(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && int.TryParse(a[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedA)
                ? parsedA
                : 0;
            var y = i < b.Length && int.TryParse(b[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedB)
                ? parsedB
                : 0;
            if (x != y) return x.CompareTo(y);
        }

        return 0;
    }
}

/// <summary>
///     Lunar Client 的版本目录。
///     <para>
///         版本列表来自 <c>/launcher/metadata/versions/lunar</c>，结构与启动响应不同：
///         它是 <c>versions[].subversions[]</c> 的两层结构，每个子版本带一份
///         <c>assets = { id, url, sha1 }</c>，正好是 <c>--assetIndex</c> 需要的值。
///     </para>
///     <para>
///         实测 1.7.10 到 26.3 的启动方式完全一致：主类都是
///         <c>com.moonsworth.lunar.genesis.Genesis</c>，<c>launchTypeData</c> 只有
///         <c>artifacts</c> 与 <c>mainClass</c> 两个字段。差异只在 JRE 版本
///         （17 / 21 / 25）与 <c>jre.extraArguments</c> 上，所以全版本共用同一套
///         命令行拼装，只要把版本号、资源索引与 JRE 参数换掉即可。
///     </para>
/// </summary>
public static class LunarVersionCatalog
{
    private const string ApiBase = "https://api.lunarclientprod.com/launcher";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private static IReadOnlyList<LunarVersion> _versions = [];

    /// <summary>当前已知的版本列表，按版本号从新到旧。</summary>
    public static IReadOnlyList<LunarVersion> Versions => _versions;

    public static bool IsEmpty => _versions.Count == 0;

    public static string CachePath(string lunarRoot) =>
        Path.Combine(lunarRoot, "launcher-cache", "pcl-lunar-versions.json");

    public static LunarVersion? Find(string id) =>
        _versions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     同步读磁盘缓存，供界面首帧使用（不能在这里发请求，否则会卡 UI）。
    /// </summary>
    public static bool TryLoadCache(string lunarRoot)
    {
        try
        {
            var path = CachePath(lunarRoot);
            if (!File.Exists(path)) return false;

            var root = JsonNode.Parse(File.ReadAllText(path));
            if (ParseCachedVersions(root?["versions"]) is not { Count: > 0 } parsed) return false;

            // 1.5.4 及以前写的缓存里没有 modules。这种缓存虽然能读，但界面上会一个
            // loader 都选不了，所以当成无效，让上层重新拉一次（只发生一次）
            if (parsed.All(x => x.Modules.Count == 0)) return false;

            _versions = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     拉取版本列表。失败时保留上一次的结果（缓存或内存里的），不抛异常。
    /// </summary>
    /// <param name="force">忽略缓存有效期，强制重新请求。</param>
    public static async Task<IReadOnlyList<LunarVersion>> FetchAsync(
        string lunarRoot,
        string? installationId = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!force && _versions.Count > 0) return _versions;

        var cachePath = CachePath(lunarRoot);
        if (!force && IsCacheFresh(cachePath) && TryLoadCache(lunarRoot)) return _versions;

        var id = installationId ?? Guid.NewGuid().ToString();
        var url = $"{ApiBase}/metadata/versions/lunar?branch=master" +
                  $"&installation_id={Uri.EscapeDataString(id)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Lunar Client Launcher v3.3.0");

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return _versions;

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(text);
            var parsed = ParseVersions(root?["versions"]);
            if (parsed is not { Count: > 0 }) return _versions;

            _versions = parsed;
            TryWriteCache(cachePath, parsed);
            return _versions;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // 网络问题不致命：版本列表拿不到就用缓存或默认 1.8.9
            return _versions;
        }
    }

    private static bool IsCacheFresh(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return DateTime.Now - File.GetLastWriteTime(path) < CacheLifetime;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static List<LunarVersion>? ParseVersions(JsonNode? node)
    {
        if (node is not JsonArray versions) return null;

        var result = new List<LunarVersion>();
        foreach (var version in versions)
        {
            if (version?["subversions"] is not JsonArray subversions) continue;

            var parentId = version["id"]?.GetValue<string>() ?? string.Empty;
            var parentName = version["name"]?.GetValue<string>() ?? string.Empty;

            foreach (var subversion in subversions)
            {
                if (subversion is null) continue;

                var subId = subversion["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(subId)) continue;

                var assets = subversion["assets"];
                result.Add(new LunarVersion(
                    subId,
                    parentId,
                    parentName,
                    assets?["id"]?.GetValue<string>() ?? string.Empty,
                    assets?["url"]?.GetValue<string>() ?? string.Empty,
                    assets?["sha1"]?.GetValue<string>() ?? string.Empty,
                    ParseModules(subversion["modules"])));
            }
        }

        result.Sort((a, b) => LunarVersion.Compare(b.Id, a.Id));
        return result;
    }

    /// <summary>
    ///     读自己写的缓存。缓存是扁平数组（<c>id/parentId/.../modules</c>），
    ///     和服务端的 <c>versions[].subversions[]</c> 不是一套结构，不能共用一个解析器 ——
    ///     1.5.4 之前这里错用了服务端那套，导致缓存**永远读不回来**，
    ///     每次进页面版本列表都是空的。
    /// </summary>
    private static List<LunarVersion>? ParseCachedVersions(JsonNode? node)
    {
        if (node is not JsonArray versions) return null;

        // 旧格式（或者有人手工塞了服务端原始响应）走服务端那套解析
        if (versions.Any(x => x?["subversions"] is JsonArray)) return ParseVersions(node);

        var result = new List<LunarVersion>();
        foreach (var version in versions)
        {
            if (version is null) continue;

            var id = version["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;

            result.Add(new LunarVersion(
                id,
                version["parentId"]?.GetValue<string>() ?? string.Empty,
                version["parentName"]?.GetValue<string>() ?? string.Empty,
                version["assetIndexId"]?.GetValue<string>() ?? string.Empty,
                version["assetIndexUrl"]?.GetValue<string>() ?? string.Empty,
                version["assetIndexSha1"]?.GetValue<string>() ?? string.Empty,
                ParseModules(version["modules"])));
        }

        if (result.Count == 0) return null;
        result.Sort((a, b) => LunarVersion.Compare(b.Id, a.Id));
        return result;
    }

    /// <summary>
    ///     解析 <c>subversions[].modules[]</c>。这一层决定「这个版本能选哪些 loader」，
    ///     缺了它就只能按写死的 <c>lunar</c>（OptiFine）走 —— 而 1.17.1 的官方默认是
    ///     <c>fabric</c>，硬走 OptiFine 会在游戏初始化前静默退出。
    /// </summary>
    private static List<LunarModule> ParseModules(JsonNode? node)
    {
        var result = new List<LunarModule>();
        if (node is not JsonArray modules) return result;

        foreach (var module in modules)
        {
            if (module is null) continue;

            var id = module["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;

            result.Add(new LunarModule(
                id,
                module["name"]?.GetValue<string>() ?? id,
                module["description"]?.GetValue<string>() ?? string.Empty,
                module["default"] is JsonValue isDefault && isDefault.TryGetValue<bool>(out var flag) && flag,
                module["sort"] is JsonValue sortNode && sortNode.TryGetValue<int>(out var sort) ? sort : 0));
        }

        // 服务端的 sort 是展示顺序（0 在前），不是优先级，直接按它排
        result.Sort((a, b) => a.Sort.CompareTo(b.Sort));
        return result;
    }

    private static void TryWriteCache(string path, IReadOnlyList<LunarVersion> versions)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var array = new JsonArray();
            foreach (var version in versions)
            {
                var modules = new JsonArray();
                foreach (var module in version.Modules)
                    modules.Add(new JsonObject
                    {
                        ["id"] = module.Id,
                        ["name"] = module.Name,
                        ["description"] = module.Description,
                        ["default"] = module.IsDefault,
                        ["sort"] = module.Sort
                    });

                array.Add(new JsonObject
                {
                    ["id"] = version.Id,
                    ["parentId"] = version.ParentId,
                    ["parentName"] = version.ParentName,
                    ["assetIndexId"] = version.AssetIndexId,
                    ["assetIndexUrl"] = version.AssetIndexUrl,
                    ["assetIndexSha1"] = version.AssetIndexSha1,
                    ["modules"] = modules
                });
            }

            var root = new JsonObject
            {
                ["fetchedAt"] = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                ["versions"] = array
            };

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去就算了，下次再拉
        }
    }
}
