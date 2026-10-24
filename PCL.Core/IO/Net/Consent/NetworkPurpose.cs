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

    /// <summary>EasyTier 联机中继与节点发现。</summary>
    EasyTier,

    /// <summary>联机大厅账号。</summary>
    LobbyAccount,

    /// <summary>加密 DNS 解析。</summary>
    DnsResolve,

    /// <summary>遥测与崩溃上报。</summary>
    Telemetry,

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
public static class NetworkPurposeCatalog
{
    /// <summary>
    /// 自建更新服务地址。
    /// TODO: 替换为 PCL Lite 实际的更新域名。
    /// </summary>
    public const string UpdateHost = "update.pcl-lite.dev";

    private static readonly NetworkPurposeInfo[] _all =
    [
        new(
            NetworkPurpose.LauncherUpdate,
            "update",
            "启动器更新", "Launcher update",
            "检查新版本、获取公告与增量更新包。", "Check for new versions, announcements and delta updates.",
            "更新检查", "update checking",
            [
                UpdateHost,
                // 过渡期保留现有更新源，替换为自建 link 后可删除以下三项
                "s3.pysio.online", "staticassets.naids.com", "github.com"
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
                "bmclapi2.bangbang93.com", "alist.8mi.tech"
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
                "files.minecraftforge.net", "maven.neoforged.net", "meta.fabricmc.net",
                "meta.legacyfabric.net", "dl.liteloader.com", "optifine.net",
                "releases.r2.labymod.net", "github.com", "authlib-injector.yushi.moe"
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
            NetworkPurpose.EasyTier,
            "easytier",
            "EasyTier 联机", "EasyTier multiplayer",
            "连接 P2P 中继节点、获取可用节点列表，并下载 EasyTier 运行文件。",
            "Connect to P2P relays, fetch available node lists, and download the EasyTier runtime.",
            "联机功能", "multiplayer",
            ["etnode.zkitefly.eu.org", "uptime.easytier.cn", "staticassets.naids.com"]
        ),
        new(
            NetworkPurpose.LobbyAccount,
            "lobby-account",
            "联机账号", "Lobby account",
            "登录联机大厅账号，并同步房间信息。",
            "Sign in to the lobby account and sync room information.",
            "需要登录的联机大厅", "multiplayer lobbies that require sign-in",
            ["account.naids.com", "pcl2ce.pysio.online"]
        ),
        new(
            NetworkPurpose.DnsResolve,
            "doh",
            "加密 DNS", "Encrypted DNS",
            "通过 DoH 解析域名，避免明文 DNS 泄露访问记录。",
            "Resolve domains over DoH so plaintext DNS cannot leak your browsing.",
            "加密 DNS（关闭后改用系统 DNS）", "encrypted DNS (falls back to system DNS)",
            ["doh.pub", "doh.pysio.online", "cloudflare-dns.com"]
        ),
        new(
            NetworkPurpose.Telemetry,
            "telemetry",
            "遥测", "Telemetry",
            "上报崩溃堆栈与设备环境，用于定位问题。",
            "Report crash traces and device environment to help diagnose issues.",
            "崩溃上报", "crash reporting",
            []
        )
    ];

    private static readonly Dictionary<string, List<NetworkPurposeInfo>> _byHost = _BuildHostMap();
    private static readonly Dictionary<NetworkPurpose, NetworkPurposeInfo> _byPurpose =
        _all.ToDictionary(x => x.Purpose);

    public static IReadOnlyList<NetworkPurposeInfo> All => _all;

    private static Dictionary<string, List<NetworkPurposeInfo>> _BuildHostMap()
    {
        var map = new Dictionary<string, List<NetworkPurposeInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in _all)
        foreach (var host in info.Hosts)
        {
            if (!map.TryGetValue(host, out var list))
                map[host] = list = [];
            if (!list.Contains(info)) list.Add(info);
        }

        return map;
    }

    /// <summary>
    /// 按域名反查用途。一个域名可能同时服务于多个用途（例如同时承载更新包与联机依赖），
    /// 只要其中任一用途获得授权即视为放行。未登记时返回空集合。
    /// </summary>
    public static IReadOnlyList<NetworkPurposeInfo> ByHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return [];
        return _byHost.TryGetValue(host, out var list) ? list : [];
    }

    /// <summary>按枚举取用途信息。</summary>
    public static NetworkPurposeInfo? ByPurpose(NetworkPurpose purpose) => _byPurpose.GetValueOrDefault(purpose);

    /// <summary>按 Id 取用途信息。</summary>
    public static NetworkPurposeInfo? ById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _all.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
