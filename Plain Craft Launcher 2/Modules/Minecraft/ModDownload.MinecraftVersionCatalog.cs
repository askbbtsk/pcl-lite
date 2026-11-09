using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public static partial class ModDownload
{

    public enum AssetsIndexExistsBehaviour
    {
        /// <summary>
        ///     如果文件存在，则不进行下载。
        /// </summary>
        DontDownload,

        /// <summary>
        ///     如果文件存在，则启动新的下载加载器进行独立的更新。
        /// </summary>
        DownloadInBackground,

        /// <summary>
        ///     如果文件存在，也同样进行下载。
        /// </summary>
        AlwaysDownload
    }




    /// <summary>
    ///     所有正式版的 Minecraft Drop 序数。
    ///     若从未完成过获取，返回 Nothing；否则必定存在元素，且从高到低排列。
    /// </summary>
    public static List<int> AllDrops
    {
        get
        {
            lock (_allDropsLock)
            {
                if (field is null)
                {
                    var rawData = States.Game.Drops;
                    if (string.IsNullOrEmpty(rawData))
                        field = new List<int>();
                    else
                        field = rawData.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(d => (int)Math.Round(ModBase.Val(d))).ToList();
                }

                return field.Count != 0 ? field : null;
            }
        }
        set
        {
            lock (_allDropsLock)
            {
                field = value;
                States.Game.Drops = value.Join(",");
            }
        }
    }


    // 主加载器
    public struct DlClientListResult
    {
        /// <summary>
        ///     数据来源名称，如“Mojang”，“BMCLAPI”。
        /// </summary>
        public string SourceName;

        /// <summary>
        ///     是否为官方的实时数据。
        /// </summary>
        public bool IsOfficial;

        /// <summary>
        ///     获取到的 Json 数据。
        /// </summary>
        public JsonObject Value;
        // ''' <summary>
        // ''' 官方源的失败原因。若没有则为 Nothing。
        // ''' </summary>
        // Public OfficialError As Exception
    }


    private static void DlClientListMain(ModLoader.LoaderTask<string, DlClientListResult> loader)
    {
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<string, DlClientListResult>, int>>
                        { new(dlClientListBmclapiLoader, 30), new(dlClientListMojangLoader, 30 + 60) },
                    loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<string, DlClientListResult>, int>>
                        { new(dlClientListMojangLoader, 5), new(dlClientListBmclapiLoader, 5 + 30) },
                    loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<string, DlClientListResult>, int>>
                        { new(dlClientListMojangLoader, 60), new(dlClientListBmclapiLoader, 60 + 60) },
                    loader.isForceRestarting);
                break;
            }
        }

        // 提取所有 Drop 序数
        var drops = new List<int>();
        foreach (JsonObject version in loader.output.Value["versions"].AsArray())
            drops.Add(McInstanceInfo.VersionToDrop((string)version["id"]));
        AllDrops = drops.Distinct().OrderByDescending(d => d).ToList();
    }


    private static void DlClientListMojangMain(ModLoader.LoaderTask<string, DlClientListResult> loader)
    {
        var startTime = TimeUtils.GetTimeTick();
        var json = (JsonObject)Requester.FetchJson("https://launchermeta.mojang.com/mc/game/version_manifest.json");
        try
        {
            var versions = (JsonArray)json["versions"];
            if (versions.Count < 200)
                throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Mojang", json));
            // 添加 UVMC 项
            var cacheFilePath = ModBase.pathTemp + @"Cache\uvmc-download.json";
            if (!File.Exists(cacheFilePath))
                try
                {
                    var unlistedJson = (JsonObject)Requester.FetchJson(
                        "https://alist.8mi.tech/d/mirror/unlisted-versions-of-minecraft/Auto/version_manifest.json");
                    File.WriteAllText(cacheFilePath, unlistedJson.ToString());
                }
                catch (Exception ex)
                {
                    ModBase.Log("[Download] 未列出的版本官方源下载失败: " + ex.Message);
                }

            try
            {
                var cachedJson = (JsonObject)ModBase.GetJson(ModBase.ReadFile(cacheFilePath));
                versions.Merge(cachedJson["versions"]);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Download] UVMC 列表加载失败，忽略列表内容");
            }

            // 确定官方源是否可用
            if (!DownloadSource.OfficialSourceIsFast)
            {
                var deltaTime = TimeUtils.GetTimeTick() - startTime;
                DownloadSource.OfficialSourceIsFast = deltaTime < 4000;
                ModBase.Log($"[Download] Mojang 官方源加载耗时：{deltaTime}ms，{(DownloadSource.OfficialSourceIsFast ? "可优先使用官方源" : "不优先使用官方源")}");
            }

            // 添加 PCL 特供项
            // 这个社区版下不了
            // If File.Exists(PathTemp & "Cache\download.json") Then Versions.Merge(GetJson(ReadFile(PathTemp & "Cache\download.json")))
            // 返回
            loader.output = new DlClientListResult
                { IsOfficial = true, SourceName = Lang.Text("Download.Source.MojangOfficial"), Value = json };
            string version;
            // 快照版
            version = (string)json["latest"]["snapshot"];
            if (Config.Tool.SnapshotNotification &&
                                      States.Tool.LastSnapshot != "" &&
                                      States.Tool.LastSnapshot != version &&
                                      !_DlClientListMojangMain_IsHinted)
            {
                _DlClientListMojangMain_IsHinted = true;
                McDownloadClientUpdateHint(version, json);
            }

            States.Tool.LastSnapshot = version ?? "Nothing";
            // 正式版
            version = (string)json["latest"]["release"];
            if (Config.Tool.ReleaseNotification &&
                                      States.Tool.LastRelease != "" &&
                                      States.Tool.LastRelease != version &&
                                      !_DlClientListMojangMain_IsHinted)
            {
                _DlClientListMojangMain_IsHinted = true;
                McDownloadClientUpdateHint(version, json);
            }

            States.Tool.LastRelease = version;
        }
        catch (Exception ex)
        {
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Mojang", ""), ex);
        }
    }


    private static void DlClientListBmclapiMain(ModLoader.LoaderTask<string, DlClientListResult> loader)
    {
        var json = (JsonObject)Requester.FetchJson(
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json");
        try
        {
            var versions = (JsonArray)json["versions"];
            if (versions.Count < 200)
                throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "BMCLAPI", json));
            // 添加 UVMC 项
            var cacheFilePath = ModBase.pathTemp + @"Cache\uvmc-download.json";
            if (!File.Exists(cacheFilePath))
                try
                {
                    var unlistedJson = (JsonObject)Requester.FetchJson(
                        "https://alist.8mi.tech/d/mirror/unlisted-versions-of-minecraft/Auto/version_manifest.json");
                    File.WriteAllText(cacheFilePath, unlistedJson.ToString());
                }
                catch (Exception ex)
                {
                    ModBase.Log("[Download] 未列出的版本镜像源下载失败: " + ex.Message);
                }

            try
            {
                var cachedJson = (JsonObject)ModBase.GetJson(ModBase.ReadFile(cacheFilePath));
                versions.Merge(cachedJson["versions"]);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Download] UVMC 列表加载失败，忽略列表内容");
            }

            // 检查是否有要求的版本（#5195）
            if (!string.IsNullOrEmpty(loader.input))
            {
                var id = loader.input;
                if (dlClientListLoader.output.Value is not null &&
                    !dlClientListLoader.output.Value["versions"].AsArray().Any(v => (string)v["id"] == id))
                    throw new Exception(Lang.Text("Minecraft.Download.Error.BmclapiMissingTargetVersion", id));
            }

            // 返回
            loader.output = new DlClientListResult { IsOfficial = false, SourceName = "BMCLAPI", Value = json };
        }
        catch (Exception ex)
        {
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "BMCLAPI", json), ex);
        }
    }


    /// <summary>
    ///     获取某个版本的 Json 下载地址，若失败则返回 Nothing。必须在工作线程执行。
    /// </summary>
    public static object DlClientListGet(string id)
    {
        try
        {
            // 确认版本格式标准
            id = id.Replace("_", "-"); // 1.7.10_pre4 在版本列表中显示为 1.7.10-pre4
            if (id != "1.0" && id.EndsWithF(".0"))
                id = id.Substring(0, id.Length - 2); // OptiFine 1.8 的下载会触发此问题，显示版本为 1.8.0
            // 获取 Minecraft 版本列表
            switch (dlClientListLoader.State)
            {
                case ModBase.LoadState.Finished:
                {
                    // 从当前的结果获取目标版本…
                    foreach (JsonObject Version in dlClientListLoader.output.Value["versions"].AsArray())
                        if ((string)Version["id"] == id)
                            return Version["url"].ToString();
                    // …如果没有，则重新尝试获取（在版本刚更新时可能出现这种情况，#5195）
                    dlClientListLoader.WaitForExit(id, isForceRestart: true);
                    break;
                }
                case ModBase.LoadState.Loading:
                {
                    dlClientListLoader.WaitForExit(id);
                    break;
                }
                case ModBase.LoadState.Failed:
                case ModBase.LoadState.Aborted:
                case ModBase.LoadState.Waiting:
                {
                    dlClientListLoader.WaitForExit(id, isForceRestart: true);
                    break;
                }
            }

            // 重新查找版本
            foreach (JsonObject Version in dlClientListLoader.output.Value["versions"].AsArray())
                if ((string)Version["id"] == id)
                    return Version["url"].ToString();
            ModBase.Log($"未发现版本 {id} 的 json 下载地址，版本列表返回为：{"\r\n"}{dlClientListLoader.output.Value}",
                ModBase.LogLevel.Debug);
            return null;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"获取版本 {id} 的 json 下载地址失败");
            return null;
        }
    }

}
