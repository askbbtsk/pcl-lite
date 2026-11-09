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



    public static MyListItem McDownloadListItem(JsonObject entry, MyListItem.ClickEventHandler onClick, bool isSaveOnly)
    {
        // 确定图标
        string logo = entry["type"].ToString() switch
        {
            "release" => ModBase.pathImage + "Blocks/Grass.png",
            "snapshot" => ModBase.pathImage + "Blocks/CommandBlock.png",
            "pending" => ModBase.pathImage + "Blocks/CommandBlock.png",
            "special" => ModBase.pathImage + "Blocks/GoldBlock.png",
            _ => ModBase.pathImage + "Blocks/CobbleStone.png"
        };

        // 建立控件
        var formattedVersion = McFormatter.FormatVersion(entry["id"].ToString()).Replace("_", " ");
        var newItem = new MyListItem
        {
            Logo = logo, SnapsToDevicePixels = true, Title = formattedVersion, Height = 42d,
            Type = MyListItem.CheckType.Clickable, Tag = entry
        };
        if (entry["lore"] is null)
        {
            if (formattedVersion != (string)entry["id"])
                newItem.Info = Lang.Date(entry["releaseTime"].ToObject<DateTime>(), "g") + " | " +
                               entry["id"];
            else
                newItem.Info = Lang.Date(entry["releaseTime"].ToObject<DateTime>(), "g");
        }
        else if (formattedVersion != (string)entry["id"])
        {
            newItem.Info = entry["lore"] + " | " + entry["id"];
        }
        else
        {
            newItem.Info = entry["lore"].ToString();
        }

        if (entry["url"].ToString().Contains("unlisted-versions-of-minecraft"))
            newItem.Tags = Lang.Text("Download.Source.Tag.Uvmc");
        newItem.Click += onClick;
        // 建立菜单
        if (isSaveOnly)
            newItem.ContentHandler = McDownloadSaveMenuBuild;
        else
            newItem.ContentHandler = McDownloadMenuBuild;
        // 结束
        return newItem;
    }


    private static void McDownloadSaveMenuBuild(object sender, EventArgs _)
    {
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (ss, ee) => McDownloadMenuLog(ss, (dynamic)ee);
        var btnServer = new MyIconButton { LogoScale = 1d, SvgIcon = "lucide/server", ToolTip = Lang.Text("Download.Version.DownloadServer") };
        ToolTipService.SetPlacement(btnServer, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnServer, 30d);
        ToolTipService.SetHorizontalOffset(btnServer, 2d);
        btnServer.Click += (ss, ee) => McDownloadMenuSaveServer(ss, (dynamic)ee);
        ((dynamic)sender).Buttons = new[] { btnServer, btnInfo };
    }


    private static void McDownloadMenuBuild(object sender, EventArgs e)
    {
        var btnSave = new MyIconButton { SvgIcon = "lucide/save", ToolTip = Lang.Text("Download.Version.SaveAs") };
        ToolTipService.SetPlacement(btnSave, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnSave, 30d);
        ToolTipService.SetHorizontalOffset(btnSave, 2d);
        btnSave.Click += (a, b) => McDownloadMenuSave(a, (dynamic)b); // dynamic!
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (a, b) => McDownloadMenuLog(a, (dynamic)b); // dynamic!
        var btnServer = new MyIconButton { LogoScale = 1d, SvgIcon = "lucide/server", ToolTip = Lang.Text("Download.Version.DownloadServer") };
        ToolTipService.SetPlacement(btnServer, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnServer, 30d);
        ToolTipService.SetHorizontalOffset(btnServer, 2d);
        btnServer.Click += (a, b) => McDownloadMenuSaveServer(a, (dynamic)b); // dynamic!
        ((dynamic)sender).Buttons = new[] { btnSave, btnInfo, btnServer };
    }


    private static void McDownloadMenuLog(object sender, RoutedEventArgs e)
    {
        JsonNode version;
        if (((dynamic)sender).Tag is not null)
            version = (JsonNode)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            version = (JsonNode)((dynamic)sender).Parent.Tag;
        else
            version = (JsonNode)((dynamic)sender).Parent.Parent.Tag;
        McUpdateLogShow(version);
    }


    private static void McDownloadMenuSaveServer(object sender, RoutedEventArgs e)
    {
        MyListItem version;
        if (sender is MyListItem)
            version = (MyListItem)sender;
        else if (((dynamic)sender).Parent is MyListItem)
            version = (MyListItem)((dynamic)sender).Parent;
        else
            version = (MyListItem)((dynamic)sender).Parent.Parent;
        try
        {
            var id = version.Title;
            string jsonUrl = ((dynamic)version.Tag)["url"].ToString();
            var versionFolder = SystemDialogs.SelectFolder();
            if (!versionFolder.Contains(@"\"))
                return;
            versionFolder = Path.Combine(versionFolder, id);

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") !=
                    (Lang.Text("Minecraft.Download.Stage.MinecraftServerDownload", id) ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.ServerDownloading"), HintType.Error);
                return;
            }

            var loaders = new List<ModLoader.LoaderBase>();
            // 下载实例 JSON 文件
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadInstanceJson"),
                new List<DownloadFile>
                {
                    new(ModDownload.DlSourceLauncherOrMetaGet(jsonUrl), Path.Combine(versionFolder, id + ".json"),
                        new ModBase.FileChecker(canUseExistsFile: false, isJson: true))
                }) { ProgressWeight = 2d });
            // 构建服务端
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                Lang.Text("Minecraft.Download.Stage.BuildServer"), task =>
            {
                // 分析服务端 JAR 文件下载地址
                var mcInstance = new McInstance(versionFolder);
                if (mcInstance.JsonObject["downloads"] is null ||
                    mcInstance.JsonObject["downloads"]["server"] is null ||
                    mcInstance.JsonObject["downloads"]["server"]["url"] is null)
                {
                    File.Delete(Path.Combine(versionFolder, id + ".json"));
                    if (!new DirectoryInfo(versionFolder).GetFileSystemInfos().Any())
                        Directory.Delete(versionFolder);
                    task.output = new List<DownloadFile>();
                    HintService.Hint(Lang.Text("Minecraft.Download.Error.NoOfficialServerDownload", id),
                        HintType.Error);
                    Thread.Sleep(2000); // 等玩家把上一个提示看完
                    task.Abort();
                    return;
                }

                var jarUrl = (string)mcInstance.JsonObject["downloads"]["server"]["url"];
                var checker = new ModBase.FileChecker(1024L,
                    (long)(mcInstance.JsonObject["downloads"]["server"]["size"] ?? -1),
                    (string)mcInstance.JsonObject["downloads"]["server"]["sha1"]);
                task.output = new List<DownloadFile>
                    { new(ModDownload.DlSourceLauncherOrMetaGet(jarUrl), Path.Combine(versionFolder, id + "-server.jar"), checker) };
                // 添加启动脚本
                var bat = $"""
                           @echo off
                           title {Lang.Text("Minecraft.Download.ServerBatch.Title", id)}
                           echo {Lang.Text("Minecraft.Download.ServerBatch.InstructionJavaPath")}
                           echo {Lang.Text("Minecraft.Download.ServerBatch.InstructionPclSettings")}
                           echo ------------------------------
                           echo {Lang.Text("Minecraft.Download.ServerBatch.InstructionEula")}
                           echo ------------------------------
                           "java" -server -XX:+UseG1GC -Xmx4096M -Xms1024M -XX:+UseCompressedOops -jar {id}-server.jar nogui
                           echo ----------------------
                           echo {Lang.Text("Minecraft.Download.ServerBatch.ServerStopped")}
                           pause
                           """;
                ModBase.WriteFile(Path.Combine(versionFolder, "Launch Server.bat"), bat.Replace("\n", "\r\n"),
                    encoding: Encoding.Default.Equals(Encoding.UTF8) ? Encoding.UTF8 : Encoding.GetEncoding("GB18030"));
                // 删除实例 JSON
                File.Delete(Path.Combine(versionFolder, id + ".json"));
            })
            {
                ProgressWeight = 0.5d,
                show = false
            });
            // 下载服务端文件
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadServerFile"), [])
                { ProgressWeight = 5d });

            // 启动
            var loader =
                new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Download.Stage.MinecraftServerDownload", id),
                        loaders)
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
                "开始 Minecraft 服务端下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    public static void McDownloadMenuSave(object sender, RoutedEventArgs e)
    {
        if (!RestrictedFeaturePolicy.Check("minecraft-client-save")) return;
        var element = (FrameworkElement)sender;
        MyListItem version;
        if (element is MyListItem s1) version = s1;
        else if (element.Parent is MyListItem s2) version = s2;
        else version = (MyListItem)((FrameworkElement)element.Parent).Parent;
        try
        {
            var id = version.Title;
            var jsonUrl = ((JsonObject)version.Tag)["url"]!.ToString();
            var versionFolder = SystemDialogs.SelectFolder();
            if (!versionFolder.Contains(@"\"))
                return;
            versionFolder = Path.Combine(versionFolder, id);

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") != (Lang.Text("Minecraft.Download.Stage.MinecraftDownload", id) ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            var loaders = new List<ModLoader.LoaderBase>();
            // 下载实例 JSON 文件
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadInstanceJson"),
                new List<DownloadFile>
                {
                    new(ModDownload.DlSourceLauncherOrMetaGet(jsonUrl), Path.Combine(versionFolder, id + ".json"),
                        new ModBase.FileChecker(canUseExistsFile: false, isJson: true))
                }) { ProgressWeight = 2d });
            // 获取支持库文件地址
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                    Lang.Text("Minecraft.Download.Stage.AnalyzeCoreJarUrl"),
                    task => task.output = new List<DownloadFile>
                        { ModDownload.DlClientJarGet(new McInstance(versionFolder), false) })
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
    ///     显示某 Minecraft 版本的更新日志。
    /// </summary>
    /// <param name="versionJson">在 version_manifest.json 中的对应项。</param>
    public static void McUpdateLogShow(JsonNode versionJson)
    {
        var wikiName = McFormatter.GetWikiUrlSuffix(versionJson["id"].ToString());
        var wikiUrl = $"{McFormatter.GetWikiBaseUrl()}/{wikiName.TrimStart('/')}";
        ModBase.OpenWebsite(wikiUrl);
    }

}
