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



    public static void McDownloadLiteLoader(ModDownload.DlLiteLoaderListEntry downloadInfo)
    {
        try
        {
            var id = downloadInfo.Inherit;
            var target = Path.Combine(ModBase.pathTemp, "Download", id + "-Liteloader.jar");
            var versionName = downloadInfo.Inherit + "-LiteLoader";
            var versionFolder = Path.Combine(ModFolder.mcFolderSelected, "versions", versionName);

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") != (Lang.Text("Minecraft.Download.Stage.LiteLoaderDownload", id) ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            // 已有实例检查
            if (File.Exists(Path.Combine(versionFolder, versionName + ".json")))
            {
                if (ModMain.MyMsgBox(
                        Lang.Text("Minecraft.Download.Error.InstanceAlreadyExists", versionName, "\r\n"),
                        Lang.Text("Minecraft.Download.Error.InstanceAlreadyExists.Title"),
                        Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel")
                    ) == 1)
                {
                    File.Delete(Path.Combine(versionFolder, versionName + ".jar"));
                    File.Delete(Path.Combine(versionFolder, versionName + ".json"));
                }
                else
                {
                    return;
                }
            }

            // 启动
            var loader =
                new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Download.Stage.LiteLoaderDownload", id),
                        McDownloadLiteLoaderLoader(downloadInfo))
                    { OnStateChanged = McInstallState };
            loader.Start(versionFolder);
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始 LiteLoader 下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    private static void McDownloadLiteLoaderSave(ModDownload.DlLiteLoaderListEntry downloadInfo)
    {
        try
        {
            if (!downloadInfo.IsLegacy)
            {
                HintService.Hint(Lang.Text("Download.Version.LiteLoader.InstallerUnavailable"), HintType.Error);
                return;
            }

            var id = downloadInfo.Inherit;
            var target = SystemDialogs.SelectSaveFile(Lang.Text("Download.Version.SelectSaveLocation"), downloadInfo.FileName.Replace("-SNAPSHOT", ""),
                Lang.Text("Minecraft.Download.Stage.ForgelikeInstallerFilter", "LiteLoader", "jar"));
            if (!target.Contains(@"\"))
                return;

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") != (Lang.Text("Minecraft.Download.Stage.LiteLoaderDownload", id) ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            // 构造步骤加载器
            var loaders = new List<ModLoader.LoaderBase>();
            // 下载
            var address = new List<string>();
            // 老版本安装器仍由 dl.liteloader.com 提供；新版 Jenkins 安装器已失联，
            // 不再保留其下载地址。
            switch (downloadInfo.Inherit ?? "")
            {
                case "1.7.10":
                    address.Add("https://dl.liteloader.com/redist/1.7.10/liteloader-installer-1.7.10-04.jar");
                    break;
                case "1.7.2":
                    address.Add("https://dl.liteloader.com/redist/1.7.2/liteloader-installer-1.7.2-04.jar");
                    break;
                case "1.6.4":
                    address.Add("https://dl.liteloader.com/redist/1.6.4/liteloader-installer-1.6.4-01.jar");
                    break;
                case "1.6.2":
                    address.Add("https://dl.liteloader.com/redist/1.6.2/liteloader-installer-1.6.2-04.jar");
                    break;
                case "1.5.2":
                    address.Add("https://dl.liteloader.com/redist/1.5.2/liteloader-installer-1.5.2-01.jar");
                    break;
                default:
                    throw new NotSupportedException(Lang.Text("Minecraft.Download.Error.UnknownMinecraftVersion",
                        downloadInfo.Inherit));
                }

            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadMainFile"),
                    new List<DownloadFile> { new(address.ToArray(), target, new ModBase.FileChecker(1024 * 1024)) })
                { ProgressWeight = 15d });
            // 启动
            var loader =
                new ModLoader.LoaderCombo<ModDownload.DlLiteLoaderListEntry>(
                        Lang.Text("Minecraft.Download.Stage.LiteLoaderInstallerDownload", id), loaders)
                    { OnStateChanged = LoaderStateChangedHintOnly };
            loader.Start(downloadInfo);
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始 LiteLoader 安装器下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     获取下载某个 LiteLoader 实例的加载器列表。
    /// </summary>
    private static List<ModLoader.LoaderBase> McDownloadLiteLoaderLoader(ModDownload.DlLiteLoaderListEntry downloadInfo,
        string mcFolder = null, ModLoader.LoaderCombo<string> clientDownloadLoader = null, bool fixLibrary = true)
    {
        // 参数初始化
        mcFolder = mcFolder ?? ModFolder.mcFolderSelected;
        var isCustomFolder = (mcFolder ?? "") != (ModFolder.mcFolderSelected ?? "");
        var id = downloadInfo.Inherit;
        var target = Path.Combine(ModBase.pathTemp, "Download", id + "-Liteloader.jar");
        var versionName = downloadInfo.Inherit + "-LiteLoader";
        var versionFolder = Path.Combine(mcFolder, "versions", versionName);
        var loaders = new List<ModLoader.LoaderBase>();

        // 启动依赖实例的下载
        if (clientDownloadLoader is null)
            loaders.Add(new ModLoader.LoaderTask<string, string>(
                Lang.Text("Minecraft.Download.Stage.StartLiteLoaderDependencyDownload"), _ =>
            {
                if (isCustomFolder)
                    throw new Exception(Lang.Text("Minecraft.Download.Error.NoVanillaLoaderSpecified"));
                clientDownloadLoader = McDownloadClient(NetPreDownloadBehaviour.ExitWhileExistsOrDownloading,
                    downloadInfo.Inherit);
            })
            {
                ProgressWeight = 0.2d,
                show = false,
                block = false
            });
        // 安装
        // 新建实例文件夹
        // 构造实例 Json
        // 输出 Json 文件
        loaders.Add(new ModLoader.LoaderTask<string, string>(Lang.Text("Minecraft.Download.Stage.InstallLiteLoader"),
            _ =>
        {
            try
            {
                Directory.CreateDirectory(versionFolder);
                var versionJson = new JsonObject();
                versionJson.Add("id", versionName);
                var releaseDate = DateTime.Parse(downloadInfo.ReleaseTime, Lang.Culture);
                versionJson.Add("time", releaseDate);
                versionJson.Add("releaseTime", releaseDate);
                versionJson.Add("type", "release");
                versionJson.Add("arguments",
                    (JsonNode)ModBase.GetJson("{\"game\":[\"--tweakClass\",\"" + downloadInfo.jsonToken["tweakClass"] +
                                            "\"]}"));
                versionJson.Add("libraries", downloadInfo.jsonToken["libraries"]?.DeepClone());
                versionJson["libraries"].AsArray().Add(ModBase.GetJson("{\"name\": \"com.mumfrey:liteloader:" +
                                                                            downloadInfo.jsonToken["version"] +
                                                                            "\",\"url\": \"https://dl.liteloader.com/versions/\"}"));
                versionJson.Add("mainClass", "net.minecraft.launchwrapper.Launch");
                versionJson.Add("minimumLauncherVersion", 18);
                versionJson.Add("inheritsFrom", downloadInfo.Inherit);
                versionJson.Add("jar", downloadInfo.Inherit);
                ModBase.WriteFile(Path.Combine(versionFolder, versionName + ".json"), versionJson.ToString());
            }
            catch (Exception ex)
            {
                throw new Exception(Lang.Text("Minecraft.Download.Error.LiteLoaderInstallFailed"), ex);
            }
        }) { ProgressWeight = 1d });
        // 下载支持库
        if (fixLibrary)
        {
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                    Lang.Text("Minecraft.Download.Stage.AnalyzeLiteLoaderLibraries"),
                    task => task.output =
                        ModLibrary.McLibNetFilesFromInstance(new McInstance(versionFolder)))
                { ProgressWeight = 1d, show = false });
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLiteLoaderLibraries"),
                    new List<DownloadFile>())
                { ProgressWeight = 6d });
        }

        return loaders;
    }




    public static MyListItem LiteLoaderDownloadListItem(ModDownload.DlLiteLoaderListEntry entry,
        MyListItem.ClickEventHandler onClick, bool isSaveOnly)
    {
        var infoParts = new List<string>
        {
            entry.IsPreview
                ? Lang.Text("Download.Version.Type.Preview")
                : Lang.Text("Download.Version.Type.Stable")
        };

        if (!string.IsNullOrEmpty(entry.ReleaseTime))
            infoParts.Add(Lang.Text("Download.Version.ReleaseDate", entry.ReleaseTime));

        // 建立控件
        var newItem = new MyListItem
        {
            Title = entry.Inherit,
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = entry,
            Info = string.Join("  |  ", infoParts),
            Logo = ModBase.pathImage + "Blocks/Egg.png"
        };

        newItem.Click += onClick;
        // 建立菜单
        newItem.ContentHandler = isSaveOnly
            ? LiteLoaderSaveContMenuBuild
            : LiteLoaderContMenuBuild;
        // 结束
        return newItem;
    }


    private static void LiteLoaderSaveContMenuBuild(MyListItem sender, EventArgs e)
    {
        // 非远古版本的独立安装器和版本列表仅托管在已失联的 Jenkins 上。
        sender.Buttons = Array.Empty<MyIconButton>();
    }


    private static void LiteLoaderContMenuBuild(MyListItem sender, EventArgs e)
    {
        if ((bool)((dynamic)sender.Tag).IsLegacy)
        {
            var btnSave = new MyIconButton
            {
                SvgIcon = "lucide/save", ToolTip = Lang.Text("Download.Version.SaveInstaller"), Tag = sender
            };
            ToolTipService.SetPlacement(btnSave, PlacementMode.Center);
            ToolTipService.SetVerticalOffset(btnSave, 30d);
            ToolTipService.SetHorizontalOffset(btnSave, 2d);
            btnSave.Click += (sender, e) => LiteLoaderSave_Click(sender, (RoutedEventArgs)e);
            sender.Buttons = [btnSave];
        }
        else
        {
            // 启动器内安装仍会从可用的 Maven 源下载库文件。
            sender.Buttons = Array.Empty<MyIconButton>();
        }
    }


    public static void LiteLoaderSave_Click(object sender, RoutedEventArgs e)
    {
        // ListItem 与小按钮都会调用这个方法
        ModDownload.DlLiteLoaderListEntry version;
        if (((dynamic)sender).Tag is ModDownload.DlLiteLoaderListEntry)
            version = (ModDownload.DlLiteLoaderListEntry)((dynamic)sender).Tag;
        else
            version = (ModDownload.DlLiteLoaderListEntry)((dynamic)sender).Tag.Tag;
        if (!version.IsLegacy)
        {
            HintService.Hint(Lang.Text("Download.Version.LiteLoader.InstallerUnavailable"), HintType.Error);
            return;
        }
        McDownloadLiteLoaderSave(version);
    }

}
