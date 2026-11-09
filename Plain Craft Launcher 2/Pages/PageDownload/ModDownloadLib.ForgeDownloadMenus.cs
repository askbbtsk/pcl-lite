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



    public static void ForgeDownloadListItemPreload(StackPanel stack, List<ModDownload.DlForgeVersionEntry> entries,
        MyListItem.ClickEventHandler onClick, bool isSaveOnly)
    {
        // 如果只有一个版本，则不特别列出
        if (entries.Count == 1)
            return;
        // 获取推荐版本与最新版本
        ModDownload.DlForgeVersionEntry freshVersion = null;
        if (entries.Any())
            freshVersion = entries[0];
        else
            ModBase.Log("[System] 未找到可用的 Forge 版本", ModBase.LogLevel.Debug);
        ModDownload.DlForgeVersionEntry recommendedVersion = null;
        foreach (var Entry in entries)
            if (Entry.IsRecommended)
                recommendedVersion = Entry;
        // 若推荐版本与最新版本为同一版本，则仅显示推荐版本
        if (freshVersion is not null && ReferenceEquals(freshVersion, recommendedVersion))
            freshVersion = null;
        // 显示各个版本
        if (recommendedVersion is not null)
        {
            var recommended = ForgeDownloadListItem(recommendedVersion, onClick, isSaveOnly);
            recommended.Info = Lang.Text("Download.Version.Type.Recommended") + (string.IsNullOrEmpty(recommended.Info) ? "" : "  |  " + recommended.Info);
            stack.Children.Add(recommended);
        }

        if (freshVersion is not null)
        {
            var fresh = ForgeDownloadListItem(freshVersion, onClick, isSaveOnly);
            fresh.Info = Lang.Text("Download.Version.Latest.Title") + (string.IsNullOrEmpty(fresh.Info) ? "" : "  |  " + fresh.Info);
            stack.Children.Add(fresh);
        }

        // 添加间隔
        stack.Children.Add(new TextBlock
        {
            Text = Lang.Text("Download.Version.AllVersions", entries.Count), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6d, 13d, 0d, 4d)
        });
    }


    public static MyListItem ForgeDownloadListItem(ModDownload.DlForgeVersionEntry entry,
        MyListItem.ClickEventHandler onClick, bool isSaveOnly)
    {
        var infoParts = new List<string>();

        if (!string.IsNullOrEmpty(entry.ReleaseTime))
            infoParts.Add(Lang.Text("Download.Version.ReleaseDate", entry.ReleaseTime));

        if (ModBase.modeDebug)
            infoParts.Add(Lang.Text("Download.Version.Forge.Type", entry.Category));

        // 建立控件
        var newItem = new MyListItem
        {
            Title = entry.VersionName,
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = entry,
            Info = string.Join("  |  ", infoParts),
            Logo = ModBase.pathImage + "Blocks/Anvil.png"
        };

        newItem.Click += onClick;
        // 建立菜单
        if (isSaveOnly)
            newItem.ContentHandler = ForgeSaveContMenuBuild;
        else
            newItem.ContentHandler = ForgeContMenuBuild;
        // 结束
        return newItem;
    }


    private static void ForgeContMenuBuild(MyListItem sender, EventArgs e)
    {
        var btnSave = new MyIconButton { SvgIcon = "lucide/save", ToolTip = Lang.Text("Download.Version.SaveAs") };
        ToolTipService.SetPlacement(btnSave, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnSave, 30d);
        ToolTipService.SetHorizontalOffset(btnSave, 2d);
        btnSave.Click += (ss, ee) => ForgeSave_Click(ss, (dynamic)ee);
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (ss, ee) => ForgeLog_Click(ss, (dynamic)ee);
        sender.Buttons = new[] { btnSave, btnInfo };
    }


    private static void ForgeSaveContMenuBuild(MyListItem sender, EventArgs e)
    {
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (ss, ee) => ForgeLog_Click(ss, (dynamic)e);
        sender.Buttons = new[] { btnInfo };
    }


    private static void ForgeLog_Click(object sender, RoutedEventArgs e)
    {
        ModDownload.DlForgeVersionEntry version;
        if (((dynamic)sender).Tag is not null)
            version = (ModDownload.DlForgeVersionEntry)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            version = (ModDownload.DlForgeVersionEntry)((dynamic)sender).Parent.Tag;
        else
            version = (ModDownload.DlForgeVersionEntry)((dynamic)sender).Parent.Parent.Tag;
        ModBase.OpenWebsite(
            $"https://files.minecraftforge.net/maven/net/minecraftforge/forge/{version.Inherit}-{version.VersionName}/forge-{version.Inherit}-{version.VersionName}-changelog.txt");
    }


    public static void ForgeSave_Click(object sender, RoutedEventArgs e)
    {
        ModDownload.DlForgeVersionEntry version;
        if (((dynamic)sender).Tag is not null)
            version = (ModDownload.DlForgeVersionEntry)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            version = (ModDownload.DlForgeVersionEntry)((dynamic)sender).Parent.Tag;
        else
            version = (ModDownload.DlForgeVersionEntry)((dynamic)sender).Parent.Parent.Tag;
        McDownloadForgelikeSave(version);
    }




    /// <summary>
    ///     尝试刷新 Forge 推荐版本缓存。
    /// </summary>
    public static void McDownloadForgeRecommendedRefresh()
    {
        if (isForgeRecommendedRefreshed)
            return;
        isForgeRecommendedRefreshed = true;
        // 获取所有推荐版本列表
        // 内容为："1.15.2":"31.2.0"
        // 保存
        ModBase.RunInNewThread(() =>
        {
            try
            {
                ModBase.Log("[Download] 刷新 Forge 推荐版本缓存开始");
                var result = ModNet.NetGetCodeByLoader("https://bmclapi2.bangbang93.com/forge/promos");
                if (result.Length < 1000) throw new Exception(Lang.Text("Minecraft.Download.Error.ForgePromosResultTooShort", result));
                var resultJson = (JsonNode)ModBase.GetJson(result);
                var recommendedList = new List<string>();
                foreach (JsonObject Version in resultJson.AsArray())
                {
                    if (Version["name"] is null || Version["build"] is null) continue;
                    var name = (string)Version["name"];
                    if (!name.EndsWithF("-recommended")) continue;
                    recommendedList.Add("\"" + name.Replace("-recommended",
                        "\":\"" + Version["build"]["version"] + "\""));
                }

                if (recommendedList.Count < 5)
                    throw new Exception(Lang.Text("Minecraft.Download.Error.ForgeRecommendedTooFew", result));
                var cacheJson = "{" + recommendedList.Join(",") + "}";
                ModBase.WriteFile(Path.Combine(ModBase.pathTemp, "Cache", "ForgeRecommendedList.json"), cacheJson);
                ModBase.Log("[Download] 刷新 Forge 推荐版本缓存成功");
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "刷新 Forge 推荐版本缓存失败");
            }
        }, "ForgeRecommendedRefresh");
    }


    /// <summary>
    ///     尝试获取某个 MC 版本对应的 Forge 推荐版本。如果不可用会返回 Nothing。
    /// </summary>
    public static string McDownloadForgeRecommendedGet(string mcInstance)
    {
        try
        {
            if (mcInstance is null)
                return null;
            var list = ModBase.ReadFile(Path.Combine(ModBase.pathTemp, "Cache", "ForgeRecommendedList.json"));
            if (list is null || string.IsNullOrEmpty(list))
            {
                ModBase.Log("[Download] 没有 Forge 推荐版本缓存文件");
                return null;
            }

            var json = (JsonObject)ModBase.GetJson(list);
            if (json is null || !(mcInstance ?? "null").Contains(".") || !json.ContainsKey(mcInstance))
                return null;
            return (json[mcInstance] ?? "").ToString();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "获取 Forge 推荐版本失败（" + (mcInstance ?? "null") + "）",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
            return null;
        }
    }




    public static void NeoForgeDownloadListItemPreload(StackPanel stack, List<ModDownload.DlNeoForgeListEntry> entries,
        MyListItem.ClickEventHandler onClick, bool isSaveOnly)
    {
        // 如果只有一个版本，则不特别列出
        if (entries.Count == 1)
            return;
        // 获取最新稳定版和测试版
        ModDownload.DlNeoForgeListEntry freshStableVersion = null;
        ModDownload.DlNeoForgeListEntry freshBetaVersion = null;
        if (entries.Any())
            foreach (var Entry in entries.ToList())
                if (Entry.IsBeta)
                {
                    if (freshBetaVersion is null)
                        freshBetaVersion = Entry;
                }
                else
                {
                    freshStableVersion = Entry;
                    break;
                }
        else
            ModBase.Log("[System] 未找到可用的 NeoForge 版本", ModBase.LogLevel.Debug);

        // 显示各个版本
        if (freshStableVersion is not null)
        {
            var fresh = NeoForgeDownloadListItem(freshStableVersion, onClick, isSaveOnly);
            fresh.Info = string.IsNullOrEmpty(fresh.Info)
                ? Lang.Text("Download.Version.Fresh.Stable")
                : Lang.Text("Download.Version.Fresh.Latest") + "  |  " + fresh.Info;
            stack.Children.Add(fresh);
        }

        if (freshBetaVersion is not null)
        {
            var fresh = NeoForgeDownloadListItem(freshBetaVersion, onClick, isSaveOnly);
            fresh.Info = string.IsNullOrEmpty(fresh.Info)
                ? Lang.Text("Download.Version.Fresh.Development")
                : Lang.Text("Download.Version.Fresh.Latest") + "  |  " + fresh.Info;
            stack.Children.Add(fresh);
        }

        // 添加间隔
        stack.Children.Add(new TextBlock
        {
            Text = Lang.Text("Download.Version.AllVersions", entries.Count), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6d, 13d, 0d, 4d)
        });
    }


    public static MyListItem NeoForgeDownloadListItem(ModDownload.DlNeoForgeListEntry info,
        MyListItem.ClickEventHandler onClick, bool isSaveOnly)
    {
        // 建立控件
        var newItem = new MyListItem
        {
            Title = info.VersionName,
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = info,
            Info = info.IsBeta ? Lang.Text("Download.Version.Type.Preview") : Lang.Text("Download.Version.Type.Stable"),
            Logo = ModBase.pathImage + "Blocks/NeoForge.png"
        };
        newItem.Click += onClick;
        // 建立菜单
        if (isSaveOnly)
            newItem.ContentHandler = NeoForgeSaveContMenuBuild;
        else
            newItem.ContentHandler = NeoForgeContMenuBuild;
        // 结束
        return newItem;
    }


    private static void NeoForgeContMenuBuild(MyListItem sender, EventArgs e)
    {
        var btnSave = new MyIconButton { SvgIcon = "lucide/save", ToolTip = Lang.Text("Download.Version.SaveAs") };
        ToolTipService.SetPlacement(btnSave, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnSave, 30d);
        ToolTipService.SetHorizontalOffset(btnSave, 2d);
        btnSave.Click += (sender, e) => NeoForgeSave_Click(sender, (RoutedEventArgs)e);
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (sender, e) => NeoForgeLog_Click(sender, (RoutedEventArgs)e);
        sender.Buttons = new[] { btnSave, btnInfo };
    }


    private static void NeoForgeSaveContMenuBuild(MyListItem sender, EventArgs e)
    {
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (sender, e) => NeoForgeLog_Click(sender, (RoutedEventArgs)e);
        sender.Buttons = new[] { btnInfo };
    }


    private static void NeoForgeLog_Click(object sender, RoutedEventArgs e)
    {
        ModDownload.DlNeoForgeListEntry info;
        if (((dynamic)sender).Tag is not null)
            info = (ModDownload.DlNeoForgeListEntry)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            info = (ModDownload.DlNeoForgeListEntry)((dynamic)sender).Parent.Tag;
        else
            info = (ModDownload.DlNeoForgeListEntry)((dynamic)sender).Parent.Parent.Tag;
        ModBase.OpenWebsite(info.UrlBase + "-changelog.txt");
    }


    public static void NeoForgeSave_Click(object sender, RoutedEventArgs e)
    {
        ModDownload.DlNeoForgeListEntry info;
        if (((dynamic)sender).Tag is not null)
            info = (ModDownload.DlNeoForgeListEntry)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            info = (ModDownload.DlNeoForgeListEntry)((dynamic)sender).Parent.Tag;
        else
            info = (ModDownload.DlNeoForgeListEntry)((dynamic)sender).Parent.Parent.Tag;
        McDownloadForgelikeSave(info);
    }




    public static void CleanroomDownloadListItemPreload(StackPanel stack,
        List<ModDownload.DlCleanroomListEntry> entries, MyListItem.ClickEventHandler onClick, bool isSaveOnly)
    {
        // 获取最新稳定版和测试版
        // Dim FreshStableVersion As DlCleanroomListEntry = Nothing
        ModDownload.DlCleanroomListEntry freshBetaVersion = null;
        if (entries.Any())
            freshBetaVersion = entries[0];
        else
            ModBase.Log("[System] 未找到可用的 Cleanroom 版本", ModBase.LogLevel.Debug);
        if (freshBetaVersion is not null)
        {
            var fresh = CleanroomDownloadListItem(freshBetaVersion, onClick, isSaveOnly);
            fresh.Info = string.IsNullOrEmpty(fresh.Info) ? Lang.Text("Download.Version.Fresh.Development") : Lang.Text("Download.Version.Fresh.Latest") + "  |  " + fresh.Info;
            stack.Children.Add(fresh);
        }

        // 添加间隔
        stack.Children.Add(new TextBlock
        {
            Text = Lang.Text("Download.Version.AllVersions", entries.Count), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(6d, 13d, 0d, 4d)
        });
    }


    public static MyListItem CleanroomDownloadListItem(ModDownload.DlCleanroomListEntry info,
        MyListItem.ClickEventHandler onClick, bool isSaveOnly)
    {
        // 建立控件
        var newItem = new MyListItem
        {
            Title = info.VersionName,
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = info,
            Info = info.IsBeta ? Lang.Text("Download.Version.Type.Preview") : Lang.Text("Download.Version.Type.Stable"),
            Logo = ModBase.pathImage + "Blocks/Cleanroom.png"
        };
        newItem.Click += onClick;
        // 建立菜单
        if (isSaveOnly)
            newItem.ContentHandler = CleanroomSaveContMenuBuild;
        else
            newItem.ContentHandler = CleanroomContMenuBuild;
        // 结束
        return newItem;
    }


    private static void CleanroomContMenuBuild(MyListItem sender, EventArgs e)
    {
        var btnSave = new MyIconButton { SvgIcon = "lucide/save", ToolTip = Lang.Text("Download.Version.SaveAs") };
        ToolTipService.SetPlacement(btnSave, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnSave, 30d);
        ToolTipService.SetHorizontalOffset(btnSave, 2d);
        btnSave.Click += (sender, _e) => CleanroomSave_Click(sender, (RoutedEventArgs)e);
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (sender, e) => CleanroomLog_Click(sender, (RoutedEventArgs)e);
        sender.Buttons = new[] { btnSave, btnInfo };
    }


    private static void CleanroomSaveContMenuBuild(MyListItem sender, EventArgs e)
    {
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (a, b) => CleanroomLog_Click(a, (dynamic)b);
        sender.Buttons = new[] { btnInfo };
    }


    private static void CleanroomLog_Click(object sender, RoutedEventArgs e)
    {
        ModDownload.DlCleanroomListEntry info;
        if (((dynamic)sender).Tag is not null)
            info = (ModDownload.DlCleanroomListEntry)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            info = (ModDownload.DlCleanroomListEntry)((dynamic)sender).Parent.Tag;
        else
            info = (ModDownload.DlCleanroomListEntry)((dynamic)sender).Parent.Parent.Tag;
        ModBase.OpenWebsite(info.UrlBase + "-changelog.txt");
    }


    public static void CleanroomSave_Click(object sender, RoutedEventArgs e)
    {
        ModDownload.DlCleanroomListEntry info;
        if (((dynamic)sender).Tag is not null)
            info = (ModDownload.DlCleanroomListEntry)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            info = (ModDownload.DlCleanroomListEntry)((dynamic)sender).Parent.Tag;
        else
            info = (ModDownload.DlCleanroomListEntry)((dynamic)sender).Parent.Parent.Tag;
        McDownloadForgelikeSave(info);
    }

}
