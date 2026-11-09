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



    public static void McDownloadLabyModProductionLoaderSave()
    {
        try
        {
            var url = "https://releases.labymod.net/api/v1/installer/production/java";
            var fileName = "LabyMod4ProductionInstaller.jar";
            var target = SystemDialogs.SelectSaveFile(Lang.Text("Download.Version.SelectSaveLocation"), fileName, Lang.Text("Download.Version.Installer.LabyMod.Filter"));
            if (!target.Contains(@"\"))
                return;

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") !=
                    (Lang.Text("Minecraft.Download.Stage.LabyModInstallerDownload") ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            // 构造步骤加载器
            var loaders = new List<ModLoader.LoaderBase>();
            // 下载
            var address = new List<string>();
            address.Add(url);
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadMainFile"),
                    new List<DownloadFile> { new(address.ToArray(), target, new ModBase.FileChecker(1024 * 64)) })
                { ProgressWeight = 15d });
            // 启动
            var loader =
                new ModLoader.LoaderCombo<JsonObject>(Lang.Text("Minecraft.Download.Stage.LabyModInstallerDownload"),
                        loaders)
                { OnStateChanged = LoaderStateChangedHintOnly };
            loader.Start();
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始 LabyMod 安装器下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    public static void McDownloadLabyModSnapshotLoaderSave()
    {
        try
        {
            var url = "https://releases.labymod.net/api/v1/installer/snapshot/java";
            var fileName = "LabyMod4SnapshotInstaller.jar";
            var target = SystemDialogs.SelectSaveFile(Lang.Text("Download.Version.SelectSaveLocation"), fileName, Lang.Text("Download.Version.Installer.LabyMod.Filter"));
            if (!target.Contains(@"\"))
                return;

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") !=
                    (Lang.Text("Minecraft.Download.Stage.LabyModInstallerDownload") ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            // 构造步骤加载器
            var loaders = new List<ModLoader.LoaderBase>();
            // 下载
            var address = new List<string>();
            address.Add(url);
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadMainFile"),
                    new List<DownloadFile> { new(address.ToArray(), target, new ModBase.FileChecker(1024 * 64)) })
                { ProgressWeight = 15d });
            // 启动
            var loader =
                new ModLoader.LoaderCombo<JsonObject>(Lang.Text("Minecraft.Download.Stage.LabyModInstallerDownload"),
                        loaders)
                { OnStateChanged = LoaderStateChangedHintOnly };
            loader.Start();
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始 LabyMod 安装器下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     获取下载某个 LabyMod 实例的加载器列表。
    /// </summary>
    private static List<ModLoader.LoaderBase> McDownloadLabyModLoader(string labyModCommitRef, string labyModChannel,
        string minecraftName, string mcFolder = null, bool fixLibrary = true)
    {
        // 参数初始化
        mcFolder = mcFolder ?? ModFolder.mcFolderSelected;
        var isCustomFolder = (mcFolder ?? "") != (ModFolder.mcFolderSelected ?? "");
        var id = "labymod-" + labyModCommitRef + "-" + minecraftName;
        var versionFolder = Path.Combine(mcFolder, "versions", id);
        var loaders = new List<ModLoader.LoaderBase>();

        // 下载 Json
        minecraftName = minecraftName.Replace("∞", "infinite"); // 放在 ID 后面避免影响实例文件夹名称
        loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.ObtainLabyModClientUrl"), task =>
        {
            // 启动依赖实例的下载
            if (fixLibrary)
                McDownloadClient(NetPreDownloadBehaviour.ExitWhileExistsOrDownloading, minecraftName,
                    $"https://releases.r2.labymod.net/api/v1/download/manifest/labymod4/{labyModChannel}/{minecraftName}/{labyModCommitRef}.json");
            task.Progress = 0.5d;
            // 构造文件请求
            task.output = new List<DownloadFile>
            {
                new(
                    new[]
                    {
                        $"https://releases.r2.labymod.net/api/v1/download/manifest/labymod4/{labyModChannel}/{minecraftName}/{labyModCommitRef}.json"
                    }, Path.Combine(versionFolder, id + ".json"), new ModBase.FileChecker(isJson: true))
            };
            task.Progress = 1d;
        })
        {
            ProgressWeight = 2d
        });
        loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLoaderLibraries", "Fabric"),
                new List<DownloadFile>())
            { ProgressWeight = 10d });
        // 下载支持库
        if (fixLibrary)
        {
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                    Lang.Text("Minecraft.Download.Stage.AnalyzeLabyModLibraries"),
                    task => task.output =
                        ModLibrary.McLibNetFilesFromInstance(new McInstance(versionFolder)))
                { ProgressWeight = 1d, show = false });
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLoaderLibraries", "LabyMod"),
                    new List<DownloadFile>())
                { ProgressWeight = 8d });
        }

        return loaders;
    }


    /// <summary>
    ///     获取下载某个 Minecraft 实例的加载器列表。
    ///     它必须安装到 PathMcFolder，但是可以自定义实例名（不过自定义的实例名不会修改 Json 中的 id 项）。
    /// </summary>
    private static List<ModLoader.LoaderBase> McDownloadLabyModClientLoader(string id, string labyChannel,
        string labyCommitRef, string versionName = null)
    {
        versionName = versionName ?? id;
        var versionFolder = Path.Combine(ModFolder.mcFolderSelected, "versions", versionName) + @"\";

        var loaders = new List<ModLoader.LoaderBase>();

        // 下载支持库文件
        var loadersLib = new List<ModLoader.LoaderBase>();
        loadersLib.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.AnalyzeVanillaAndLabyModLibrariesSide"), task =>
        {
            ModBase.WaitForFileReady(Path.Combine(versionFolder, versionName + ".json"));
            ModBase.Log("[Download] 开始分析原版与 LabyMod 支持库文件：" + versionFolder);
            task.output = ModLibrary.McLibNetFilesFromInstance(new McInstance(versionFolder));
        })
        {
            ProgressWeight = 1d,
            show = false
        });
        loadersLib.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadVanillaAndLabyModLibrariesSide"),
                new List<DownloadFile>())
            { ProgressWeight = 13d, show = false });
        loaders.Add(new ModLoader.LoaderCombo<string>(mcDownloadClientLibName, loadersLib)
            { block = false, ProgressWeight = 14d });

        // 下载资源文件
        var loadersAssets = new List<ModLoader.LoaderBase>();
        loadersAssets.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.AnalyzeAssetsIndex.Side"), task =>
        {
            try
            {
                var version = new McInstance(versionFolder);
                task.output = new List<DownloadFile> { ModDownload.DlClientAssetIndexGet(version) };
            }
            catch (Exception ex)
            {
                throw new Exception(Lang.Text("Minecraft.Download.Error.AssetIndexAnalysisFailed"), ex);
            }

            // 顺手添加 Json 项目
            try
            {
                var versionJson = (JsonObject)ModBase.GetJson(ModBase.ReadFile(Path.Combine(versionFolder, versionName + ".json")));
                versionJson.Add("clientVersion", id);
                ModBase.WriteFile(Path.Combine(versionFolder, versionName + ".json"), versionJson.ToString());
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
                ModAssets.McAssetsFixList(new McInstance(versionFolder), true, ref argprogressFeed);
            task = (ModLoader.LoaderTask<string, List<DownloadFile>>)argprogressFeed;
        })
        {
            ProgressWeight = 3d,
            show = false
        });
        loadersAssets.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadAssets.Side"),
                new List<DownloadFile>())
            { ProgressWeight = 14d, show = false });
        loaders.Add(
            new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Download.Stage.DownloadVanillaAssets"),
                loadersAssets) { block = false, ProgressWeight = 21d });

        return loaders;
    }




    public static MyListItem LabyModDownloadListItem(JsonObject entry, MyListItem.ClickEventHandler onClick)
    {
        // 建立控件
        var newItem = new MyListItem
        {
            Title = entry["version"] + " " + (entry["channel"].ToString().Contains("snapshot") ? Lang.Text("Download.Version.Type.Snapshot") : Lang.Text("Download.Version.Type.Stable")),
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = entry,
            Info = entry["channel"].ToString().Contains("snapshot") ? Lang.Text("Download.Version.Type.Snapshot") : Lang.Text("Download.Version.Type.Stable"),
            Logo = ModBase.pathImage + "Blocks/LabyMod.png"
        };
        newItem.Click += onClick;
        newItem.ContentHandler = LabyModContMenuBuild;
        // 结束
        return newItem;
    }


    private static void LabyModContMenuBuild(object sender, EventArgs e)
    {
        var btnSave = new MyIconButton { SvgIcon = "lucide/save", ToolTip = Lang.Text("Download.Version.SaveAs") };
        ToolTipService.SetPlacement(btnSave, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnSave, 30d);
        ToolTipService.SetHorizontalOffset(btnSave, 2d);
        btnSave.Click += (a, b) => LabyModSave_Click(a, (dynamic)b);
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (a, b) => LabyModLog_Click(a, (dynamic)b);
        ((dynamic)sender).Buttons = new[] { btnSave, btnInfo };
    }


    private static void LabyModLog_Click(object sender, RoutedEventArgs e)
    {
        ModBase.OpenWebsite("https://www.labymod.net/zh_Hans/download");
    }


    private static void LabyModSave_Click(object sender, RoutedEventArgs e)
    {
        JsonObject version;
        if (((dynamic)sender).Tag is not null)
            version = (JsonObject)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            version = (JsonObject)((dynamic)sender).Parent.Tag;
        else
            version = (JsonObject)((dynamic)sender).Parent.Parent.Tag;
        if ((string)version["channel"] == "snapshot")
            McDownloadLabyModSnapshotLoaderSave();
        else
            McDownloadLabyModProductionLoaderSave();
    }

}
