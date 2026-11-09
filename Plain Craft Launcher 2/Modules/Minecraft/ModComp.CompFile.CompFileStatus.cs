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



    // 类定义

    public enum CompFileStatus
    {
        Release = 1, // 枚举值来源：https://docs.curseforge.com/#tocS_FileReleaseType
        Beta = 2,
        Alpha = 3
    }


    public class CompFile
    {
        /// <summary>
        ///     该文件的所有必要依赖工程的 Project.Id。
        /// </summary>
        public readonly List<string> Dependencies = new();

        /// <summary>
        ///     下载量计数。注意，该计数仅为一个来源，无法反应两边加起来的下载量，且 CurseForge 可能错误地返回 0。
        /// </summary>
        public readonly int DownloadCount;

        /// <summary>
        ///     下载的文件名。
        /// </summary>
        public readonly string FileName;

        /// <summary>
        ///     该文件来自 CurseForge 还是 Modrinth。
        /// </summary>
        public readonly bool FromCurseForge;

        //  <summary>
        //  未经处理的支持的游戏版本列表。
        // </summary>
        public readonly List<string> RawGameVersions = new();
        /// <summary>
        ///     支持的游戏版本列表。类型包括："26.1.5"，"26.1"，"26.1 预览版"，"1.18.5"，"1.18"，"1.18 预览版"，"21w15a"，"未知版本"。
        /// </summary>
        public readonly List<string> GameVersions = new();

        /// <summary>
        ///     文件的 SHA1 或 MD5。
        /// </summary>
        public readonly string Hash;

        /// <summary>
        ///     用于唯一性鉴别该文件的 ID。CurseForge 中为 123456 的大整数，Modrinth 中为英文乱码的 Version 字段。
        /// </summary>
        public readonly string Id;

        /// <summary>
        ///     支持的 Mod 加载器列表。可能为空。
        /// </summary>
        public readonly List<CompLoaderType> ModLoaders = new();

        /// <summary>
        ///     该文件的所有可选依赖工程的 Project.Id。
        /// </summary>
        public readonly List<string> OptionalDependencies = new();

        /// <summary>
        ///     该文件所属项目的 ID。
        /// </summary>
        public readonly string ProjectId;

        /// <summary>
        ///     该文件的所有必要依赖工程的原始 ID。
        ///     这些 ID 可能没有加载，在加载后会添加到 Dependencies 中（主要是因为 Modrinth 返回的是字符串 ID 而非 Slug，导致 Project.Id 查询不到）。
        /// </summary>
        public readonly List<string> RawDependencies = new();

        /// <summary>
        ///     该文件的所有可选依赖工程的原始 ID。
        ///     这些 ID 可能没有加载，在加载后会添加到 OptionalDependencies 中（主要是因为 Modrinth 返回的是字符串 ID 而非 Slug，导致 Project.Id 查询不到）。
        /// </summary>
        public readonly List<string> RawOptionalDependencies = new();

        /// <summary>
        ///     发布时间。
        /// </summary>
        public readonly DateTime ReleaseDate;

        /// <summary>
        ///     发布状态：Release/Beta/Alpha。
        /// </summary>
        public readonly CompFileStatus Status;

        // 源信息

        /// <summary>
        ///     文件的种类。
        /// </summary>
        public readonly CompType Type;

        // 描述性信息

        /// <summary>
        ///     文件描述名（并非文件名，是自定义的字段）。对很多 Mod，这会给出 Mod 版本号。
        /// </summary>
        public string DisplayName;

        /// <summary>
        ///     文件所有可能的下载源。
        /// </summary>
        public List<string> DownloadUrls;

        /// <summary>
        ///     Mod 版本号。
        ///     不一定是标准格式。CurseForge 上默认为 Nothing。
        /// </summary>
        public string Version;

        // 实例化

        /// <summary>
        ///     从文件 Json 中初始化实例。若出错会抛出异常。
        /// </summary>
        public CompFile(JsonObject data, CompType defaultType)
        {
            Type = defaultType;
            if (data.ContainsKey("FromCurseForge"))
            {

                FromCurseForge = data["FromCurseForge"].ToObject<bool>();
                Id = data["Id"].ToString();
                DisplayName = data["DisplayName"].ToString();
                if (data.ContainsKey("Version"))
                    Version = data["Version"].ToString();
                ReleaseDate = data["ReleaseDate"].ToObject<DateTime>();
                DownloadCount = data["DownloadCount"].ToObject<int>();
                Status = (CompFileStatus)data["Status"].ToObject<int>();
                if (data.ContainsKey("FileName"))
                    FileName = data["FileName"].ToString();
                if (data.ContainsKey("DownloadUrls"))
                    DownloadUrls = data["DownloadUrls"].ToObject<List<string>>();
                if (data.ContainsKey("ModLoaders"))
                    ModLoaders = data["ModLoaders"].ToObject<List<CompLoaderType>>();
                if (data.ContainsKey("Hash"))
                    Hash = data["Hash"].ToString();
                if (data.ContainsKey("RawGameVersions"))
                    RawGameVersions = data["RawGameVersions"].ToObject<List<string>>();
                if (data.ContainsKey("GameVersions"))
                    GameVersions = data["GameVersions"].ToObject<List<string>>();
                if (data.ContainsKey("RawDependencies"))
                    RawDependencies = data["RawDependencies"].ToObject<List<string>>();
                if (data.ContainsKey("Dependencies"))
                    Dependencies = data["Dependencies"].ToObject<List<string>>();
                if (data.ContainsKey("RawOptionalDependencies"))
                    RawOptionalDependencies = data["RawOptionalDependencies"].ToObject<List<string>>();
                if (data.ContainsKey("OptionalDependencies"))
                    OptionalDependencies = data["OptionalDependencies"].ToObject<List<string>>();
            }


            else
            {
                FromCurseForge = data.ContainsKey("gameId");
                if (FromCurseForge)
                {

                    // 简单信息
                    Id = data["id"].ToString();
                    ProjectId = data["modId"].ToString();
                    DisplayName = data["displayName"].ToString().Replace("	", "").Trim(' ');
                    Version = null;
                    ReleaseDate = data["fileDate"].ToObject<DateTime>();
                    Status = (CompFileStatus)data["releaseType"].ToObject<int>();
                    DownloadCount = (int)data["downloadCount"];
                    FileName = (string)data["fileName"];
                    Hash =
                        (string)((JsonArray)data["hashes"]).ToList().FirstOrDefault(s => s["algo"].ToObject<int>() == 1)?[
                            "value"];
                    if (Hash is null)
                        Hash = (string)((JsonArray)data["hashes"]).ToList()
                            .FirstOrDefault(s => s["algo"].ToObject<int>() == 2)?["value"];
                    // DownloadAddress
                    var url = data["downloadUrl"]?.ToString() ?? "";
                    // TODO: 移除龙猫写的直接下载，换用提醒用户手动下载相关模组
                    if (string.IsNullOrWhiteSpace(url))
                        url =
                            $"https://edge.forgecdn.net/files/{int.Parse(Id[..4])}/{int.Parse(Id[4..])}/{FileName}";
                    url = url.Replace(FileName, WebUtility.UrlEncode(FileName)); // 对文件名进行编码
                    url = url.Replace("+", "%20"); // 修正被编码成 + 的空格，CurseForge 会对 + 号也进行编码
                    DownloadUrls = ModDownload.DlSourceModDownloadGet(HandleCurseForgeDownloadUrls(url)); // 添加镜像源
                    // Dependencies
                    if (data.ContainsKey("dependencies"))
                    {
                        RawDependencies = data["dependencies"].AsArray()
                            .Where(d => d["relationType"].ToObject<int>() == 3 &&
                                        d["modId"].ToObject<int>() != 306612 && d["modId"].ToObject<int>() != 634179)
                            .Select(d => d["modId"].ToString()).ToList(); // 种类为必要依赖
                        // 排除 Fabric API 和 Quilt API
                        RawOptionalDependencies = data["dependencies"].AsArray()
                            .Where(d => d["relationType"].ToObject<int>() == 2 &&
                                        d["modId"].ToObject<int>() != 306612 && d["modId"].ToObject<int>() != 634179)
                            .Select(d => d["modId"].ToString()).ToList(); // 种类为可选依赖
                        // 排除 Fabric API 和 Quilt API
                    }

                    // GameVersions
                    RawGameVersions = data["gameVersions"].AsArray().Select(t => t.ToString().Trim().ToLower()).ToList();
                    GameVersions = RawGameVersions.Where(v => McInstanceInfo.IsFormatFit(v))
                        .Select(v => v.Replace("-snapshot", Lang.Text("Download.Comp.Detail.CompItem.PreviewSuffix"))).Distinct().ToList();
                    if (GameVersions.Count > 1)
                    {
                        GameVersions = GameVersions.Sort(McVersionComparer.CompareVersionGe).ToList();
                        if (Type == CompType.ModPack)
                            GameVersions = new List<string> { GameVersions[0] }; // 整合包理应只 "支持" 一个版本
                    }
                    else if (GameVersions.Count == 1)
                    {
                        GameVersions = GameVersions.ToList();
                    }
                    else
                    {
                        GameVersions = new List<string> { Lang.Text("Download.Comp.Detail.CompItem.UnknownVersion") };
                    }

                    // ModLoaders
                    ModLoaders = new List<CompLoaderType>();
                    if (RawGameVersions.Contains("forge"))
                        ModLoaders.Add(CompLoaderType.Forge);
                    if (RawGameVersions.Contains("fabric"))
                        ModLoaders.Add(CompLoaderType.Fabric);
                    if (RawGameVersions.Contains("quilt"))
                        ModLoaders.Add(CompLoaderType.Quilt);
                    if (RawGameVersions.Contains("neoforge"))
                        ModLoaders.Add(CompLoaderType.NeoForge);
                }


                else
                {

                    // 简单信息
                    Id = (string)data["id"];
                    ProjectId = (string)data["project_id"];
                    DisplayName = data["name"].ToString().Replace("	", "").Trim(' ');
                    Version = (string)data["version_number"];
                    ReleaseDate = data["date_published"].ToObject<DateTime>();
                    Status = data["version_type"].ToString() == "release" ? CompFileStatus.Release :
                        data["version_type"].ToString() == "beta" ? CompFileStatus.Beta : CompFileStatus.Alpha;
                    DownloadCount = (int)data["downloads"];
                    if (((JsonArray)data["files"]).Any()) // 可能为空
                    {
                        var file = data["files"][0];
                        FileName = (string)file["filename"];
                        DownloadUrls = ModDownload.DlSourceModDownloadGet(file["url"].ToString()); // 同时添加了镜像源
                        Hash = (string)file["hashes"]["sha1"];
                    }

                    // ModLoaders
                    // 结果可能混杂着 Mod、数据包和服务端插件
                    var rawLoaders = data["loaders"].AsArray().Select(v => v.ToString()).ToList();
                    ModLoaders = new List<CompLoaderType>();
                    if (Type == CompType.Mod) // 以尽量宽容的方式检测加载器，以免同时兼容两种的项被删除
                    {
                        if (rawLoaders.Intersect(new[] { "bukkit", "folia", "paper", "purpur", "spigot" }).Any())
                            Type = CompType.Plugin; // Veinminer Enchantment 同时支持服务端与 Fabric
                        if (rawLoaders.Contains("datapack"))
                            Type = CompType.DataPack;
                        if (rawLoaders.Contains("forge"))
                        {
                            ModLoaders.Add(CompLoaderType.Forge);
                            Type = CompType.Mod;
                        }

                        if (rawLoaders.Contains("neoforge"))
                        {
                            ModLoaders.Add(CompLoaderType.NeoForge);
                            Type = CompType.Mod;
                        }

                        if (rawLoaders.Contains("fabric"))
                        {
                            ModLoaders.Add(CompLoaderType.Fabric);
                            Type = CompType.Mod;
                        }

                        if (rawLoaders.Contains("quilt"))
                        {
                            ModLoaders.Add(CompLoaderType.Quilt);
                            Type = CompType.Mod;
                        }
                    }
                    else if (Type == CompType.DataPack)
                    {
                        if (rawLoaders.Intersect(new[] { "bukkit", "folia", "paper", "purpur", "spigot" }).Any())
                            Type = CompType.Plugin;
                        if (rawLoaders.Contains("forge"))
                        {
                            ModLoaders.Add(CompLoaderType.Forge);
                            Type = CompType.Mod;
                        }

                        if (rawLoaders.Contains("neoforge"))
                        {
                            ModLoaders.Add(CompLoaderType.NeoForge);
                            Type = CompType.Mod;
                        }

                        if (rawLoaders.Contains("fabric"))
                        {
                            ModLoaders.Add(CompLoaderType.Fabric);
                            Type = CompType.Mod;
                        }

                        if (rawLoaders.Contains("quilt"))
                        {
                            ModLoaders.Add(CompLoaderType.Quilt);
                            Type = CompType.Mod;
                        }

                        if (rawLoaders.Contains("datapack"))
                            Type = CompType.DataPack;
                    }

                    // Dependencies
                    if (data.ContainsKey("dependencies"))
                    {
                        RawDependencies = data["dependencies"].AsArray()
                            .Where(d => (string)d["dependency_type"] == "required" &&
                                        d["project_id"] is not null &&
                                        (string)d["project_id"] != "P7dR8mSH" &&
                                        (string)d["project_id"] != "qvIfYCYJ" && d["project_id"] is not null)
                            .Select(d => d["project_id"].ToString()).ToList(); // 种类为必要依赖
                        // 排除 Fabric API 和 Quilt API
                        // 有时候真的会空……
                        RawOptionalDependencies = data["dependencies"].AsArray()
                            .Where(d => (string)d["dependency_type"] == "optional" &&
                                        d["project_id"] is not null &&
                                        (string)d["project_id"] != "P7dR8mSH" &&
                                        (string)d["project_id"] != "qvIfYCYJ" && d["project_id"] is not null)
                            .Select(d => d["project_id"].ToString()).ToList(); // 种类为可选依赖
                        // 排除 Fabric API 和 Quilt API
                        // 有时候真的会空……
                    }

                    // GameVersions
                    RawGameVersions = data["game_versions"].AsArray().Select(t => t.ToString().Trim().ToLower()).ToList();
                    GameVersions = RawGameVersions.Where(v => v.Contains(".")).Select(v =>
                        v.Contains("-") ? v.BeforeFirst("-") + Lang.Text("Download.Comp.Detail.CompItem.PreviewSuffix") : v.StartsWithF("b1.") ? Lang.Text("Download.Comp.Detail.CompItem.AncientVersion") : v).Distinct().ToList();
                    if (GameVersions.Count > 1)
                    {
                        GameVersions = GameVersions.Sort(McVersionComparer.CompareVersionGe).ToList();
                        if (Type == CompType.ModPack)
                            GameVersions = new List<string> { GameVersions[0] }; // 整合包理应只 “支持” 一个版本
                    }
                    else if (GameVersions.Count == 1)
                    {
                    }
                    // 无需处理
                    else if (RawGameVersions.Any(v => v.RegexCheck("[0-9]{2}w[0-9]{2}[a-z]")))
                    {
                        GameVersions = RawGameVersions.Where(v => v.RegexCheck("[0-9]{2}w[0-9]{2}[a-z]")).ToList();
                    }
                    else
                    {
                        GameVersions = new List<string> { Lang.Text("Download.Comp.Detail.CompItem.UnknownVersion") };
                    }

                }
            }
        }

        /// <summary>
        ///     发布状态的友好描述。例如："正式版"，"Beta 版"。
        /// </summary>
        public string StatusDescription
        {
            get
            {
                switch (Status)
                {
                    case CompFileStatus.Release:
                    {
                        return Lang.Text("Download.Comp.Detail.FileList.ReleaseType.Release");
                    }
                    case CompFileStatus.Beta:
                    {
                        return Lang.Text("Download.Comp.Detail.FileList.ReleaseType.Beta");
                    }

                    default:
                    {
                        return Lang.Text("Download.Comp.Detail.FileList.ReleaseType.Alpha");
                    }
                }
            }
        }

        // 下载信息
        /// <summary>
        ///     下载信息是否可用。
        /// </summary>
        public bool Available => FileName is not null && DownloadUrls is not null;

        /// <summary>
        /// 获取下载信息。
        /// </summary>
        /// <param name="localAddress">目标本地文件夹，或完整的文件路径。会自动判断类型。</param>
        /// <param name="reason">下载原因。</param>
        /// <returns>下载信息。</returns>
        public DownloadFile ToNetFile(string localAddress, DownloadReason reason = DownloadReason.Standalone)
        {
            return ToNetFile(localAddress, reason, RawGameVersions.FirstOrDefault(), ModLoaders.FirstOrDefault());
        }
        
        /// <summary>
        /// 获取下载信息。
        /// </summary>
        /// <param name="localAddress">目标本地文件夹，或完整的文件路径。会自动判断类型。</param>
        /// <param name="reason">下载原因。</param>
        /// <param name="version">实例版本。</param>
        /// <param name="modLoader">实例的模组加载器。</param>
        /// <returns>下载信息。</returns>
        public DownloadFile ToNetFile(
            string localAddress,
            DownloadReason reason,
            string? version,
            CompLoaderType modLoader = CompLoaderType.Any)
        {
            if (DownloadUrls is null)
            {
                throw new InvalidCastException("DownloadUrls 为空");
            }
            
            return new DownloadFile(HandleModrinthDownloadUrls(DownloadUrls, reason, version, modLoader),
                localAddress + (localAddress.EndsWithF(@"\") ? CompFileNameSanitize(FileName) : ""),
                new ModBase.FileChecker(hash: Hash), true);
        }

        /// <summary>
        ///     对之前错误的 CurseForge 的下载地址进行修正。
        /// </summary>
        public static string HandleCurseForgeDownloadUrls(string url)
        {
            return url.Replace("-service.overwolf.wtf", ".forgecdn.net").Replace("://media.", "://edge.")
                .Replace("://mediafilez.", "://edge.");
        }

        /// <summary>
        /// 对 Modrinth 下载链接添加上报参数。
        /// </summary>
        /// <param name="urls">下载链接。</param>
        /// <param name="reason">下载原因。</param>
        /// <param name="version">实例版本。</param>
        /// <param name="modLoader">实例的模组加载器。</param>
        /// <returns>处理后的下载链接。</returns>
        public static IEnumerable<string> HandleModrinthDownloadUrls(
            IEnumerable<string> urls,
            DownloadReason reason = DownloadReason.Standalone,
            string? version = null,
            CompLoaderType modLoader = CompLoaderType.Any)
        {
            foreach (var url in urls)
            {
                if (!url.Contains("modrinth", StringComparison.InvariantCultureIgnoreCase))
                {
                    yield return url;
                    continue;
                }

                var sb = new StringBuilder(url);
            
                sb.Append(url.Contains('?') ? "&mr_download_reason=" : "?mr_download_reason=");
                sb.Append(reason.ToString().ToLowerInvariant());

                if (version is not null)
                {
                    sb.Append("&mr_game_version=");
                    sb.Append(version);
                }

                if (modLoader != CompLoaderType.Any)
                {
                    sb.Append("&mr_loader=");
                    sb.Append(modLoader.ToString().ToLowerInvariant());
                }

                yield return sb.ToString();
            }
        }

        /// <summary>
        ///     将当前实例转为可用于保存缓存的 Json。
        /// </summary>
        public JsonObject ToJson()
        {
            var json = new JsonObject();
            json.Add("FromCurseForge", FromCurseForge);
            json.Add("Id", Id);
            if (Version is not null)
                json.Add("Version", Version);
            json.Add("DisplayName", DisplayName);
            json.Add("ReleaseDate", ReleaseDate);
            json.Add("DownloadCount", DownloadCount);
            json.Add("ModLoaders", new JsonArray(ModLoaders.Select(m => (JsonNode)(int)m).ToArray()));
            json.Add("RawGameVersions", new JsonArray(RawGameVersions.Select(s => (JsonNode)s).ToArray()));
            json.Add("GameVersions", new JsonArray(GameVersions.Select(s => (JsonNode)s).ToArray()));
            json.Add("Status", (int)Status);
            if (FileName is not null)
                json.Add("FileName", FileName);
            if (DownloadUrls is not null)
                json.Add("DownloadUrls", new JsonArray(DownloadUrls.Select(s => (JsonNode)s).ToArray()));
            if (Hash is not null)
                json.Add("Hash", Hash);
            json.Add("RawDependencies", new JsonArray(RawDependencies.Select(s => (JsonNode)s).ToArray()));
            json.Add("RawOptionalDependencies", new JsonArray(RawOptionalDependencies.Select(s => (JsonNode)s).ToArray()));
            json.Add("Dependencies", new JsonArray(Dependencies.Select(s => (JsonNode)s).ToArray()));
            json.Add("OptionalDependencies", new JsonArray(OptionalDependencies.Select(s => (JsonNode)s).ToArray()));
            return json;
        }

        /// <summary>
        ///     将当前文件信息实例化为控件。
        /// </summary>
        public MyVirtualizingElement<MyListItem> ToListItem(MyListItem.ClickEventHandler onClick,
            MyIconButton.ClickEventHandler? onSaveClick = null,
            bool badDisplayName = false)
        {
            return new MyVirtualizingElement<MyListItem>(() =>
                {
                    // 1. 获取基础描述信息
                    var title = badDisplayName ? FileName : DisplayName;
                    var info = new List<string>();

                    // 2. 填充信息列表
                    if (title != FileName.BeforeLast("."))
                        info.Add(FileName.BeforeLast("."));

                    if (Dependencies.Any())
                        info.Add(Lang.Text("Download.Comp.Detail.FileList.DependencyCount", Dependencies.Count()));

                    // 简化后的游戏版本逻辑喵
                    var snapshotKeywords = new[] { "w", "snapshot", "rc", "pre", "experimental", "-" };
                    if (GameVersions.All(ver =>
                            !ver.Contains('.') || snapshotKeywords.Any(s => ver.ContainsF(s, true))))
                        info.Add(Lang.Text("Download.Comp.Detail.FileList.GameVersion", string.Join("、", GameVersions)));

                    if (DownloadCount > 0)
                        info.Add(Lang.Text("Common.Format.DownloadCount", Lang.CompactNumber(DownloadCount)));

                    info.Add(Lang.Text("Download.Comp.Detail.FileList.Updated", Lang.TimeSpan(ReleaseDate - DateTime.Now)));

                    if (Status != CompFileStatus.Release)
                        info.Add(StatusDescription);

                    // 3. 建立控件
                    var newItem = new MyListItem
                    {
                        Title = title,
                        SnapsToDevicePixels = true,
                        Height = 42,
                        Type = MyListItem.CheckType.Clickable,
                        Tag = this,
                        Info = string.Join("  |  ", info),
                        // 使用 switch 表达式精简 Logo 选择喵！
                        Logo = Status switch
                        {
                            CompFileStatus.Release => ModBase.pathImage + "Icons/R.png",
                            CompFileStatus.Beta => ModBase.pathImage + "Icons/B.png",
                            _ => ModBase.pathImage + "Icons/A.png"
                        }
                    };
                    newItem.Click += onClick;

                    // 4. 建立另存为按钮
                    if (onSaveClick is not null)
                    {
                        var btnSave = new MyIconButton { SvgIcon = "lucide/save", ToolTip = Lang.Text("Download.Version.SaveAs") };
                        ToolTipService.SetPlacement(btnSave, PlacementMode.Center);
                        ToolTipService.SetVerticalOffset(btnSave, 30);
                        ToolTipService.SetHorizontalOffset(btnSave, 2);
                        btnSave.Click += onSaveClick;
                        newItem.Buttons = new[] { btnSave };
                    }

                    return newItem;
                })
                { Height = 42 };
        }

        public override string ToString()
        {
            return $"{Id}: {FileName}";
        }
    }

}
