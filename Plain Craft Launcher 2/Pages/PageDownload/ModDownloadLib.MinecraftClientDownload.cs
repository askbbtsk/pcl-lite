using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Http;
using PCL.Core.Minecraft;
using PCL.Core.UI;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public static partial class ModDownloadLib
{


    /// <summary>
    ///     下载某个 Minecraft 实例，这会创造一个单独的下载任务，失败会跳过执行并要求反馈。
    ///     返回正在下载的任务，若跳过或失败，则返回 Nothing。
    /// </summary>
    /// <param name="Id">所下载的 Minecraft 的版本名。</param>
    /// <param name="JsonUrl">Json 文件的 Mojang 官方地址。</param>
    /// <param name="targetMcFolder">
    ///     装到哪个 MC 目录，默认是当前选中的那个。Lunar 要指定：
    ///     它的 Genesis 固定往 <c>%APPDATA%\.minecraft</c> 里补原版客户端，
    ///     不能装进玩家自己的 MC 目录。
    /// </param>
    /// <param name="repairOnly">
    ///     只补缺的文件，不把「实例已存在」当成跳过条件。Lunar 用：
    ///     jar 和 json 都在、<c>libraries</c> 缺一半时，Genesis 会闷头把剩下的下完
    ///     （实测 1.3 GB / 970 个文件，界面上一个字都没有），所以这种情况必须
    ///     照样交给 PCL 的下载任务，让进度能看见。
    /// </param>
    public static ModLoader.LoaderCombo<string> McDownloadClient(NetPreDownloadBehaviour behaviour, string id,
        string jsonUrl = null, string targetMcFolder = null, bool repairOnly = false)
    {
        if (!RestrictedFeaturePolicy.Check("minecraft-client-download")) return null;
        try
        {
            var versionFolder = Path.Combine(targetMcFolder ?? ModFolder.mcFolderSelected, "versions", id);

            // 重复任务检查
            foreach (var ongoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if (ongoingLoader.name != Lang.Text("Minecraft.Download.Stage.MinecraftDownload", id))
                    continue;
                if (behaviour == NetPreDownloadBehaviour.ExitWhileExistsOrDownloading)
                    return (ModLoader.LoaderCombo<string>)ongoingLoader;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return (ModLoader.LoaderCombo<string>)ongoingLoader;
            }

            // 已有实例检查
            if (!repairOnly && behaviour != NetPreDownloadBehaviour.IgnoreCheck &&
                File.Exists(Path.Combine(versionFolder, id + ".json")) &&
                File.Exists(Path.Combine(versionFolder, id + ".jar")))
            {
                if (behaviour == NetPreDownloadBehaviour.ExitWhileExistsOrDownloading)
                    return null;
                if (ModMain.MyMsgBox(
                        Lang.Text("Minecraft.Download.Error.InstanceAlreadyExists", id, "\r\n"),
                        Lang.Text("Minecraft.Download.Error.InstanceAlreadyExists.Title"),
                        Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel")
                    ) == 1)
                {
                    File.Delete(Path.Combine(versionFolder, id + ".jar"));
                    File.Delete(Path.Combine(versionFolder, id + ".json"));
                }
                else
                {
                    return null;
                }
            }

            // 启动
            var loader =
                new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Download.Stage.MinecraftDownload", id),
                        McDownloadClientLoader(id, jsonUrl, targetMcFolder: targetMcFolder))
                    { OnStateChanged = McInstallState };
            loader.Start(versionFolder);
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
            return loader;
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始 Minecraft 下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
            return null;
        }
    }


    /// <summary>
    ///     保存某个 Minecraft 实例的核心文件（仅 Json 与核心 Jar）。
    /// </summary>
    /// <param name="id">所下载的 Minecraft 的版本名。</param>
    /// <param name="jsonUrl">Json 文件的 Mojang 官方地址。</param>
    public static void McDownloadClientCore(string id, string jsonUrl, NetPreDownloadBehaviour behaviour)
    {
        if (!RestrictedFeaturePolicy.Check("minecraft-client-save")) return;
        try
        {
            var versionFolder = SystemDialogs.SelectFolder();
            if (!versionFolder.Contains(@"\"))
                return;
            versionFolder = Path.Combine(versionFolder, id);

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar)
            {
                if ((OngoingLoader.name ?? "") != (Lang.Text("Minecraft.Download.Stage.MinecraftDownload", id) ?? ""))
                    continue;
                if (behaviour == NetPreDownloadBehaviour.ExitWhileExistsOrDownloading)
                    return;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            var loaders = new List<ModLoader.LoaderBase>();
            // 下载实例 Json 文件
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadInstanceJson"),
                new List<DownloadFile>
                {
                    new(ModDownload.DlSourceLauncherOrMetaGet(jsonUrl), Path.Combine(versionFolder, id + ".json"),
                        new ModBase.FileChecker(canUseExistsFile: false, isJson: true))
                }) { ProgressWeight = 2d });
            // 获取支持库文件地址
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                    Lang.Text("Minecraft.Download.Stage.AnalyzeCoreJarUrl"),
                    task => task.output =
                        ModLibrary.McLibNetFilesFromInstance(new McInstance(versionFolder)))
                { ProgressWeight = 0.5d, show = false });
            // 下载支持库文件
            loaders.Add(
                new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadCoreJar"), new List<DownloadFile>())
                    { ProgressWeight = 5d });

            // 启动
            var loader =
                new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Download.Stage.MinecraftDownload", id), loaders)
                { OnStateChanged = LoaderStateChangedHintOnly };
            loader.Start(id);
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始 Minecraft 下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     获取下载某个 Minecraft 版本的加载器列表。
    ///     它必须安装到 McFolderSelected，但是可以自定义版本名（不过自定义的实例名不会修改 Json 中的 id 项）。
    /// </summary>
    private static List<ModLoader.LoaderBase> McDownloadClientLoader(string id, string jsonUrl = null,
        string instanceName = null, string targetMcFolder = null)
    {
        instanceName = instanceName ?? id;
        var instanceFolder = Path.Combine(targetMcFolder ?? ModFolder.mcFolderSelected, "versions", instanceName);

        var loaders = new List<ModLoader.LoaderBase>();

        // 下载实例 Json 文件
        if (jsonUrl is null)
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                Lang.Text("Minecraft.Download.Stage.ObtainVanillaJsonUrl"), task =>
            {
                var jsonAddress = ModDownload.DlClientListGet(id)?.ToString();
                task.output = new List<DownloadFile>
                {
                    new(ModDownload.DlSourceLauncherOrMetaGet(jsonAddress), Path.Combine(instanceFolder, instanceName + ".json"))
                };
            })
            {
                ProgressWeight = 2d,
                show = false
            });
        loaders.Add(new LoaderDownload(mcDownloadClientJsonName,
            new List<DownloadFile>
            {
                new(ModDownload.DlSourceLauncherOrMetaGet(jsonUrl ?? ""), Path.Combine(instanceFolder, instanceName + ".json"),
                    new ModBase.FileChecker(canUseExistsFile: false, isJson: true))
            }) { ProgressWeight = 3d });

        // 下载支持库文件
        var loadersLib = new List<ModLoader.LoaderBase>();
        loadersLib.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.AnalyzeVanillaLibraries.Side"), task =>
        {
            var jsonPath = Path.Combine(instanceFolder, instanceName + ".json");
            ModBase.WaitForFileReady(jsonPath);
            ModBase.Log("[Download] 开始分析原版支持库文件：" + instanceFolder);
            if (id == "1.16.5" && Config.Download.FixAuthLib) // 1.16.5 Authlib 修复
                try
                {
                    var json = ModBase.ReadFile(jsonPath);
                    json = json.Replace("2.1.28/authlib-2.1.28.jar", "2.3.31/authlib-2.3.31.jar")
                        .Replace("com.mojang:authlib:2.1.28", "com.mojang:authlib:2.3.31")
                        .Replace("ad54da276bf59983d02d5ed16fc14541354c71fd", "bbd00ca33b052f73a6312254780fc580d2da3535")
                        .Replace("76328", "87662");
                    ModBase.WriteFile(jsonPath, json);
                }
                catch (Exception ex)
                {
                    ModBase.Log("[Download] 替换 Authlib 版本失败: " + ex.Message);
                }

            task.output = ModLibrary.McLibNetFilesFromInstance(new McInstance(instanceFolder));
        })
        {
            ProgressWeight = 1d,
            show = false
        });
        loadersLib.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadVanillaLibraries.Side"),
                new List<DownloadFile>())
            { ProgressWeight = 13d, show = false });
        loaders.Add(new ModLoader.LoaderCombo<string>(mcDownloadClientLibName, loadersLib)
            { block = false, ProgressWeight = 14d });

        // 下载资源文件
        var loadersAssets = new List<ModLoader.LoaderBase>();
        loadersAssets.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.AnalyzeAssetsIndex.Side"), task =>
        {
            ModBase.WaitForFileReady(Path.Combine(instanceFolder, instanceName + ".json"));
            try
            {
                var assetIndex = new McInstance(instanceFolder);
                task.output = new List<DownloadFile> { ModDownload.DlClientAssetIndexGet(assetIndex) };
            }
            catch (Exception ex)
            {
                throw new Exception(Lang.Text("Minecraft.Download.Error.AssetIndexAnalysisFailed"), ex);
            }

            // 顺手添加 Json 项目
            try
            {
                var versionJson = (JsonObject)ModBase.GetJson(ModBase.ReadFile(Path.Combine(instanceFolder, instanceName + ".json")));
                versionJson.Add("clientVersion", id);
                ModBase.WriteFile(Path.Combine(instanceFolder, instanceName + ".json"), versionJson.ToString());
            }
            catch (Exception ex)
            {
                throw new Exception(Lang.Text("Minecraft.Download.Error.AddClientVersionFailed"), ex);
            }
        })
        {
            ProgressWeight = 1d,
            show = false
        });
        loadersAssets.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadAssetsIndex.Side"),
                new List<DownloadFile>())
            { ProgressWeight = 3d, show = false });
        loadersAssets.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.AnalyzeRequiredAssets.Side"), task =>
        {
            ModLoader.LoaderBase argprogressFeed = task;
            task.output =
                ModAssets.McAssetsFixList(new McInstance(instanceFolder), true, ref argprogressFeed);
            task = (ModLoader.LoaderTask<string, List<DownloadFile>>)argprogressFeed;
        })
        {
            ProgressWeight = 0.01d,
            show = false
        });
        loadersAssets.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadAssets.Side"),
                new List<DownloadFile>())
            { ProgressWeight = 14d, show = false });
        loaders.Add(
            new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Download.Stage.DownloadVanillaAssets"),
                loadersAssets) { block = false, ProgressWeight = 18d });

        return loaders;
    }

}
