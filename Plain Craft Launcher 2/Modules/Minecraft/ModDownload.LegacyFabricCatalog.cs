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

    private static void DlLegacyFabricListMain(ModLoader.LoaderTask<int, DlLegacyFabricListResult> loader)
    {
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlLegacyFabricListResult>, int>>
                        { new(dlLegacyFabricListOfficialLoader, 30) }, loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlLegacyFabricListResult>, int>>
                        { new(dlLegacyFabricListOfficialLoader, 5) }, loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlLegacyFabricListResult>, int>>
                        { new(dlLegacyFabricListOfficialLoader, 60) }, loader.isForceRestarting);
                break;
            }
        }
    }


    private static void DlLegacyFabricListOfficialMain(ModLoader.LoaderTask<int, DlLegacyFabricListResult> loader)
    {
        var result =
            (JsonObject)Requester.FetchJson("https://meta.legacyfabric.net/v2/versions");
        try
        {
            var output = new DlLegacyFabricListResult
                { isOfficial = true, sourceName = Lang.Text("Download.Source.LegacyFabricOfficial"), Value = result };
            if (output.Value["game"] is null || output.Value["loader"] is null || output.Value["installer"] is null)
                throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "LegacyFabric",
                    result));
            loader.output = output;
        }
        catch (Exception ex)
        {
            throw new Exception(
                Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "LegacyFabric", result), ex);
        }
    }



    /// <summary>
    ///     发送 Minecraft 更新提示。
    /// </summary>
    public static void McDownloadClientUpdateHint(string versionName, JsonObject json)
    {
        try
        {
            // 获取对应版本
            JsonNode version = null;
            foreach (var Token in json["versions"].AsArray())
                if (Token["id"] is not null && (Token["id"].ToString() ?? "") == (versionName ?? ""))
                {
                    version = Token;
                    break;
                }

            // 进行提示
            if (version is null)
                return;
            var time = version["releaseTime"].ToObject<DateTime>();
            var msgBoxText = Lang.Text("Minecraft.Update.NewVersion", versionName) + "\r\n" +
                             ((DateTime.Now - time).TotalDays > 1d
                                 ? Lang.Text("Minecraft.Update.UpdateTime") + Lang.Date(time)
                                 : Lang.Text("Minecraft.Update.UpdatedAt") + Lang.TimeSpan(time - DateTime.Now));
            var msgResult = ModMain.MyMsgBox(msgBoxText, Lang.Text("Minecraft.Update.Title"),
                Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Download"),
                (DateTime.Now - time).TotalHours > 3d ? Lang.Text("Common.Action.UpdateLog") : "",
                button3Action: () => ModDownloadLib.McUpdateLogShow(version));
            // 弹窗结果
            if (msgResult == 2)
                // 下载
                ModBase.RunInUi(() =>
                {
                    PageDownloadInstall.mcVersionWaitingForSelect = versionName;
                    ModMain.frmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall);
                });
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Minecraft.Error.UpdateNotify", versionName ?? "Nothing"),
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Error.UpdateNotify", versionName ?? "Nothing"));
        }
    }

}
