using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
/// 从 <c>Pcl-Lite-service</c> 仓库的 <c>update.json</c> 读取更新的更新源。
/// <para>
/// 存在的意义只有一个：<b>发布仓库可以搬家，老客户端不会失联</b>。
/// 客户端的代码里写死的不是「哪个仓库有 Release」，而是这份清单的地址；
/// 清单里再写当前该去哪个仓库、哪个 tag、哪个直链、什么哈希。
/// 以后无论换组织、换仓库名、换下载通道，只要改 json，所有还在跑老版本的机器
/// 都能顺着它找到新版本。
/// </para>
/// <para>
/// 顺带解决了 GitHub Releases 源的两个毛病：不消耗 API 限流额度（清单走 raw / CDN），
/// 也不依赖「按时间挑第一个 Release」这种脆弱的推断——版本号、通道、架构全部显式声明。
/// </para>
/// </summary>
public class UpdatesManifestModel : IUpdateSource
{
    /// <summary>清单缓存有效期。太短会让每次检查更新都发请求，太长又看不到刚发的版本。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private DateTime _fetchedAt = DateTime.MinValue;

    private JsonObject? _manifest;

    public UpdatesManifestModel(string? sourceName = null)
    {
        SourceName = sourceName ?? "更新清单";
    }

    public string SourceName { get; set; }

    /// <summary>
    /// 清单地址是编译期写死的常量，不依赖任何用户配置，所以永远「可用」。
    /// 真连不上时由 <see cref="RefreshCache"/> 抛异常，交给上层换下一个源。
    /// </summary>
    public bool IsAvailable() => true;

    public bool RefreshCache(bool force = false)
    {
        if (!force && _manifest is not null && DateTime.UtcNow - _fetchedAt < CacheTtl)
            return true;

        // raw.githubusercontent 在国内被墙得很彻底，jsDelivr 通常还能通。
        // 两个都失败才算这个源挂了。
        var errors = new List<string>();
        foreach (var url in new[] { ProjectLinks.UpdateManifest, ProjectLinks.UpdateManifestMirror })
        {
            try
            {
                var json = HttpRequest.Create(url)
                    .SendAsync()
                    .GetAwaiter()
                    .GetResult()
                    .AsString();

                var parsed = _ParseManifest(json);
                _manifest = parsed;
                _fetchedAt = DateTime.UtcNow;
                ModBase.Log($"[Update] 更新清单已加载：{url}", ModBase.LogLevel.Developer);
                return true;
            }
            catch (Exception ex) when (ex is not UpdateSourceException)
            {
                errors.Add($"{url} → {ex.Message}");
                ModBase.Log(ex, $"[Update] 更新清单拉取失败：{url}", ModBase.LogLevel.Developer);
            }
        }

        throw new UpdateSourceException(
            $"无法获取更新清单（{ProjectLinks.ServiceRepository}）：{string.Join("；", errors)}");
    }

    /// <summary>
    /// 校验并解析清单。格式不对时给一句能照着改的提示，而不是让用户看到 JsonException。
    /// </summary>
    private static JsonObject _ParseManifest(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            // 最常见的坑：仓库私有、路径写错时 raw 返回的是 404 页面而不是 json
            throw new UpdateSourceException(
                "更新清单不是合法的 JSON。请确认 Pcl-Lite-service 仓库是公开的，且 main 分支下存在 update.json。", ex);
        }

        if (node is not JsonObject root)
            throw new UpdateSourceException("更新清单的根节点必须是一个对象。");

        var format = root["format"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(format) && !format.Equals("pcl-lite-update", StringComparison.OrdinalIgnoreCase))
            throw new UpdateSourceException($"更新清单的 format 是「{format}」，不认识这个格式。");

        if (root["channels"] is not JsonObject)
            throw new UpdateSourceException("更新清单缺少 channels 字段。");

        return root;
    }

    /// <summary>
    /// 取指定通道的节点。
    /// <para>
    /// beta 通道没写时回退到 stable：预发布通道本来就该至少能看到正式版，
    /// 否则选了 Beta 的用户会在没有预发布版本的日子里收到「没有任何可用版本」。
    /// </para>
    /// </summary>
    private JsonObject _PickChannel(UpdateChannel channel)
    {
        if (_manifest is null) RefreshCache();

        var channels = _manifest!["channels"] as JsonObject
                       ?? throw new UpdateSourceException("更新清单缺少 channels 字段。");

        var wanted = channel == UpdateChannel.stable ? "stable" : "beta";
        if (channels[wanted] is JsonObject node) return node;

        if (wanted == "beta" && channels["stable"] is JsonObject stable)
        {
            ModBase.Log("[Update] 更新清单没有 beta 通道，回退到 stable", ModBase.LogLevel.Developer);
            return stable;
        }

        throw new UpdateSourceException(wanted == "stable"
            ? "更新清单里没有 stable 通道，无法检查正式版更新。"
            : "更新清单里既没有 beta 也没有 stable 通道。");
    }

    /// <summary>
    /// 取指定架构的资产节点。没有对应架构时退到唯一存在的那一个，
    /// 这样只发 x64 的时候 arm64 用户至少还能装上（x64 版在 ARM 上能靠模拟跑）。
    /// </summary>
    private static JsonObject _PickAsset(JsonObject channelNode, UpdateArch arch)
    {
        if (channelNode["assets"] is not JsonObject assets || assets.Count == 0)
            throw new UpdateSourceException("更新清单里这一版没有 assets。");

        var key = arch == UpdateArch.arm64 ? "arm64" : "x64";
        if (assets[key] is JsonObject exact) return exact;

        var first = assets.FirstOrDefault();
        if (first.Value is JsonObject fallback)
        {
            ModBase.Log($"[Update] 更新清单没有 {key} 资产，回退到 {first.Key}", ModBase.LogLevel.Developer);
            return fallback;
        }

        throw new UpdateSourceException("更新清单的 assets 结构不对。");
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var node = _PickChannel(channel);
        var asset = _PickAsset(node, arch);

        var version = node["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version))
            throw new UpdateSourceException("更新清单里这一版没有写 version。");

        var url = asset["url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url))
            throw new UpdateSourceException("更新清单里这一版没有写下载地址。");

        return new VersionDataModel
        {
            VersionName = version.Trim(),
            VersionCode = node["version_code"]?.GetValue<int>() ?? 0,
            Changelog = node["changelog"]?.GetValue<string>() ?? string.Empty,
            Sha256 = (asset["sha256"]?.GetValue<string>() ?? string.Empty).Trim().ToLowerInvariant(),
            Source = SourceName
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var latest = GetLatestVersion(channel, arch);
        if (!SemVer.TryParse(latest.VersionName, out var remote))
            throw new UpdateSourceException($"更新清单里的版本号「{latest.VersionName}」不是合法的 SemVer。");
        return currentVersion >= remote;
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        // 清单里暂时没有公告机制，返回空列表让调用方跳过。
        return new VersionAnnouncementDataModel { Content = [] };
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var loaders = new List<ModLoader.LoaderBase>();
        var tempPath = $@"{ModBase.pathTemp}Cache\Update\Download\";

        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(
            Lang.Text("Update.Task.GetVersionInfo"), load =>
            {
                var asset = _PickAsset(_PickChannel(channel), arch);
                var url = asset["url"]!.GetValue<string>();
                var fileName = url[(url.LastIndexOf('/') + 1)..];
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "PCL-Lite-update.exe";
                tempPath += fileName;

                // 清单给的是直链，正常应该直接下得动。但直链多半还是 github.com，
                // 国内直连经常只有几十 KB/s，所以照样排上反代，直连放最后兜底。
                var urls = new List<string>();
                urls.AddRange(UpdatesGitHubModel.DownloadProxies.Select(proxy => proxy + url));
                urls.Add(url);
                load.output = [new DownloadFile(urls.Distinct(), tempPath)];
            }));

        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadFile"), []));

        loaders.Add(new ModLoader.LoaderTask<string, int>(
            Lang.Text("Update.Task.ApplyFile"), _ => _ApplyDownloaded(tempPath, output)));

        return loaders;
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
}
