using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dapper;
using Microsoft.Data.Sqlite;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.Utils;
using PCL.Core.Utils.Hash;
using PCL.Network;
using PCL.Network.Loaders;
using ProtoBuf;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public static partial class ModComp
{

    // 输入与输出

    public class CompProjectRequest
    {
        /// <summary>
        ///     筛选 MC 版本。
        /// </summary>
        public string gameVersion = null;

        /// <summary>
        ///     筛选 Mod 加载器类别。
        /// </summary>
        public CompLoaderType modLoader = CompLoaderType.Any;

        /// <summary>
        ///     搜索的文本内容。
        /// </summary>
        public string searchText;

        /// <summary>
        ///     在进行中文搜索时，CurseForge 的替代搜索文本。
        ///     由于 CurseForge API 在有任意关键词未匹配的时候就不显示结果，所以不能使用与 Modrinth 相同的算法。
        /// </summary>
        public string curseForgeAltSearchText;

        /// <summary>
        ///     搜索结果排序方式。
        /// </summary>
        public CompSortType sort = CompSortType.Default;

        /// <summary>
        ///     允许的来源。
        /// </summary>
        public CompSourceType source = CompSourceType.Any;

        // 结果要求

        /// <summary>
        ///     加载后应输出到的结果存储器。
        /// </summary>
        public CompProjectStorage storage;

        /// <summary>
        ///     筛选资源标签。空字符串代表不限制。格式例如 "406/worldgen"，分别是 CurseForge 和 Modrinth 的 ID。
        /// </summary>
        public string tag = "";

        /// <summary>
        ///     应当尽量达成的结果数量。
        /// </summary>
        public int targetResultCount;

        // 输入内容

        /// <summary>
        ///     筛选资源种类。
        /// </summary>
        public CompType type;

        /// <summary>
        ///     构造函数。
        /// </summary>
        public CompProjectRequest(CompType type, CompProjectStorage storage, int targetResultCount)
        {
            this.type = type;
            this.storage = storage;
            this.targetResultCount = targetResultCount;
        }

        /// <summary>
        ///     根据加载位置记录，是否还可以继续获取内容。
        /// </summary>
        public bool CanContinue
        {
            get
            {
                if (tag.StartsWithF("/") || !source.HasFlag(CompSourceType.CurseForge))
                    storage.curseForgeTotal = 0;
                if (tag.EndsWithF("/") || !source.HasFlag(CompSourceType.Modrinth))
                    storage.modrinthTotal = 0;
                if (storage.curseForgeTotal == -1 || storage.modrinthTotal == -1)
                    return true;
                return storage.curseForgeOffset < storage.curseForgeTotal ||
                       storage.modrinthOffset < storage.modrinthTotal;
            }
        }

        // 构造请求

        /// <summary>
        ///     获取对应的 CurseForge API 请求链接。若返回 Nothing 则为不进行 CurseForge 请求。
        /// </summary>
        public string GetCurseForgeAddress()
        {
            if (!source.HasFlag(CompSourceType.CurseForge))
                return null;
            if (tag.StartsWithF("/"))
                storage.curseForgeTotal = 0;
            if (storage.curseForgeTotal > -1 && storage.curseForgeTotal <= storage.curseForgeOffset)
                return null;
            // 应用筛选参数
            var address =
                new StringBuilder(
                    $"https://api.curseforge.com/v1/mods/search?gameId=432&sortOrder=desc&pageSize={compPageSize}");
            switch (type)
            {
                case CompType.Mod:
                {
                    address.Append("&classId=6");
                    break;
                }
                case CompType.ModPack:
                {
                    address.Append("&classId=4471");
                    break;
                }
                case CompType.DataPack:
                {
                    address.Append("&classId=6945");
                    break;
                }
                case CompType.Shader:
                {
                    address.Append("&classId=6552");
                    break;
                }
                case CompType.ResourcePack:
                {
                    address.Append("&classId=12");
                    break;
                }
                case CompType.World:
                {
                    address.Append("&classId=17");
                    break;
                }
            }

            if (!string.IsNullOrEmpty(tag)) address.Append($"&categoryId={tag.BeforeFirst("/")}");
            if (modLoader != CompLoaderType.Any)
                address.Append("&modLoaderType=").Append(((int)modLoader).ToString());
            if (!string.IsNullOrEmpty(gameVersion))
                address.Append("&gameVersion=").Append(gameVersion);
            if (!string.IsNullOrEmpty(curseForgeAltSearchText ?? searchText))
                address.Append("&searchFilter=").Append(WebUtility.UrlEncode(curseForgeAltSearchText ?? searchText));
            if (storage.curseForgeOffset > 0)
                address.Append("&index=").Append(storage.curseForgeOffset);
            switch (sort)
            {
                case CompSortType.Relevance:
                {
                    address.Append("&sortField=4");
                    break;
                }
                case CompSortType.Downloads:
                {
                    address.Append("&sortField=6");
                    break;
                }
                case CompSortType.Follows:
                {
                    address.Append("&sortField=2");
                    break;
                }
                case CompSortType.Newest:
                {
                    address.Append("&sortField=11");
                    break;
                }
                case CompSortType.Updated:
                {
                    address.Append("&sortField=3");
                    break;
                }

                default:
                {
                    address.Append("&sortField=2");
                    break;
                }
            }

            return address.ToString();
        }

        /// <summary>
        ///     获取对应的 Modrinth API 请求链接。若返回 Nothing 则为不进行 Modrinth 请求。
        /// </summary>
        public string GetModrinthAddress()
        {
            if (!source.HasFlag(CompSourceType.Modrinth))
                return null;
            if (tag.EndsWithF("/"))
                storage.modrinthTotal = 0;
            if (storage.modrinthTotal > -1 && storage.modrinthTotal <= storage.modrinthOffset)
                return null;
            // 应用筛选参数
            var address = $"https://api.modrinth.com/v2/search?limit={compPageSize}";
            switch (sort)
            {
                case CompSortType.Relevance:
                {
                    address += "&index=relevance";
                    break;
                }
                case CompSortType.Downloads:
                {
                    address += "&index=downloads";
                    break;
                }
                case CompSortType.Follows:
                {
                    address += "&index=follows";
                    break;
                }
                case CompSortType.Newest:
                {
                    address += "&index=newest";
                    break;
                }
                case CompSortType.Updated:
                {
                    address += "&index=updated";
                    break;
                }

                default:
                {
                    address += "&index=relevance";
                    break;
                }
            }

            if (!string.IsNullOrEmpty(searchText))
                address += "&query=" + WebUtility.UrlEncode(searchText);
            if (storage.modrinthOffset > 0)
                address += "&offset=" + storage.modrinthOffset;
            // facets=[["categories:'game-mechanics'"],["categories:'forge'"],["versions:1.19.3"],["project_type:mod"]]
            var facets = new List<string>();
            facets.Add($"[\"project_type:{ModBase.GetStringFromEnum(type).ToLower()}\"]");
            if (!string.IsNullOrEmpty(tag))
                facets.Add($"[\"categories:'{tag.AfterLast("/")}'\"]");
            if (modLoader != CompLoaderType.Any)
                facets.Add($"[\"categories:'{ModBase.GetStringFromEnum(modLoader).ToLower()}'\"]");
            if (!string.IsNullOrEmpty(gameVersion))
                facets.Add($"[\"versions:'{gameVersion}'\"]");
            address += "&facets=[" + string.Join(",", facets) + "]";
            return address;
        }

        // 相同判断
        public override bool Equals(object obj)
        {
            var request = obj as CompProjectRequest;
            return request is not null && type == request.type && targetResultCount == request.targetResultCount &&
                   (tag ?? "") == (request.tag ?? "") && modLoader == request.modLoader && source == request.source &&
                   (gameVersion ?? "") == (request.gameVersion ?? "") &&
                   (searchText ?? "") == (request.searchText ?? "") && sort == request.sort;
        }

        public override int GetHashCode() => HashCode.Combine(type, targetResultCount, tag ?? "", modLoader,
            source, gameVersion ?? "", searchText ?? "", sort);

        public static bool operator ==(CompProjectRequest left, CompProjectRequest right)
        {
            return EqualityComparer<CompProjectRequest>.Default.Equals(left, right);
        }

        public static bool operator !=(CompProjectRequest left, CompProjectRequest right)
        {
            return !(left == right);
        }
    }


    public class CompProjectStorage
    {
        // 加载位置记录

        public int curseForgeOffset;
        public int curseForgeTotal = -1;

        /// <summary>
        ///     当前的错误信息。如果没有则为 Nothing。
        /// </summary>
        public string errorMessage = null;

        public int modrinthOffset;
        public int modrinthTotal = -1;

        // 结果列表

        /// <summary>
        ///     可供展示的所有工程的列表。
        /// </summary>
        public List<CompProject> results = new();
    }


    private enum ResourceSite { None, CurseForge, Modrinth }


    /// <summary>
    /// 用 Uri 解析单个 token 是否为受支持的 CurseForge/Modrinth 资源链接。
    /// 成功时输出站点、分类段与 slug（query、fragment 会被自动丢弃）。
    /// </summary>
    private static bool TryParseResourceLink(string token, out ResourceSite site, out string category, out string slug)
    {
        site = ResourceSite.None;
        category = string.Empty;
        slug = string.Empty;

        // 容忍无协议前缀（如直接粘贴 www.curseforge.com/...）：原样试，再补 https:// 试
        if (!Uri.TryCreate(token, UriKind.Absolute, out var uri) &&
            !Uri.TryCreate($"https://{token}", UriKind.Absolute, out uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // 仅取 path 段，天然忽略 ?query 与 #fragment
        var segments = uri.AbsolutePath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);

        // CurseForge: /minecraft/{category}/{slug}
        if (IsHostOf(uri.Host, "curseforge.com"))
        {
            if (segments.Length < 3 || !segments[0].Equals("minecraft", StringComparison.OrdinalIgnoreCase)) return false;
            site = ResourceSite.CurseForge;
            category = segments[1];
            slug = segments[2];
            return true;
        }

        // Modrinth: /{type}/{slug}
        if (IsHostOf(uri.Host, "modrinth.com"))
        {
            if (segments.Length < 2) return false;
            site = ResourceSite.Modrinth;
            category = segments[0]; // 类型段，当前解析未使用
            slug = segments[1];
            return true;
        }

        return false;
    }


    /// <summary>host 等于 domain 或为其子域，避免 evilcurseforge.com 之类的伪装域名。</summary>
    private static bool IsHostOf(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);


    /// <summary>从文本中找出首个可识别的资源链接 token，找不到返回 null。</summary>
    private static string? FindFirstResourceLinkToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var token in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (TryParseResourceLink(token, out _, out _, out _)) return token;
        return null;
    }


    /// <summary>
    /// 从单条 CurseForge / Modrinth 资源链接解析出 projectId。无法识别或获取失败时返回 null。
    /// </summary>
    public static string? ResolveLinkToProjectId(string url)
    {
        // 纯链接（搜索框已抽出的单 token）直接命中；含周围文本（如剪贴板整段）时回退取首个链接
        if (!TryParseResourceLink(url, out var site, out var category, out var slug))
        {
            var token = FindFirstResourceLinkToken(url);
            if (token is null || !TryParseResourceLink(token, out site, out category, out slug)) return null;
        }

        if (site == ResourceSite.CurseForge)
        {
            var encodedSlug = WebUtility.UrlEncode(slug);
            var json = ModDownload.DlModRequest<JsonObject>(
                $"https://api.curseforge.com/v1/mods/search?gameId=432&slug={encodedSlug}");
            var dataArray = (JsonArray)json["data"];
            if (!dataArray.Any()) return null;

            var receivedClassId = ((JsonObject)dataArray[0])["classId"]?.ToString();
            if (!curseForgeCategoryClassIds.TryGetValue(category, out var targetClassId) ||
                receivedClassId == targetClassId)
                return dataArray[0]["id"]?.ToString();

            // 分类不符：带 classId 重搜。结果与首次查询语义不同，使用独立变量
            var filteredJson = ModDownload.DlModRequest<JsonObject>(
                $"https://api.curseforge.com/v1/mods/search?gameId=432&slug={encodedSlug}&classId={targetClassId}");
            var filteredDatas = (JsonArray)filteredJson["data"];
            return filteredDatas.Any() ? filteredDatas[0]["id"]?.ToString() : null;
        }

        // Modrinth：slug 进 path，用 EscapeDataString
        var mr = ModDownload.DlModRequest<JsonObject>(
            $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(slug)}");
        return mr["id"]?.ToString();
    }


    /// <summary>
    /// 若输入文本中恰好含 1 条 CurseForge/Modrinth 资源链接，返回该链接；含 0 条或 ≥2 条时返回 null。
    /// </summary>
    public static string? TryExtractSingleResourceLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string? found = null;
        foreach (var token in tokens)
        {
            if (!TryParseResourceLink(token, out _, out _, out _)) continue;
            if (found is not null) return null; // ≥2 条链接，一条也不识别
            found = token;
        }
        return found; // 恰好 1 条返回该链接；0 条返回 null
    }

}
