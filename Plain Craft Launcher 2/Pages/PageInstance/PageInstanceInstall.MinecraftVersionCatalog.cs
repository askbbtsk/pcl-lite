using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceInstall
{



    // 结果数据化
    private static string GetVersionTypeTitle(string key) => key switch
    {
        "正式版" => Lang.Text("Download.Version.Type.Release"),
        "预览版" => Lang.Text("Download.Version.Type.Development"),
        "远古版" => Lang.Text("Download.Version.Type.BeforeRelease"),
        _ => key
    };


    private void LoadMinecraft_OnFinish()
    {
        ExitSelectPage(); // 返回
        do
        {
            try
            {
                var dict = new Dictionary<string, List<JsonObject>>
                {
                    { "正式版", new List<JsonObject>() }, { "预览版", new List<JsonObject>() }, { "远古版", new List<JsonObject>() }
                };
                var versions = (JsonArray)ModDownload.dlClientListLoader.output.Value["versions"];
                foreach (JsonObject Version in versions)
                {
                    if (McVersionClassifier.ClassifyVersion(Version) is not { } versionCategory)
                        continue;
                    var type = versionCategory switch
                    {
                        McVersionCategory.Release => "正式版",
                        McVersionCategory.Snapshot => "预览版",
                        _ => "远古版"
                    };

                    // 加入辞典
                    dict[type].Add(Version);
                }

                // 排序
                foreach (var Pair in dict.ToList())
                    dict[Pair.Key] = Pair.Value.OrderByDescending(McVersionClassifier.GetReleaseTime).ToList();
                // 清空当前
                PanMinecraft.Children.Clear();
                // 添加最新版本
                var cardInfo = new MyCard { Title = Lang.Text("Download.Version.Latest.Title"), Margin = new Thickness(0d, 15d, 0d, 15d) };
                var topestVersions = new List<JsonObject>();
                var release = (JsonObject)dict["正式版"][0].DeepClone();
                release["lore"] = Lang.Text("Download.Version.Latest.Release", Lang.Date(release["releaseTime"].ToObject<DateTime>(), "g"));
                topestVersions.Add(release);
                if (dict["正式版"][0]["releaseTime"].ToObject<DateTime>() < dict["预览版"][0]["releaseTime"].ToObject<DateTime>())
                {
                    var snapshot = (JsonObject)dict["预览版"][0].DeepClone();
                    snapshot["lore"] = Lang.Text("Download.Version.Latest.Development", Lang.Date(snapshot["releaseTime"].ToObject<DateTime>(), "g"));
                    topestVersions.Add(snapshot);
                }

                var panInfo = new StackPanel
                {
                    Margin = new Thickness(20d, MyCard.SwapedHeight, 18d, 0d),
                    VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(0d, 0d),
                    Tag = topestVersions
                };

                void StackInstall(StackPanel stack)
                {
                    foreach (var item in (IEnumerable)stack.Tag)
                        stack.Children.Add(ModDownloadLib.McDownloadListItem((JsonObject)item,
                            (sender, e) => MinecraftSelected((MyListItem)sender, e), false));
                }

                ;
                MyCard.StackInstall(ref panInfo, StackInstall);
                cardInfo.Children.Add(panInfo);
                PanMinecraft.Children.Insert(0, cardInfo);
                // 添加其他版本
                foreach (var Pair in dict)
                {
                    if (!Pair.Value.Any())
                        continue;
                    // 增加卡片
                    var newCard = new MyCard
                        { Title = GetVersionTypeTitle(Pair.Key) + " (" + Pair.Value.Count + ")", Margin = new Thickness(0d, 0d, 0d, 15d) };
                    var newStack = new StackPanel
                    {
                        Margin = new Thickness(20d, MyCard.SwapedHeight, 18d, 0d),
                        VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(0d, 0d),
                        Tag = Pair.Value
                    };
                    newCard.Children.Add(newStack);
                    newCard.SwapControl = newStack;
                    // 不能使用 AddressOf，这导致了 #535，原因完全不明，疑似是编译器 Bug
                    newCard.InstallMethod = StackInstall;
                    newCard.IsSwapped = true;
                    PanMinecraft.Children.Add(newCard);
                }

                // 自动选择版本
                if (mcVersionWaitingForSelect is null)
                    break;
                ModBase.Log("[Download] 自动选择 MC 版本：" + mcVersionWaitingForSelect);
                foreach (JsonObject Version in versions)
                {
                    if ((Version["id"].ToString() ?? "") != (mcVersionWaitingForSelect ?? ""))
                        continue;
                    var item = ModDownloadLib.McDownloadListItem(Version, (_, _) => { }, false);
                    MinecraftSelected(item, null);
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "可视化安装版本列表出错",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
            }
        } while (false);
    }

}
