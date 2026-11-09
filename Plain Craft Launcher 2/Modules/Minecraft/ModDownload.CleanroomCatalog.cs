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



    public struct DlCleanroomListResult
    {
        /// <summary>
        ///     数据来源名称，如“Official”，“BMCLAPI”。
        /// </summary>
        public string sourceName;

        /// <summary>
        ///     是否为官方的实时数据。
        /// </summary>
        public bool isOfficial;

        /// <summary>
        ///     所有版本的列表。已经按从新到老排序。
        /// </summary>
        public List<DlCleanroomListEntry> Value;
    }


    public class DlCleanroomListEntry : DlForgelikeEntry
    {
        /// <summary>
        ///     API 使用的原始版本字符串，如 “0.2.4-alpha”。
        /// </summary>
        public string ApiName;

        /// <summary>
        ///     是否是 Beta 版。
        /// </summary>
        public bool IsBeta;

        public DlCleanroomListEntry(string apiName)
        {
            forgeType = ForgelikeType.Cleanroom;
            this.ApiName = apiName;
            IsBeta = apiName.Contains("alpha");
            VersionName = apiName;
            version = new Version(apiName.BeforeFirst("-"));
            Inherit = "1.12.2";
        }

        /// <summary>
        ///     文件在官网的基础地址，不包含后缀。
        /// </summary>
        public string UrlBase =>
            $"https://github.com/CleanroomMC/Cleanroom/releases/download/{ApiName}/cleanroom-{ApiName}";
    }


    private static void DlCleanroomListMain(ModLoader.LoaderTask<int, DlCleanroomListResult> loader)
    {
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlCleanroomListResult>, int>>
                        { new(dlCleanroomListOfficialLoader, 30) }, loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlCleanroomListResult>, int>>
                        { new(dlCleanroomListOfficialLoader, 5) }, loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlCleanroomListResult>, int>>
                        { new(dlCleanroomListOfficialLoader, 60) }, loader.isForceRestarting);
                break;
            }
        }
    }


    private static void DlCleanroomListOfficialMain(ModLoader.LoaderTask<int, DlCleanroomListResult> loader)
    {
        // 获取版本列表 JSON
        var resultLatest = Requester.FetchJson(
            "https://api.github.com/repos/CleanroomMC/Cleanroom/releases", new RequestParam
            {
                UseBrowserUserAgent = true
            }).ToString();
        if (resultLatest.Length < 100)
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Cleanroom",
                resultLatest));
        // 解析
        try
        {
            loader.output = new DlCleanroomListResult
            {
                isOfficial = true,
                sourceName = Lang.Text("Download.Source.CleanroomOfficial"),
                Value = GetCleanroomEntries(resultLatest)
            };
        }
        catch (Exception ex)
        {
            throw new Exception(
                Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Cleanroom", resultLatest), ex);
        }
    }


    private static List<DlCleanroomListEntry> GetCleanroomEntries(string latestJson)
    {
        var versions = new List<DlCleanroomListEntry>();
        var json = JsonArray.Parse(latestJson);
        foreach (JsonObject Token in json.AsArray())
            versions.Add(new DlCleanroomListEntry(Token["tag_name"].ToString())
                { forgeType = (DlForgelikeEntry.ForgelikeType)2 });
        if (!versions.Any())
            throw new Exception(Lang.Text("Minecraft.Download.Error.NoAvailableVersion"));
        versions = versions.OrderByDescending(a => a.version).ToList();
        return versions;
    }

}
