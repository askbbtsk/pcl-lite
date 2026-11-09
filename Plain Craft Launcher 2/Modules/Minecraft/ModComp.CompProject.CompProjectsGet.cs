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

    /// <summary>
    ///     根据搜索请求获取一系列的工程列表。需要基于加载器运行。
    /// </summary>
    public static void CompProjectsGet(ModLoader.LoaderTask<CompProjectRequest, int> task)
    {
        var request = task.input;
        var storage = request.storage;

        // === Issue #2942: 搜索框单条资源链接识别 ===
        var singleLink = TryExtractSingleResourceLink(request.searchText);
        if (singleLink is not null)
        {
            // 已得到结果则直接结束（幂等，避免重复获取）
            if (storage.results.Any()) return;

            CompProject? project;
            try
            {
                var projectId = ResolveLinkToProjectId(singleLink);
                project = string.IsNullOrEmpty(projectId)
                    ? null
                    : CompRequest.GetCompProjectsByIds(new List<string> { projectId }).FirstOrDefault();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Comp] 解析资源链接失败");
                throw new Exception(Lang.Text("Download.Comp.Link.ResolveFailed"));
            }

            // 解析或获取失败 → 提示
            if (project is null)
                throw new Exception(Lang.Text("Download.Comp.Link.ResolveFailed"));

            // 类型与当前页不符 → 按无结果处理（与既有"无匹配结果"一致）
            if (request.type != CompType.Any && project.Type != request.type)
                throw new Exception(Lang.Text("Download.Comp.List.NoMatchingResults"));

            // 命中：单条结果，隐藏分页
            storage.results.Add(project);
            storage.curseForgeTotal = 0;
            storage.modrinthTotal = 0;
            return;
        }
        // === /Issue #2942 ===


        if (storage.results.Count >= request.targetResultCount)
        {
            LogWrapper.Info($"[Comp] 已有 {storage.results.Count} 个结果，多于所需的 {request.targetResultCount} 个结果，结束处理");
            return;
        }

        if (!request.CanContinue)
        {
            if (!storage.results.Any()) throw new Exception(Lang.Text("Download.Comp.List.NoMatchingResults"));
            LogWrapper.Info(
                $"[Comp] 已有 {storage.results.Count} 个结果，少于所需的 {request.targetResultCount} 个结果，但无法继续获取，结束处理");
            return;
        }

        // 拒绝不支持的版本
        if (request.modLoader == CompLoaderType.Quilt &&
            McVersionComparer.CompareVersion(request.gameVersion ?? "1.15", "1.14") == -1)
                throw new Exception(Lang.Text("Minecraft.Error.QuiltUnsupported", request.gameVersion));



        var rawFilter = (request.searchText ?? "").Trim();
        request.searchText = rawFilter;
        var rawFilterLower = rawFilter.ToLower();
        LogWrapper.Info("[Comp] 工程列表搜索原始文本：" + rawFilter);

        // 中文请求关键字处理
        var isChineseSearch = Lang.IsChineseMainland &&
                              RegexPatterns.HasChineseChar.IsMatch(rawFilter) &&
                              !string.IsNullOrEmpty(rawFilter);
        if (isChineseSearch && request.type is CompType.Mod or CompType.DataPack)
        {
            var searchEntries = new List<ModBase.SearchEntry<CompDatabaseEntry>>();
            using (var conn = CompDB)
            {
                var likeEscaped = rawFilter.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                var searchRes = conn.Query<CompDatabaseEntry>(
                    "SELECT * FROM ModTranslation WHERE ChineseName LIKE @p ESCAPE '\\' OR CurseForgeSlug LIKE @p ESCAPE '\\' OR ModrinthSlug LIKE @p ESCAPE '\\'",
                    new { p = $"%{likeEscaped}%" }).ToList();
                foreach (var searchItem in searchRes)
                {
                    if (searchItem.ChineseName.Contains("动态的树")) continue;
                    searchEntries.Add(new ModBase.SearchEntry<CompDatabaseEntry>
                    {
                        item = searchItem,
                        searchSource = new List<ModBase.SearchSource>
                        {
                            new(searchItem.ChineseName.BeforeFirst(" (").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries), 1),
                            new(searchItem.ChineseName.AfterFirst(" (") + (searchItem.CurseForgeSlug ?? "") + (searchItem.ModrinthSlug ?? ""), 0.5)
                        }
                    });
                }
            }

            var searchResults = ModBase.Search(searchEntries, request.searchText, 40, 0.2);
            if (!searchResults.Any()) throw new Exception(Lang.Text("Download.Comp.List.NoResults"));

            string[] ExtractWords(ModBase.SearchEntry<CompDatabaseEntry> result)
            {
                var word = "";
                if (result.item.CurseForgeSlug is not null)
                    word += result.item.CurseForgeSlug.Replace("-", " ").Replace("/", " ") + " ";
                if (result.item.ModrinthSlug is not null)
                    word += result.item.ModrinthSlug.Replace("-", " ").Replace("/", " ") + " ";
                word += result.item.ChineseName.AfterLast(" (").TrimEnd(')', ' ').BeforeFirst(" - ")
                    .Replace(":", "").Replace("(", "").Replace(")", "").ToLower().Replace("/", " ").Replace("-", " ");
                var words = word.ToLower().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                words = words.Select(w => w.TrimStart('{', '[', '(').TrimEnd('}', ']', ')')).Where(
                    w =>
                    {
                        if (w.Length <= 1) return false;
                        if (!w.Any(char.IsLetterOrDigit)) return false;
                        if (new[] { "the", "of", "for", "mod", "and", "forge", "fabric", "quilt", "neoforge" }.Contains(w)) return false;
                        if (ModBase.Val(w) > 0) return false;
                        if (w.Split(' ').Length > 3 && w.Contains("ftb")) return false;
                        return true;
                    }).Distinct().ToArray();
                return words;
            }

            var wordModCount = new Dictionary<string, HashSet<int>>();
            foreach (var result in searchResults)
                foreach (var word in ExtractWords(result))
                {
                    if (!wordModCount.TryGetValue(word, out var mods))
                        wordModCount[word] = mods = new HashSet<int>();
                    mods.Add(result.item.WikiId);
                }

            if (wordModCount.Count == 0) throw new Exception(Lang.Text("Download.Comp.List.NoResults"));

            static string NormalizeName(string s) =>
                new string(s.Where(c => !char.IsWhiteSpace(c) && !char.IsSurrogate(c)).ToArray());
            string CanonName(string name) => NormalizeName(name.BeforeFirst(" ("));
            var normalizedQuery = NormalizeName(rawFilter);
            var exactNameEntries = searchResults
                .Where(r => CanonName(r.item.ChineseName) == normalizedQuery).ToList();
            var exactNameMods = exactNameEntries.Select(r => r.item.WikiId).Distinct().Count();

            if (exactNameMods == 1)
            {
                var canonicalEntry = exactNameEntries
                    .OrderByDescending(r => r.absoluteRight)
                    .ThenByDescending(r => r.similarity)
                    .ThenBy(r => (r.item.CurseForgeSlug ?? r.item.ModrinthSlug ?? r.item.ChineseName).Length)
                    .First();
                var canonicalWords = ExtractWords(canonicalEntry);
                request.searchText = canonicalWords.Any()
                    ? string.Join(" ", canonicalWords)
                    : string.Join(" ", wordModCount.OrderByDescending(w => w.Value.Count).Take(2).Select(w => w.Key));
                var cfSlugs = exactNameEntries.Select(r => r.item.CurseForgeSlug)
                    .Where(s => s is not null).Distinct().ToList();
                if (cfSlugs.Count == 1)
                    request.curseForgeAltSearchText = cfSlugs[0];
            }
            else
            {
                var maxCount = wordModCount.Values.Max(mods => mods.Count);
                if (maxCount <= 1)
                {
                    var best = searchResults
                        .OrderByDescending(r => r.absoluteRight)
                        .ThenByDescending(r => r.similarity)
                        .ThenBy(r => (r.item.CurseForgeSlug ?? r.item.ModrinthSlug ?? r.item.ChineseName).Length)
                        .First();
                    request.searchText = string.Join(" ", ExtractWords(best));
                }
                else
                {
                    var tied = wordModCount
                        .Where(w => w.Value.Count == maxCount)
                        .OrderBy(w => w.Key.Length)
                        .ToList();
                    var anchorMods = tied[0].Value;
                    request.searchText = string.Join(" ", tied
                        .Where(w => w.Value.Overlaps(anchorMods))
                        .Take(3)
                        .Select(w => w.Key));
                }
            }
            LogWrapper.Debug("[Comp] 中文搜索基础关键词：" + request.searchText);
        }

        // 最终处理关键字：分割、去重
        void processKeywords(ref string text)
        {
            if (text is null) return;
            text = text.ToLowerInvariant();
            var words = new List<string>();
            foreach (var keyword in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var cleanKeyword = keyword.Trim('[', ']');
                if (string.IsNullOrEmpty(cleanKeyword)) continue;
                if (new[] { "forge", "fabric", "for", "mod", "quilt" }.Contains(cleanKeyword))
                {
                    LogWrapper.Debug("[Comp] 已跳过搜索关键词：" + cleanKeyword);
                    continue;
                }

                words.Add(cleanKeyword);
            }

            if (rawFilter.Length > 0 && !words.Any())
                text = rawFilter;
            else
                text = string.Join(" ", words.Distinct());

            // 例外项：OptiForge、OptiFabric（拆词后因为包含 Forge/Fabric 导致无法搜到实际的 Mod）
            if (rawFilter.Replace(" ", "").ContainsF("optiforge", true)) text = "optiforge";
            if (rawFilter.Replace(" ", "").ContainsF("optifabric", true)) text = "optifabric";
        }

        if (request.curseForgeAltSearchText is not null)
        {
            processKeywords(ref request.curseForgeAltSearchText);
            LogWrapper.Debug("[Comp] 工程列表搜索最终文本（CurseForge）：" + request.curseForgeAltSearchText);
        }

        processKeywords(ref request.searchText);
        LogWrapper.Debug("[Comp] 工程列表搜索最终文本：" + request.searchText);
        task.Progress = 0.1;


        var realResults = new List<CompProject>();


        while (true)
        {
            var rawResults = new List<CompProject>();
            Exception lastError = null;
            var resultsLock = new object();

            // 1.14 以下 Forge 筛选处理
            var isOldForgeRequest = request.modLoader == CompLoaderType.Forge &&
                                    McInstanceInfo.VersionToDrop(request.gameVersion, true) < 140;
            if (isOldForgeRequest) request.modLoader = CompLoaderType.Any;
            var curseForgeUrl = request.GetCurseForgeAddress();
            var modrinthUrl = request.GetModrinthAddress();
            if (isOldForgeRequest) request.modLoader = CompLoaderType.Forge;

            var tasks = new List<Task>();

            // CurseForge 线程内嵌
            if (curseForgeUrl is not null)
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        LogWrapper.Info("[Comp] 开始从 CurseForge 获取列表：" + curseForgeUrl);
                        var json = ModDownload.DlModRequest<JsonObject>(curseForgeUrl);
                        var projects = json["data"].AsArray().Select(j => new CompProject((JsonObject)j))
                            .Where(p => !(request.type == CompType.ResourcePack && p.Tags.Contains(Lang.Text("Download.Comp.Type.DataPack"))))
                            .ToList();
                        lock (resultsLock)
                        {
                            rawResults.AddRange(projects);
                        }

                        storage.curseForgeOffset += projects.Count;
                        storage.curseForgeTotal = json["pagination"]["totalCount"].ToObject<int>();
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        LogWrapper.Error(ex, "CurseForge 获取失败");
                    }
                }));

            // Modrinth 线程内嵌
            if (modrinthUrl is not null)
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        LogWrapper.Info("[Comp] 开始从 Modrinth 获取列表：" + modrinthUrl);
                        var json = ModDownload.DlModRequest<JsonObject>(modrinthUrl);
                        var projects = json["hits"].AsArray().Select(j => new CompProject((JsonObject)j)).ToList();
                        lock (resultsLock)
                        {
                            rawResults.AddRange(projects);
                        }

                        storage.modrinthOffset += projects.Count;
                        storage.modrinthTotal = json["total_hits"].ToObject<int>();
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        LogWrapper.Error(ex, "Modrinth 获取失败");
                    }
                }));

            Task.WaitAll(tasks.ToArray());
            task.Progress += 0.4;
            if (task.IsAborted) return;

            // 过滤老版本 Forge
            if (isOldForgeRequest)
                rawResults = rawResults.Where(p => !p.ModLoaders.Any() || p.ModLoaders.Contains(CompLoaderType.Forge))
                    .ToList();

            // 错误检查与空结果处理
            if (!rawResults.Any())
            {
                if (lastError is not null) throw lastError;
                // 处理各平台不兼容报错... (此处省略具体 Exception 文本以保持简略)
                throw new Exception(Lang.Text("Download.Comp.List.NoResultsSimple"));
            }


            // 优先保留 Modrinth 顺序并去重
            var processedResults = rawResults.OrderBy(x => x.FromCurseForge)
                .Where(r => !realResults.Any(b => r.IsLike(b)) && !storage.results.Any(b => r.IsLike(b)))
                .ToList();

            realResults.AddRange(processedResults);
            LogWrapper.Info($"[Comp] 去重、筛选后累计新增结果 {processedResults.Count} 个（目前已有结果 {storage.results.Count} 个）");

            if (realResults.Count + storage.results.Count < request.targetResultCount && request.CanContinue &&
                lastError is null)
            {
                LogWrapper.Info("[Comp] 数量不足，继续加载下一页");
                continue;
            }

            break;

        }



        if (request.sort != CompSortType.Default)
        {
            if (task.IsAborted) throw new ThreadInterruptedException();
            storage.results.AddRange(realResults); // 遵从API返回顺序
            return;
        }
        
        var scores = new Dictionary<CompProject, double>();
        Func<CompProject, double> getDownloadCountMult = p =>
        {
            switch (request.type)
            {
                case CompType.Mod:
                case CompType.ModPack: return p.FromCurseForge ? 1 : 7;
                case CompType.DataPack: return p.FromCurseForge ? 10 : 1;
                case CompType.ResourcePack:
                case CompType.Shader: return p.FromCurseForge ? 1 : 5;
                default: return 1;
            }
        };

        if (string.IsNullOrEmpty(rawFilter))
        {
            foreach (var res in realResults) scores.Add(res, res.DownloadCount * getDownloadCountMult(res));
        }
        else
        {
            var searchEntries = new List<ModBase.SearchEntry<CompProject>>();
            foreach (var res in realResults)
            {
                scores.Add(res,
                    (Lang.IsChineseMainland && res.WikiId > 0 ? 0.2 : 0) +
                    Math.Log10(Math.Max(res.DownloadCount, 1) * getDownloadCountMult(res)) / 9);
                searchEntries.Add(new ModBase.SearchEntry<CompProject>
                {
                    item = res,
                    searchSource = new List<ModBase.SearchSource>
                    {
                        new((isChineseSearch ? res.TranslatedName : res.RawName).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries), 1),
                        new(res.Description, 0.05)
                    }
                });
            }

            var searchRes = ModBase.Search(searchEntries, rawFilter, 101, -1);
            foreach (var item in searchRes)
                scores[item.item] +=
                    (item.absoluteRight ? 10 : item.similarity) /
                    (searchRes.First().absoluteRight ? 10 : searchRes.First().similarity);
        }

        if (task.IsAborted) throw new ThreadInterruptedException();
        storage.results.AddRange(scores.OrderByDescending(s => s.Value).Select(s => s.Key));

    }

}
