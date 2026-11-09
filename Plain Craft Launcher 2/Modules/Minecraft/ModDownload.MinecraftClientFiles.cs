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

    /// <summary>
    ///     返回某 Minecraft 版本对应的原版主 Jar 文件的下载信息，要求对应依赖实例已存在。
    ///     失败则抛出异常，不需要下载则返回 Nothing。
    /// </summary>
    public static DownloadFile DlClientJarGet(McInstance version, bool returnNothingOnFileUseable)
    {
        // 获取底层继承实例
        try
        {
            while (!string.IsNullOrEmpty(version.InheritInstanceName))
                version = new McInstance(version.InheritInstanceName);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "获取底层继承实例失败");
        }

        // 检查 Json 是否标准
        if (version.JsonObject["downloads"] is null || version.JsonObject["downloads"]["client"] is null ||
            version.JsonObject["downloads"]["client"]["url"] is null)
            throw new Exception(Lang.Text("Minecraft.Download.Error.NoJarDownloadInfo", version.Name));
        // 检查文件
        var checker = new ModBase.FileChecker(1024L, (long)(version.JsonObject["downloads"]["client"]["size"] ?? -1),
            (string)version.JsonObject["downloads"]["client"]["sha1"]);
        if (returnNothingOnFileUseable && checker.Check(version.PathInstance + version.Name + ".jar") is null)
            return null; // 通过校验
        // 返回下载信息
        var jarUrl = (string)version.JsonObject["downloads"]["client"]["url"];
        return new DownloadFile(DlSourceLauncherOrMetaGet(jarUrl), version.PathInstance + version.Name + ".jar",
            checker);
    }


    /// <summary>
    ///     返回某 Minecraft 版本对应的原版主 AssetIndex 文件的下载信息，要求对应依赖实例已存在。
    ///     若未找到，则会返回 Legacy 资源文件或 Nothing。
    /// </summary>
    public static DownloadFile DlClientAssetIndexGet(McInstance version)
    {
        // 获取底层继承实例
        while (!string.IsNullOrEmpty(version.InheritInstanceName))
            version = new McInstance(version.InheritInstanceName);
        // 获取信息
        var indexInfo = ModAssets.McAssetsGetIndex(version, true, true);
        var indexAddress = Path.Combine(ModFolder.mcFolderSelected, "assets", "indexes", indexInfo["id"] + ".json");
        ModBase.Log("[Download] 实例 " + version.Name + " 对应的资源文件索引为 " + indexInfo["id"]);
        var indexUrl = (string)(indexInfo["url"] ?? "");
        if (string.IsNullOrEmpty(indexUrl)) return null;

        // 避免下载器先发一次 Range 探测请求，再回退到顺序下载
        var indexSize = indexInfo["size"] is null ? -1L : (long)indexInfo["size"];
        return new DownloadFile(DlSourceLauncherOrMetaGet(indexUrl), indexAddress,
            new ModBase.FileChecker(actualSize: indexSize, canUseExistsFile: false));
    }


    /// <summary>
    ///     构造补全某 Minecraft 版本的所有文件的加载器列表。失败会抛出异常。
    /// </summary>
    public static List<ModLoader.LoaderBase> DlClientFix(McInstance version, bool checkAssetsHash,
        AssetsIndexExistsBehaviour assetsIndexBehaviour)
    {
        var loaders = new List<ModLoader.LoaderBase>();


        if (ModLibrary.ShouldIgnoreFileCheck(version))
        {
            ModBase.Log("[Download] 已跳过所有 Libraries 检查");
        }
        else
        {
            var loadersLib = new List<ModLoader.LoaderBase>
            {
                new ModLoader.LoaderTask<string, List<DownloadFile>>(
                    Lang.Text("Minecraft.Download.Stage.AnalyzeMissingLibraries"),
                    task => task.output = ModLibrary.McLibNetFilesFromInstance(version)) { ProgressWeight = 1d },
                new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLibraries"), new List<DownloadFile>())
                    { ProgressWeight = 15d }
            };
            // 构造加载器
            loaders.Add(
                new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Download.Stage.DownloadLibraries.MainLoader"),
                        loadersLib)
                { block = false, show = false, ProgressWeight = 16d });
        }



        if (ModLibrary.ShouldIgnoreFileCheck(version))
        {
            ModBase.Log("[Download] 已跳过所有 Assets 检查");
        }
        else
        {
            var loadersAssets = new List<ModLoader.LoaderBase>();
            // 获取资源文件索引地址
            loadersAssets.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                Lang.Text("Minecraft.Download.Stage.AnalyzeAssetsIndex"), task =>
            {
                try
                {
                    var indexFile = DlClientAssetIndexGet(version);
                    var indexFileInfo = new FileInfo(indexFile.LocalPath);
                    if (assetsIndexBehaviour != AssetsIndexExistsBehaviour.AlwaysDownload &&
                        indexFile.Check.Check(indexFile.LocalPath) is null)
                        task.output = new List<DownloadFile>();
                    else
                        task.output = new List<DownloadFile> { indexFile };
                }
                catch (Exception ex)
                {
                    throw new Exception(Lang.Text("Minecraft.Download.Error.AssetIndexAnalysisFailed"), ex);
                }
            }) { ProgressWeight = 0.5d, show = false });
            // 下载资源文件索引
            loadersAssets.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadAssetsIndex"),
                    new List<DownloadFile>())
                { ProgressWeight = 2d });
            // 要求独立更新索引
            if (assetsIndexBehaviour == AssetsIndexExistsBehaviour.DownloadInBackground)
            {
                var loadersAssetsUpdate = new List<ModLoader.LoaderBase>();
                string tempAddress = null;
                string realAddress = null;
                loadersAssetsUpdate.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                    Lang.Text("Minecraft.Download.Stage.AnalyzeAssetsIndex.Background"), task =>
                {
                    var backAssetsFile = DlClientAssetIndexGet(version);
                    realAddress = backAssetsFile.LocalPath;
                    tempAddress = ModBase.pathTemp + @"Cache\" + backAssetsFile.LocalName;
                    backAssetsFile.LocalPath = tempAddress;
                    task.output = new List<DownloadFile> { backAssetsFile };
                    // 检查是否需要更新：每天只更新一次
                    if (File.Exists(realAddress) &&
                        Math.Abs((File.GetLastWriteTime(realAddress).Date - DateTime.Now.Date).TotalDays) < 1d)
                    {
                        ModBase.Log("[Download] 无需更新资源文件索引，取消");
                        task.Abort();
                    }
                }));
                loadersAssetsUpdate.Add(new LoaderDownload(
                    Lang.Text("Minecraft.Download.Stage.DownloadAssetsIndex.Background"), new List<DownloadFile>()));
                loadersAssetsUpdate.Add(new ModLoader.LoaderTask<List<DownloadFile>, string>(
                    Lang.Text("Minecraft.Download.Stage.CopyAssetsIndex.Background"), task =>
                {
                    ModBase.CopyFile(tempAddress, realAddress);
                    ModLaunch.McLaunchLog("后台更新资源文件索引成功：" + tempAddress);
                }));
                var updater = new ModLoader.LoaderCombo<string>(
                    Lang.Text("Minecraft.Download.Stage.UpdateAssetsIndex.Background"), loadersAssetsUpdate);
                ModBase.Log("[Download] 开始后台检查资源文件索引");
                updater.Start();
            }

            // 获取资源文件地址
            loadersAssets.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                Lang.Text("Minecraft.Download.Stage.AnalyzeMissingAssets"), task =>
            {
                ModLoader.LoaderBase argprogressFeed = task;
                task.output = ModAssets.McAssetsFixList(version, checkAssetsHash, ref argprogressFeed);
                task = (ModLoader.LoaderTask<string, List<DownloadFile>>)argprogressFeed;
            })
            {
                ProgressWeight = 3d
            });
            // 下载资源文件
            loadersAssets.Add(
                new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadAssets"), new List<DownloadFile>())
                    { ProgressWeight = 25d });
            // 构造加载器
            loaders.Add(
                new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Download.Stage.DownloadAssets.MainLoader"),
                        loadersAssets)
                { block = false, show = false, ProgressWeight = 30.5d });
        }


        return loaders;
    }


    /// <summary>
    ///     只读取本地文件，判断实例是否可以在不下载 Minecraft 文件的情况下启动。
    /// </summary>
    public static bool McClientFilesAvailableLocally(McInstance instance)
    {
        try
        {
            if (!instance.IsLoaded) instance.Load();
            if (!File.Exists(Path.Combine(instance.PathInstance, instance.Name + ".json"))) return false;
            var libraries = ModLibrary.McLibListGet(instance, true);
            foreach (var library in libraries)
            {
                if (library.IsLocal) continue;
                var checker = new ModBase.FileChecker(
                    minSize: 1,
                    actualSize: library.size > 0 ? library.size : -1,
                    hash: library.Sha1);
                if (checker.Check(library.LocalPath) is not null) return false;
            }

            var assets = ModAssets.McAssetsListGet(instance);
            return assets.All(asset => File.Exists(asset.localPath) && new FileInfo(asset.localPath).Length > 0 &&
                                       (asset.size <= 0 || new FileInfo(asset.localPath).Length == asset.size));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "本地检查 Minecraft 文件失败");
            return false;
        }
    }

}
