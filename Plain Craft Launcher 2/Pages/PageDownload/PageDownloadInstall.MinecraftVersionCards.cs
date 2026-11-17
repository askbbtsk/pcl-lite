using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PCL.Core.App;
using PCL.Core.Utils.Validate;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageDownloadInstall
{



    // 结果数据化
    private void LoadMinecraft_OnFinish()
    {
        ExitSelectPage(); // 返回

        try
        {
            if (ModDownload.dlClientListLoader.output.Value["versions"] is not JsonArray versions)
                return;

            _RebuildVersionCards(versions);

            if (mcVersionWaitingForSelect is null) return;

            ModBase.Log("[Download] 自动选择 MC 版本：" + mcVersionWaitingForSelect);

            foreach (JsonObject version1 in versions)
            {
                if (((string)version1["id"] ?? "") != mcVersionWaitingForSelect)
                    continue;

                var item = ModDownloadLib.McDownloadListItem(version1, (_, _) => { }, false);
                MinecraftSelected(item, null);
                break;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Download.Install.Error.OperationFailed"));
        }
    }


    private void _RebuildVersionCards(JsonArray versions)
    {
        var categoryOrder = new[]
        {
            McVersionCategory.Release,
            McVersionCategory.Snapshot,
            McVersionCategory.BeforeRelease,
            McVersionCategory.AprilFools
        };

        var dict = categoryOrder.ToDictionary(
            category => category,
            _ => new List<JsonObject>()
        );

        foreach (JsonObject version in versions)
        {
            var category = McVersionClassifier.ClassifyVersion(version);
            dict[category].Add(version);
        }

        foreach (var category in categoryOrder)
            dict[category] = dict[category]
                .OrderByDescending(McVersionClassifier.GetReleaseTime)
                .ToList();

        PanMinecraft.Children.Clear();

        _AddLatestVersionCard(dict);
        _AddCategoryCards(dict, categoryOrder);
    }


    private void _OnLanguageChanged() => ModBase.RunInUi(() =>
    {
        LoadMinecraft.Text = Lang.Text("Download.Version.LoadingList");
        if (ModDownload.dlClientListLoader.output.Value?["versions"] is JsonArray versions)
            _RebuildVersionCards(versions);
    });


    private void _AddLatestVersionCard(Dictionary<McVersionCategory, List<JsonObject>> dict)
    {
        var latestRelease = dict[McVersionCategory.Release].FirstOrDefault();
        var latestSnapshot = dict[McVersionCategory.Snapshot].FirstOrDefault();

        var latestVersions = new List<JsonObject>();

        if (latestRelease is not null)
        {
            var release = (JsonObject)latestRelease.DeepClone();
            release["lore"] = Lang.Text(
                "Download.Version.Latest.Release",
                Lang.Date(McVersionClassifier.GetReleaseTime(release), "g")
            );
            latestVersions.Add(release);
        }

        if (latestSnapshot is not null &&
            (latestRelease is null || McVersionClassifier.GetReleaseTime(latestRelease) <
                McVersionClassifier.GetReleaseTime(latestSnapshot)))
        {
            var snapshot = (JsonObject)latestSnapshot.DeepClone();
            snapshot["lore"] = Lang.Text(
                "Download.Version.Latest.Development",
                Lang.Date(McVersionClassifier.GetReleaseTime(snapshot), "g")
            );
            latestVersions.Add(snapshot);
        }

        if (latestVersions.Count == 0)
            return;

        var cardInfo = new MyCard
        {
            Title = Lang.Text("Download.Version.Latest.Title"),
            Margin = new Thickness(0d, 15d, 0d, 15d)
        };

        var panInfo = _CreateVersionStack(latestVersions);
        MyCard.StackInstall(ref panInfo, _StackInstall);

        cardInfo.Children.Add(panInfo);
        PanMinecraft.Children.Insert(0, cardInfo);
    }


    private void _AddCategoryCards(
        Dictionary<McVersionCategory, List<JsonObject>> dict,
        IEnumerable<McVersionCategory> categoryOrder)
    {
        foreach (var category in categoryOrder)
        {
            var versions = dict[category];
            if (versions.Count == 0)
                continue;

            var card = new MyCard
            {
                Title = $"{McVersionClassifier.GetCategoryDisplayName(category)} ({versions.Count})",
                Margin = new Thickness(0d, 0d, 0d, 15d)
            };

            var stack = _CreateVersionStack(versions);

            card.Children.Add(stack);
            card.SwapControl = stack;

            // 不能使用 AddressOf，这导致了 #535，原因完全不明，疑似是编译器 Bug
            card.InstallMethod = _StackInstall;
            card.IsSwapped = true;

            PanMinecraft.Children.Add(card);
        }
    }


    private static StackPanel _CreateVersionStack(IEnumerable<JsonObject> versions)
    {
        return new StackPanel
        {
            Margin = new Thickness(20d, MyCard.SwapedHeight, 18d, 0d),
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new TranslateTransform(0d, 0d),
            Tag = versions
        };
    }


    private static void _StackInstall(StackPanel stack)
    {
        foreach (var item in (IEnumerable)stack.Tag)
            stack.Children.Add(
                ModDownloadLib.McDownloadListItem(
                    (JsonObject)item,
                    (sender, e) => ModMain.frmDownloadInstall.MinecraftSelected((MyListItem)sender, e),
                    false
                )
            );
    }

}
