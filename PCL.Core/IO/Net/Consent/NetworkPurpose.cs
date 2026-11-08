using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.IO.Net.Consent;

/// <summary>
/// 网络用途。PCL Lite 把每一次出站连接都归属到某个用途上，
/// 用户按用途授权，而不是按域名逐个确认。
/// </summary>
public enum NetworkPurpose
{
    /// <summary>启动器更新与公告。</summary>
    LauncherUpdate,

    /// <summary>Minecraft 版本、资源与库文件下载。</summary>
    MinecraftDownload,

    /// <summary>模组加载器元数据（Forge / Fabric / NeoForge 等）。</summary>
    LoaderMetadata,

    /// <summary>模组与整合包仓库（Modrinth / CurseForge）。</summary>
    ModRepository,

    /// <summary>加密 DNS 解析。</summary>
    DnsResolve,

    /// <summary>未登记的目标域名。</summary>
    Unknown
}

/// <summary>
/// 网络用途的元信息：文案、依赖域名、拒绝后的后果。
/// </summary>
public sealed record NetworkPurposeInfo(
    NetworkPurpose Purpose,
    string Id,
    string TitleZh,
    string TitleEn,
    string DescriptionZh,
    string DescriptionEn,
    string FeatureZh,
    string FeatureEn,
    string[] Hosts)
{
    /// <summary>拒绝授权后是否会导致对应功能完全不可用。</summary>
    public bool RequiredForFeature => true;
}

/// <summary>
/// 网络用途登记表。未在此登记的域名一律视为「未登记目标」，需要单独确认。
/// </summary>
/// <remarks>
/// 这里的域名必须与代码里真正会发出的请求保持一致：登记多了会让用户为不存在的连接授权，
/// 登记少了则会让守门人在运行时弹出「未登记目标」。因此新增联网点时必须同步维护本表。
/// 少数域名（例如更新反代）的清单掌握在上层项目手里，可通过
/// <see cref="RegisterExtraHosts"/> 在启动阶段补充，避免两处维护。
/// </remarks>
public static class NetworkPurposeCatalog
{
    private static readonly object Sync = new();

    /// <summary>
    /// 内置域名表。运行时通过 <see cref="RegisterExtraHosts"/> 追加的域名不写在这里，
    /// 由 <see cref="_BuildSnapshot"/> 合并进对外暴露的视图。
    /// </summary>
    private static readonly NetworkPurposeInfo[] Defaults =
    [
        new(
            NetworkPurpose.LauncherUpdate,
            "update",
            "启动器更新", "Launcher update",
            "检查新版本、获取公告与增量更新包。", "Check for new versions, announcements and delta updates.",
            "更新检查", "update checking",
            [
                // 更新清单与更新包实际走 GitHub 的三种入口，另有反代在运行时补充
                "raw.githubusercontent.com", "cdn.jsdelivr.net", "api.github.com", "github.com"
            ]
        ),
        new(
            NetworkPurpose.MinecraftDownload,
            "mc-download",
            "Minecraft 下载", "Minecraft download",
            "获取游戏版本清单、资源文件与依赖库，并下载 Java 运行时。",
            "Fetch game manifests, assets and libraries, and download Java runtimes.",
            "安装与启动游戏", "installing and launching the game",
            [
                "launchermeta.mojang.com", "piston-meta.mojang.com", "piston-data.mojang.com",
                "libraries.minecraft.net", "resources.download.minecraft.net",
                "bmclapi2.bangbang93.com", "alist.8mi.tech", "zkitefly.github.io",
                "mirrors.cloud.tencent.com"
            ]
        ),
        new(
            NetworkPurpose.LoaderMetadata,
            "loader-meta",
            "模组加载器", "Mod loaders",
            "查询并下载 Forge、NeoForge、Fabric、LiteLoader、OptiFine 等加载器。",
            "Query and download Forge, NeoForge, Fabric, LiteLoader, OptiFine and friends.",
            "安装模组加载器", "installing mod loaders",
            [
                "files.minecraftforge.net", "maven.minecraftforge.net", "maven.neoforged.net",
                "meta.fabricmc.net", "meta.legacyfabric.net", "dl.liteloader.com",
                "jenkins.liteloader.com", "optifine.net", "releases.r2.labymod.net",
                "releases.labymod.net", "github.com", "api.github.com", "authlib-injector.yushi.moe"
            ]
        ),
        new(
            NetworkPurpose.ModRepository,
            "mod-repo",
            "模组仓库", "Mod repository",
            "搜索与下载模组、整合包、资源包，以及获取中文简介翻译。",
            "Search and download mods, modpacks and resource packs, and fetch translated descriptions.",
            "在线浏览与下载模组", "browsing and downloading mods online",
            [
                "api.modrinth.com", "api.curseforge.com", "cdn.modrinth.com",
                "edge.forgecdn.net", "mediafilez.forgecdn.net", "mod.mcimirror.top"
            ]
        ),
        new(
            NetworkPurpose.DnsResolve,
            "doh",
            "加密 DNS", "Encrypted DNS",
            "通过 DoH 解析域名，避免明文 DNS 泄露访问记录。",
            "Resolve domains over DoH so plaintext DNS cannot leak your browsing.",
            "加密 DNS（关闭后改用系统 DNS）", "encrypted DNS (falls back to system DNS)",
            ["doh.pub", "doh.pysio.online", "cloudflare-dns.com"]
        )
    ];

    /// <summary>运行时追加的域名，按用途分组。</summary>
    private static readonly Dictionary<NetworkPurpose, List<string>> ExtraHosts = [];

    private static volatile Snapshot _snapshot = _BuildSnapshot();

    /// <summary>登记表的只读视图，随 <see cref="RegisterExtraHosts"/> 整体替换。</summary>
    public static IReadOnlyList<NetworkPurposeInfo> All => _snapshot.All;

    /// <summary>
    /// 追加若干域名到某个用途。重复登记会被忽略，可在启动阶段多次调用。
    /// </summary>
    public static void RegisterExtraHosts(NetworkPurpose purpose, IEnumerable<string>? hosts)
    {
        if (hosts is null) return;

        var added = hosts
            .Select(host => host?.Trim().ToLowerInvariant())
            .Where(host => !string.IsNullOrEmpty(host))
            .Select(host => host!)
            .ToList();
        if (added.Count == 0) return;

        lock (Sync)
        {
            if (!ExtraHosts.TryGetValue(purpose, out var list)) ExtraHosts[purpose] = list = [];

            var changed = false;
            foreach (var host in added)
            {
                if (list.Contains(host, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(host);
                changed = true;
            }

            if (changed) _snapshot = _BuildSnapshot();
        }
    }

    /// <summary>
    /// 追加若干完整 URL 的主机名到某个用途，便于把上层持有的地址清单直接接进来。
    /// </summary>
    public static void RegisterExtraHostsFromUrls(NetworkPurpose purpose, IEnumerable<string>? urls)
    {
        if (urls is null) return;
        RegisterExtraHosts(purpose, urls.Select(_HostOfUrl).OfType<string>());
    }

    /// <summary>
    /// 按域名反查用途。一个域名可能同时服务于多个用途（例如同时承载更新包与联机依赖），
    /// 只要其中任一用途获得授权即视为放行。未登记时返回空集合。
    /// </summary>
    public static IReadOnlyList<NetworkPurposeInfo> ByHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return [];
        return _snapshot.ByHost.TryGetValue(host, out var list) ? list : [];
    }

    /// <summary>按枚举取用途信息。</summary>
    public static NetworkPurposeInfo? ByPurpose(NetworkPurpose purpose) =>
        _snapshot.ByPurpose.GetValueOrDefault(purpose);

    /// <summary>按 Id 取用途信息。</summary>
    public static NetworkPurposeInfo? ById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _snapshot.All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static string? _HostOfUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;
    }

    private static Snapshot _BuildSnapshot()
    {
        var all = new NetworkPurposeInfo[Defaults.Length];
        for (var i = 0; i < Defaults.Length; i++)
        {
            var info = Defaults[i];
            if (!ExtraHosts.TryGetValue(info.Purpose, out var extra) || extra.Count == 0)
            {
                all[i] = info;
                continue;
            }

            all[i] = info with
            {
                Hosts = info.Hosts.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        var byHost = new Dictionary<string, List<NetworkPurposeInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in all)
        foreach (var host in info.Hosts)
        {
            if (!byHost.TryGetValue(host, out var list)) byHost[host] = list = [];
            if (!list.Contains(info)) list.Add(info);
        }

        return new Snapshot(all, byHost, all.ToDictionary(x => x.Purpose));
    }

    private sealed record Snapshot(
        NetworkPurposeInfo[] All,
        Dictionary<string, List<NetworkPurposeInfo>> ByHost,
        Dictionary<NetworkPurpose, NetworkPurposeInfo> ByPurpose);
}
