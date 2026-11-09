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
public partial class CompProject
{

        private sealed class CompProjectBuildResult
        {
            public bool FromCurseForge;
            public CompType Type;
            public string Slug;
            public string Id;
            public List<int> CurseForgeFileIds = [];
            public string RawName;
            public string Description;
            public string Website;
            public DateTime? LastUpdate;
            public int DownloadCount;
            public List<CompLoaderType> ModLoaders = [];
            public List<string> Tags = [];
            public string LogoUrl;
            public List<int> Drops = [];
        }


        private static CompProjectBuildResult _BuildFromCompJson(JsonObject data)
        {
            var result = new CompProjectBuildResult
            {
                FromCurseForge = (string)data["DataSource"] == "CurseForge",
                Type = (CompType)data["Type"].ToObject<int>(),
                Slug = (string)data["Slug"],
                Id = (string)data["Id"],
                RawName = (string)data["RawName"],
                Description = (string)data["Description"],
                Website = (string)data["Website"],
                DownloadCount = (int)data["DownloadCount"],
                Tags = ((JsonArray)data["Tags"]).Select(t => t.ToString()).ToList()
            };

            if (data.TryGetPropertyValue("CurseForgeFileIds", out var id))
                result.CurseForgeFileIds = ((JsonArray)id).Select(t => t.ToObject<int>()).ToList();

            if (data.TryGetPropertyValue("LastUpdate", out var last))
                result.LastUpdate = last?.ToObject<DateTime>();

            if (data.TryGetPropertyValue("ModLoaders", out var loaders))
                result.ModLoaders = ((JsonArray)loaders).Select(t => (CompLoaderType)t.ToObject<int>())
                    .ToList();

            if (data.TryGetPropertyValue("LogoUrl", out var url))
                result.LogoUrl = (string)url;

            if (data.TryGetPropertyValue("Drops", out var drops))
                result.Drops = ((JsonArray)drops).Select(t => t.ToObject<int>()).ToList();

            return result;
        }


        private static CompProjectBuildResult _BuildFromCurseForge(JsonObject data)
        {
            var result = new CompProjectBuildResult
            {
                FromCurseForge = true,

                // 简单信息
                Id = data["id"].ToString(),
                Slug = (string)data["slug"],
                RawName = (string)data["name"],
                Description = (string)data["summary"],
                Website = (data["links"]?["websiteUrl"]?.ToString() ?? "").TrimEnd('/'),
                LastUpdate = data["dateReleased"]?.ToObject<DateTime>(), // #1194
                DownloadCount = (int)data["downloadCount"]
            };

            if (data["logo"] is JsonObject { Count: > 0 } logo)
                result.LogoUrl = string.IsNullOrEmpty((string)logo["thumbnailUrl"])
                    ? (string)logo["url"]
                    : (string)logo["thumbnailUrl"];

            if (string.IsNullOrEmpty(result.LogoUrl))
                result.LogoUrl = null;

            // Type
            result.Type = _GetCurseForgeTypeByWebsite(result.Website);

            // FileIndexes / VanillaMajorVersions / ModLoaders
            var files = new List<KeyValuePair<int, List<string>>>(); // FileId, GameVersions

            foreach (var file in (data["latestFiles"] as JsonArray) ?? [])
            {
                var newFile = new CompFile((JsonObject)file, result.Type);
                if (!newFile.Available)
                    continue;

                result.ModLoaders.AddRange(newFile.ModLoaders);

                var gameVersions = file["gameVersions"]?.ToObject<List<string>>() ?? [];
                if (!gameVersions.Any(McInstanceInfo.IsFormatFit))
                    continue;

                files.Add(new KeyValuePair<int, List<string>>((int)file["id"], gameVersions));
            }

            files.AddRange(
                from File in (data["latestFilesIndexes"] as JsonArray) ?? []
                let GameVersion = File["gameVersion"]?.ToString() ?? ""
                where McInstanceInfo.IsFormatFit(GameVersion)
                select new KeyValuePair<int, List<string>>((int)File["fileId"], new[] { GameVersion }.ToList())
            );

            result.CurseForgeFileIds = files
                .Select(f => f.Key)
                .Distinct()
                .ToList();

            result.Drops = files
                .SelectMany(f => f.Value)
                .Select(v => McInstanceInfo.VersionToDrop(v))
                .Where(v => v > 0)
                .Distinct()
                .OrderByDescending(v => v)
                .ToList();

            result.ModLoaders = result.ModLoaders
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            // Tags
            var categories = ((data["categories"] as JsonArray) ?? [])
                .Select(t => t["id"]?.ToObject<int?>())
                .Where(t => t.HasValue)
                .Select(t => t.Value)
                .Distinct()
                .OrderByDescending(t => t);

            foreach (var category in categories)
                if (curseForgeCategoryLangKeys.TryGetValue(category, out var langKey))
                    _AddTag(result.Tags, langKey);

            return result;
        }


        private static CompProjectBuildResult _BuildFromModrinth(JsonObject data)
        {
            var projectType = data["project_type"]?.ToString() ?? "";
            var slug = (string)data["slug"];

            var result = new CompProjectBuildResult
            {
                FromCurseForge = false,

                // 简单信息
                Id = (string)(data["project_id"] ?? data["id"]), // 两个 API 会返回的 key 不一样
                Slug = slug,
                RawName = (string)data["title"],
                Description = (string)data["description"],
                LastUpdate = data["date_modified"]?.ToObject<DateTime>(),
                DownloadCount = (int)data["downloads"],
                LogoUrl = (string)data["icon_url"],
                Website = $"https://modrinth.com/{projectType}/{slug}",

                // Type
                Type = projectType switch
                {
                    "modpack" => CompType.ModPack,
                    "resourcepack" => CompType.ResourcePack,
                    "shader" => CompType.Shader,
                    _ => CompType.Mod // Modrinth 将数据包标为 Mod
                }
            };

            if (string.IsNullOrEmpty(result.LogoUrl))
                result.LogoUrl = null;

            // GameVersions
            // 搜索结果的键为 versions，获取特定工程的键为 game_versions
            result.Drops = ((data["game_versions"] ?? data["versions"]) as JsonArray ?? [])
                .Select(v => McInstanceInfo.VersionToDrop((string)v))
                .Where(v => v > 0)
                .Distinct()
                .OrderByDescending(v => v)
                .ToList();

            // Tags & ModLoaders
            foreach (var category in (data["loaders"] as JsonArray)?.Select(t => t.ToString()) ?? [])
                if (modrinthLoaderTypes.TryGetValue(category ?? "", out var loader))
                    result.ModLoaders.Add(loader);

            foreach (var category in (data["categories"] as JsonArray)?.Select(t => t.ToString()) ?? [])
            {
                if (string.IsNullOrEmpty(category)) continue;
                // 加载器
                if (modrinthLoaderTypes.TryGetValue(category, out var loader))
                {
                    result.ModLoaders.Add(loader);
                }
                else
                {
                    // Modrinth 将数据包标为 Mod，若包含数据包版本，则优先标为 DataPack
                    if (category.Equals("datapack", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Type = CompType.DataPack;
                    }
                    else
                    {
                        // 这些分类在资源包中不显示
                        if (resourcePackHiddenCategoryLangKeys.TryGetValue(category, out var hiddenLangKey))
                        {
                            if (result.Type != CompType.ResourcePack)
                                _AddTag(result.Tags, hiddenLangKey);
                        }
                        else
                        {
                            if (modrinthCategoryLangKeys.TryGetValue(category, out var langKey))
                                _AddTag(result.Tags, langKey);
                        }
                    }
                }
            }

            return result;
        }


        private static CompType _GetCurseForgeTypeByWebsite(string website)
        {
            var websiteLower = (website ?? "").ToLowerInvariant();

            if (websiteLower.Contains("/mc-mods/") || websiteLower.Contains("/mod/"))
                return CompType.Mod;
            if (websiteLower.Contains("/modpacks/"))
                return CompType.ModPack;
            if (websiteLower.Contains("/resourcepacks/") || websiteLower.Contains("/texture-packs/"))
                return CompType.ResourcePack;
            if (websiteLower.Contains("/shaders/"))
                return CompType.Shader;
            if (websiteLower.Contains("/worlds/"))
                return CompType.World;

            return CompType.DataPack;
        }


        private static void _AddTag(List<string> tags, string langKey)
        {
            var tag = Lang.Text(langKey);

            if (!tags.Contains(tag))
                tags.Add(tag);
        }


        /// <summary>
        ///     关联的数据库条目。若为 Nothing 则没有。
        /// </summary>
        private CompDatabaseEntry DatabaseEntry
        {
            get
            {
                if (!loadedDatabase)
                {
                    loadedDatabase = true;
                    if (Type == CompType.Mod || Type == CompType.DataPack)
                        _DatabaseEntry = GetCompWikiEntryBySlug(Slug);
                }

                return _DatabaseEntry;
            }
            set
            {
                loadedDatabase = true;
                _DatabaseEntry = value;
            }
        }


        /// <summary>
        ///     MC 百科的页面 ID。若为 0 则没有。
        /// </summary>
        public int WikiId => DatabaseEntry is null ? 0 : DatabaseEntry.WikiId;


        /// <summary>
        ///     翻译后的中文名。若数据库没有则等同于 RawName。
        /// </summary>
        public string TranslatedName =>
            Lang.IsChineseMainland && DatabaseEntry?.ChineseName is { Length: > 0 } cn
                ? cn
                : RawName;


        /// <summary>
        ///     中文描述。若为 Nothing 则没有。
        /// </summary>
        public Task<string> ChineseDescription => GetChineseDescriptionAsync();


        private async Task<string> GetChineseDescriptionAsync()
        {
            var from = FromCurseForge ? "curseforge" : "modrinth";
            var para = FromCurseForge ? "modId" : "project_id";
            string result = null;

            var descHash = $"{Id}{ModBase.GetStringMD5(Description)}";
            var cacheFilePath = $@"{ModBase.pathTemp}Cache\CompTranslation.ini";
            var cacheTranslation = ModBase.ReadIni(cacheFilePath, descHash);
            if (!string.IsNullOrWhiteSpace(cacheTranslation))
            {
                result = ModBase.Base64Decode(cacheTranslation);
                return result;
            }

            try
            {
                var jsonObject = (JsonObject)await 
                    Requester.FetchJsonAsync($"https://mod.mcimirror.top/translate/{from}/{Id}");
                if (jsonObject.ContainsKey("translated"))
                {
                    result = jsonObject["translated"].ToString();
                    ModBase.WriteIni(cacheFilePath, descHash, ModBase.Base64Encode(result));
                }
            }
            catch (HttpRequestException ex)
            {
                if (ex.Message.Contains("404"))
                {
                    ModMain.MyMsgBox(Lang.Text("Download.Comp.Detail.DescriptionNoTranslation"), Lang.Text("Download.Comp.Detail.DescriptionTranslationFailed"), Lang.Text("Download.Comp.Detail.KnownButton"));
                    return null;
                }

                ModBase.Log(
                    ex,
                    "获取中文描述时出现错误",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Minecraft.Comp.Error.OperationFailed"));
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "获取中文描述时出现错误",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Minecraft.Comp.Error.OperationFailed"));
            }

            return result;
        }


        /// <summary>
        ///     将当前实例转为可用于保存缓存的 Json。
        /// </summary>
        public JsonObject ToJson()
        {
            var json = new JsonObject();
            json["DataSource"] = FromCurseForge ? "CurseForge" : "Modrinth";
            json["Type"] = (int)Type;
            json["Slug"] = Slug;
            json["Id"] = Id;
            if (CurseForgeFileIds is not null)
                json["CurseForgeFileIds"] = new JsonArray(CurseForgeFileIds.Select(i => (JsonNode)i).ToArray());
            json["RawName"] = RawName;
            json["Description"] = Description;
            json["Website"] = Website;
            if (LastUpdate is not null)
                json["LastUpdate"] = LastUpdate;
            json["DownloadCount"] = DownloadCount;
            if (ModLoaders is not null && ModLoaders.Any())
                json["ModLoaders"] = new JsonArray(ModLoaders.Select(m => (JsonNode)(int)m).ToArray());
            json["Tags"] = new JsonArray(Tags.Select(s => (JsonNode)s).ToArray());
            if (LogoUrl is not null)
                json["LogoUrl"] = LogoUrl;
            if (Drops.Any())
                json["Drops"] = new JsonArray(Drops.Select(i => (JsonNode)i).ToArray());
            json["CacheTime"] = DateTime.Now; // 用于检查缓存时间
            return json;
        }


        /// <summary>
        ///     将当前工程信息实例化为控件。
        /// </summary>
        /// <param name="showQuickDownload">是否在卡片右侧显示快速下载按钮（仅搜索结果页应传 true）。</param>
        public MyVirtualizingElement<MyCompItem> ToCompItem(bool showMcVersionDesc, bool showLoaderDesc,
            bool showQuickDownload = false)
        {
            // --- 1. 获取版本描述 (核心算法优化) ---
            string gameVersionDescription;
            if (Drops is null || !Drops.Any())
            {
                gameVersionDescription = Lang.Text("Download.Comp.Detail.CompItem.SnapshotOnly");
            }
            else
            {
                var segments = new List<string>();
                var isOld = false;

                for (var i = 0; i < Drops.Count; i++)
                {
                    int startDrop = Drops[i], endDrop = Drops[i];

                    if (startDrop < 100)
                    {
                        if (segments.Any() && !isOld) break;
                        isOld = true;
                    }

                    // 查找连续的版本段
                    for (var ii = i + 1; ii < Drops.Count; ii++)
                    {
                        if (ModDownload.AllDrops is null || ModDownload.AllDrops.IndexOf(Drops[ii]) !=
                            ModDownload.AllDrops.IndexOf(endDrop) + 1) break;
                        endDrop = Drops[ii];
                        i = ii;
                    }

                    // 将段转为文本的逻辑
                    var startName = McInstanceInfo.DropToVersion(startDrop);
                    var endName = McInstanceInfo.DropToVersion(endDrop);

                    if (startDrop == endDrop)
                    {
                        segments.Add(startName);
                    }
                    else if (ModDownload.AllDrops?.Any() == true && startDrop >= ModDownload.AllDrops.First())
                    {
                        if (endDrop < 100)
                        {
                            segments.Clear();
                            segments.Add(Lang.Text("Download.Comp.Detail.CompItem.AllVersions"));
                            break;
                        }

                        segments.Add(endName + "+");
                    }
                    else if (endDrop < 100)
                    {
                        segments.Add(startName + "-");
                        break;
                    }
                    else if (ModDownload.AllDrops is null ||
                             ModDownload.AllDrops.IndexOf(endDrop) - ModDownload.AllDrops.IndexOf(startDrop) == 1)
                    {
                        segments.Add($"{startName}, {endName}");
                    }
                    else
                    {
                        segments.Add($"{startName}~{endName}");
                    }
                }

                gameVersionDescription = string.Join(", ", segments);
            }

            // --- 2. 获取 Mod 加载器描述 (使用 Switch 表达式) ---
            var modLoadersForDesc = ModLoaders.ToList();
            if (Config.Download.Comp.IgnoreQuilt) modLoadersForDesc.Remove(CompLoaderType.Quilt);

            var (fullDesc, partDesc) = modLoadersForDesc.Count switch
            {
                0 => ModLoaders.Count == 1 ? (Lang.Text("Download.Comp.Type.Only", ModLoaders.Single().ToString()), ModLoaders.Single().ToString()) : (Lang.Text("Download.Comp.Type.Unknown"), ""),
                1 => (Lang.Text("Download.Comp.Type.Only", modLoadersForDesc.Single().ToString()), modLoadersForDesc.Single().ToString()),
                _ => GetMultiLoaderDesc()
            };

            // 局部函数处理复杂的“任意”判断逻辑
            (string, string) GetMultiLoaderDesc()
            {
                var newestDrop = Drops?.FirstOrDefault() ?? 9999;
                var isAny = ModLoaders.Contains(CompLoaderType.Forge) &&
                            (newestDrop < 140 || ModLoaders.Contains(CompLoaderType.Fabric)) &&
                            (newestDrop < 200 || ModLoaders.Contains(CompLoaderType.NeoForge)) &&
                            (newestDrop < 140 || ModLoaders.Contains(CompLoaderType.Quilt) ||
                             Config.Download.Comp.IgnoreQuilt);

                var joined = string.Join(" / ", modLoadersForDesc);
                return isAny ? (Lang.Text("Download.Comp.Type.Any"), "") : (joined, joined);
            }

            // --- 3. 实例化 UI (精简布局逻辑) ---
            return new MyVirtualizingElement<MyCompItem>(() =>
                {
                    var newItem = new MyCompItem { Tag = this };
                    ApplyLogoToMyImage(newItem.PathLogo);

                    var title = GetControlTitle(true);
                    newItem.Title = title.Key;

                    if (string.IsNullOrEmpty(title.Value))
                        ((StackPanel)newItem.LabTitleRaw.Parent).Children.Remove(newItem.LabTitleRaw);
                    else
                        newItem.SubTitle = title.Value;

                    newItem.Tags = Tags;
                    newItem.Description = Description.Replace("\r", "").Replace("\n", "");
                    newItem.ShowDownloadBtn = showQuickDownload;

                    // 下边栏逻辑切换
                    newItem.LabVersion.Text = (showMcVersionDesc, showLoaderDesc) switch
                    {
                        (true, true) =>
                            $"{(string.IsNullOrEmpty(partDesc) ? "" : partDesc + " ")}{gameVersionDescription}",
                        (true, false) => gameVersionDescription,
                        (false, true) => fullDesc,
                        _ => "" // 处理隐藏逻辑见下
                    };

                    if (!showMcVersionDesc && !showLoaderDesc)
                    {
                        ((Grid)newItem.SvgIconVersion.Parent).Children.Remove(newItem.SvgIconVersion);
                        ((Grid)newItem.LabVersion.Parent).Children.Remove(newItem.LabVersion);
                        newItem.ColumnVersion1.Width = new GridLength(0);
                        newItem.ColumnVersion2.MaxWidth = 0;
                        newItem.ColumnVersion3.Width = new GridLength(0);
                    }

                    newItem.LabSource.Text = FromCurseForge ? "CurseForge" : "Modrinth";

                    if (LastUpdate is not null)
                    {
                        newItem.LabTime.Text = Lang.TimeSpan(LastUpdate.Value - DateTime.Now, 1);
                    }
                    else
                    {
                        newItem.LabTime.Visibility = Visibility.Collapsed;
                        newItem.ColumnTime1.Width =
                            newItem.ColumnTime2.Width = newItem.ColumnTime3.Width = new GridLength(0);
                    }

                    // 下载量数值缩写
                    newItem.LabDownload.Text = Lang.CompactNumber(DownloadCount);

                    return newItem;
                })
                { Height = 64 };
        }


        public MyListItem ToListItem()
        {
            var result = new MyListItem
            {
                Title = TranslatedName,
                Info = Description.Replace("\r", "").Replace("\n", ""),
                Logo = string.IsNullOrEmpty(LogoUrl) ? $"{ModBase.pathImage}Icons/NoIcon.png" : LogoUrl,
                Tags = Tags,
                Tag = this,
                LogoCornerRadius = new CornerRadius(6)
            };
            return result;
        }


        public void ApplyLogoToMyImage(MyImage img)
        {
            if (string.IsNullOrEmpty(LogoUrl))
            {
                img.Source = ModBase.pathImage + "Icons/NoIcon.png";
            }
            else
            {
                img.Source = LogoUrl;
                img.FallbackSource = ModDownload.DlSourceModGet(LogoUrl);
            }
        }


        public KeyValuePair<string, string> GetControlTitle(bool hasModLoaderDescription)
        {
            // 参考 #1567 测试例
            var title = RawName;
            List<string> subtitleList = new();

            if (TranslatedName == RawName)
            {
                // --- 场景 A: 没有中文翻译 ---
                var nameLists = TranslatedName.Split(new[] { " | ", " - ", "(", ")", "[", "]", "{", "}" },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim(' ', '/', '\\', '"'))
                    .Where(w => !string.IsNullOrEmpty(w))
                    .ToList();

                if (nameLists.Count <= 1) return BuildResult(title, "");

                var normalNameList = new List<string>();
                foreach (var name in nameLists)
                {
                    var lowerName = name.ToLower();
                    // 匹配缩写 (全大写且不是特定词)
                    if (name.ToUpper() == name && name != "FPS" && name != "HUD")
                        subtitleList.Add(name);
                    // 匹配加载器标记 (Forge/Fabric/Quilt 且去掉后不含其他字母)
                    else if (IsModLoaderMarker(lowerName))
                        subtitleList.Add(name);
                    else
                        normalNameList.Add(name);
                }

                if (!normalNameList.Any() || !subtitleList.Any())
                    return BuildResult(title, "");

                title = string.Join(" - ", normalNameList);
            }
            else
            {
                // --- 场景 B: 有中文翻译 ---
                // 尝试拆分：Title (EnglishName) - Suffix
                title = TranslatedName.BeforeFirst(" (").BeforeFirst(" - ");

                var suffix = "";
                if (TranslatedName.AfterLast(")").Contains(" - "))
                    suffix = TranslatedName.AfterLast(")").AfterLast(" - ");

                var englishName = TranslatedName;
                if (!string.IsNullOrEmpty(suffix))
                    englishName = englishName.Replace(" - " + suffix, "");

                englishName = englishName.Replace(title, "").Trim('(', ')', ' ');

                subtitleList = englishName.Split(new[] { " | ", " - ", "(", ")", "[", "]", "{", "}" },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim(' ', '/'))
                    .Where(w => !string.IsNullOrEmpty(w))
                    .ToList();

                // 特殊逻辑：如果看起来不像版本标记或特定缩写，则保持原名
                if (subtitleList.Count > 1 &&
                    !subtitleList.Any(s => IsModLoaderMarker(s.ToLower())) &&
                    !(subtitleList.Count == 2 && subtitleList.Last().ToUpper() == subtitleList.Last()))
                    subtitleList = new List<string> { englishName };

                if (!string.IsNullOrEmpty(suffix)) subtitleList.Add(suffix);
            }

            // --- 后处理: 构建 Subtitle 字符串 ---
            var finalSubtitles = new List<string>();
            foreach (var rawEx in subtitleList.Distinct())
            {
                var ex = rawEx;
                var lowerEx = ex.ToLower();
                var isModLoader = lowerEx.Contains("forge") || lowerEx.Contains("fabric") || lowerEx.Contains("quilt");

                if (!hasModLoaderDescription && isModLoader) continue;
                if (ex.Length < 16 && lowerEx.Contains("fabric") && lowerEx.Contains("forge")) continue;

                if (isModLoader && !ex.Contains("版") &&
                    lowerEx.Replace("forge", "").Replace("fabric", "").Replace("quilt", "").Length <= 3)
                    ex = ex.Replace("Edition", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("edition", "", StringComparison.OrdinalIgnoreCase)
                        .Trim().Capitalize() + Lang.Text("Download.Comp.Detail.CompItem.EditionSuffix");

                // 规范化名称大小写
                ex = ex.Replace("forge", "Forge").Replace("neo", "Neo").Replace("fabric", "Fabric")
                    .Replace("quilt", "Quilt");
                finalSubtitles.Add(ex.Trim());
            }

            var subtitleResult = finalSubtitles.Any() ? "  |  " + string.Join("  |  ", finalSubtitles) : "";
            return BuildResult(title, subtitleResult);

            bool IsModLoaderMarker(string input)
            {
                return (input.Contains("forge") || input.Contains("fabric") || input.Contains("quilt")) &&
                       !input.Replace("forge", "").Replace("fabric", "").Replace("quilt", "").RegexCheck("[a-z]+");
            }

            KeyValuePair<string, string> BuildResult(string t, string s)
            {
                return new KeyValuePair<string, string>(t, s);
            }
        }

}
}
