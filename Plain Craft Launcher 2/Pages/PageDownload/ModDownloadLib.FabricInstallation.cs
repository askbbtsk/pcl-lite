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



    public static void McDownloadFabricLoaderSave(JsonObject downloadInfo)
    {
        try
        {
            var url = downloadInfo["url"].ToString();
            var fileName = ModBase.GetFileNameFromPath(url);
            var version = ModBase.GetFileNameFromPath(downloadInfo["version"].ToString());
            var target = SystemDialogs.SelectSaveFile(Lang.Text("Download.Version.SelectSaveLocation"), fileName, Lang.Text("Download.Version.Installer.Fabric.Filter"));
            if (!target.Contains(@"\"))
                return;

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") !=
                    (Lang.Text("Minecraft.Download.Stage.FabricInstallerDownload", version) ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            // 构造步骤加载器
            var loaders = new List<ModLoader.LoaderBase>();
            // 下载
            // BMCLAPI 不支持 Fabric Installer 下载
            var address = new List<string>();
            address.Add(url);
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadMainFile"),
                    new List<DownloadFile> { new(address.ToArray(), target, new ModBase.FileChecker(1024 * 64)) })
                { ProgressWeight = 15d });
            // 启动
            var loader =
                new ModLoader.LoaderCombo<JsonObject>(
                        Lang.Text("Minecraft.Download.Stage.FabricInstallerDownload", version), loaders)
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
                "开始 Fabric 安装器下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     获取下载某个 Fabric 实例的加载器列表。
    /// </summary>
    private static List<ModLoader.LoaderBase> McDownloadFabricLoader(string fabricVersion, string minecraftName,
        string mcFolder = null, bool fixLibrary = true)
    {
        // 参数初始化
        mcFolder = mcFolder ?? ModFolder.mcFolderSelected;
        var isCustomFolder = (mcFolder ?? "") != (ModFolder.mcFolderSelected ?? "");
        var id = "fabric-loader-" + fabricVersion + "-" + minecraftName;
        var versionFolder = Path.Combine(mcFolder, "versions", id);
        var loaders = new List<ModLoader.LoaderBase>();

        // 下载 Json
        minecraftName = minecraftName.Replace("∞", "infinite"); // 放在 ID 后面避免影响实例文件夹名称
        loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.ObtainFabricMainFileUrl"), task =>
        {
            // 启动依赖实例的下载
            if (fixLibrary)
                McDownloadClient(NetPreDownloadBehaviour.ExitWhileExistsOrDownloading, minecraftName);
            task.Progress = 0.5d;
            
            var safeName = minecraftName.Replace("∞", "infinite");
            var bmclapiUrl = $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{safeName}/{fabricVersion}/profile/json";
            var officialUrl = $"https://meta.fabricmc.net/v2/versions/loader/{safeName}/{fabricVersion}/profile/json";

            string json = null;
            foreach (var url in new[] { bmclapiUrl, officialUrl })
            {
                try
                {
                    json = Requester.FetchString(url, new RequestParam { UseBrowserUserAgent = true, Timeout = 5000, Retries = 2 });
                    if (json is not null) break;
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, $"[Download] 从 {url} 下载 Fabric meta 失败");
                }
            }

            if (json is null)
                throw new Exception(Lang.Text("Minecraft.Download.Error.FabricMetaDownloadFailed",
                    $"{bmclapiUrl} and {officialUrl}"));

            Directory.CreateDirectory(versionFolder);
            File.WriteAllText(Path.Combine(versionFolder, id + ".json"), json, Encoding.UTF8);
            task.output = new List<DownloadFile>();
        })
        {
            ProgressWeight = 0.5d
        });
        loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLoaderMainFile", "Fabric"),
            new List<DownloadFile>()) { ProgressWeight = 2.5d });

        // 下载支持库
        if (fixLibrary)
        {
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                    Lang.Text("Minecraft.Download.Stage.AnalyzeFabricLibraries"),
                    task => task.output =
                        ModLibrary.McLibNetFilesFromInstance(new McInstance(versionFolder)))
                { ProgressWeight = 1d, show = false });
            loaders.Add(
                new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLabyModClientJson"),
                    new List<DownloadFile>()) { ProgressWeight = 8d });
        }

        return loaders;
    }




    public static void McDownloadLegacyFabricLoaderSave(JsonObject downloadInfo)
    {
        try
        {
            var url = downloadInfo["url"].ToString();
            var fileName = ModBase.GetFileNameFromPath(url);
            var version = ModBase.GetFileNameFromPath(downloadInfo["version"].ToString());
            var target = SystemDialogs.SelectSaveFile(Lang.Text("Download.Version.SelectSaveLocation"), fileName, Lang.Text("Download.Version.Installer.LegacyFabric.Filter"));
            if (!target.Contains(@"\"))
                return;

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") !=
                    (Lang.Text("Minecraft.Download.Stage.LegacyFabricInstallerDownload", version) ?? ""))
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
                new ModLoader.LoaderCombo<JsonObject>(
                        Lang.Text("Minecraft.Download.Stage.LegacyFabricInstallerDownload", version), loaders)
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
                "开始 Legacy Fabric 安装器下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     获取下载某个 LegacyFabric 实例的加载器列表。
    /// </summary>
    private static List<ModLoader.LoaderBase> McDownloadLegacyFabricLoader(string legacyFabricVersion,
        string minecraftName, string mcFolder = null, bool fixLibrary = true)
    {
        // 参数初始化
        mcFolder = mcFolder ?? ModFolder.mcFolderSelected;
        var isCustomFolder = (mcFolder ?? "") != (ModFolder.mcFolderSelected ?? "");
        var id = "legacy-fabric-loader-" + legacyFabricVersion + "-" + minecraftName;
        var versionFolder = Path.Combine(mcFolder, "versions", id);
        var loaders = new List<ModLoader.LoaderBase>();

        // 下载 Json
        loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.ObtainLegacyFabricMainFileUrl"), task =>
        {
            // 启动依赖实例的下载
            if (fixLibrary)
                McDownloadClient(NetPreDownloadBehaviour.ExitWhileExistsOrDownloading, minecraftName);
            task.Progress = 0.5d;
            // 构造文件请求
            task.output = new List<DownloadFile>
            {
                new(
                    new[]
                    {
                        "https://meta.legacyfabric.net/v2/versions/loader/" + minecraftName + "/" +
                        legacyFabricVersion + "/profile/json"
                    }, Path.Combine(versionFolder, id + ".json"), new ModBase.FileChecker(isJson: true))
            };
        })
        {
            ProgressWeight = 0.5d
        });
        loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLoaderMainFile", "Legacy Fabric"),
                new List<DownloadFile>())
            { ProgressWeight = 2.5d });

        // 下载支持库
        if (fixLibrary)
        {
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                    Lang.Text("Minecraft.Download.Stage.AnalyzeLegacyFabricLibraries"),
                    task => task.output =
                        ModLibrary.McLibNetFilesFromInstance(new McInstance(versionFolder)))
                { ProgressWeight = 1d, show = false });
            loaders.Add(new LoaderDownload(
                    Lang.Text("Minecraft.Download.Stage.DownloadLoaderLibraries", "Legacy Fabric"),
                    new List<DownloadFile>())
                { ProgressWeight = 8d });
        }

        return loaders;
    }




    public static MyListItem FabricDownloadListItem(JsonObject entry, MyListItem.ClickEventHandler onClick)
    {
        // 建立控件
        var newItem = new MyListItem
        {
            Title = entry["version"].ToString().Replace("+build", ""),
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = entry,
            Info = entry["stable"].ToObject<bool>() ? Lang.Text("Download.Version.Type.Stable") : Lang.Text("Download.Version.Type.Preview"),
            Logo = ModBase.pathImage + "Blocks/Fabric.png"
        };
        newItem.Click += onClick;
        newItem.ContentHandler = FabricContMenuBuild;
        // 结束
        return newItem;
    }


    private static void FabricContMenuBuild(object sender, EventArgs e)
    {
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (a, b) => FabricLog_Click(a, (dynamic)b);
        ((dynamic)sender).Buttons = new[] { btnInfo };
    }


    private static void FabricLog_Click(object sender, RoutedEventArgs e)
    {
        ModBase.OpenWebsite("https://fabricmc.net/blog");
    }


    public static MyListItem FabricApiDownloadListItem(ModComp.CompFile entry, MyListItem.ClickEventHandler onClick)
    {
        // 建立控件
        var newItem = new MyListItem
        {
            Title = entry.DisplayName.Split("]")[1].Replace("Fabric API ", "").Replace(" build ", ".").Trim(),
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = entry,
            Info = entry.StatusDescription + Lang.Text("Download.Version.ReleaseDate", Lang.Date(entry.ReleaseDate, "g")),
            Logo = ModBase.pathImage + "Blocks/Fabric.png"
        };
        newItem.Click += onClick;
        // 结束
        return newItem;
    }


    public static MyListItem OptiFabricDownloadListItem(ModComp.CompFile entry, MyListItem.ClickEventHandler onClick)
    {
        // 建立控件
        var newItem = new MyListItem
        {
            Title = entry.DisplayName.ToLower().Replace("optifabric-", "").Replace(".jar", "").Trim().TrimStart('v'),
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = entry,
            Info = entry.StatusDescription + Lang.Text("Download.Version.ReleaseDate", Lang.Date(entry.ReleaseDate, "g")),
            Logo = ModBase.pathImage + "Blocks/OptiFabric.png"
        };
        newItem.Click += onClick;
        // 结束
        return newItem;
    }




    public static MyListItem LegacyFabricDownloadListItem(JsonObject entry, MyListItem.ClickEventHandler onClick)
    {
        // 建立控件
        var newItem = new MyListItem
        {
            Title = entry["version"].ToString(),
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = entry,
            Info = entry["stable"].ToObject<bool>() ? Lang.Text("Download.Version.Type.Stable") : Lang.Text("Download.Version.Type.Preview"),
            Logo = ModBase.pathImage + "Blocks/Fabric.png"
        };
        newItem.Click += onClick;
        // 结束
        return newItem;
    }


    public static MyListItem LegacyFabricApiDownloadListItem(ModComp.CompFile entry,
        MyListItem.ClickEventHandler onClick)
    {
        // 建立控件
        var newItem = new MyListItem
        {
            Title = entry.DisplayName.Replace("Legacy Fabric API ", ""),
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = entry,
            Info = entry.StatusDescription + Lang.Text("Download.Version.ReleaseDate", Lang.Date(entry.ReleaseDate, "g")),
            Logo = ModBase.pathImage + "Blocks/Fabric.png"
        };
        newItem.Click += onClick;
        // 结束
        return newItem;
    }

}
