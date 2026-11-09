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



    public struct DlLiteLoaderListResult
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
        ///     获取到的数据。
        /// </summary>
        public List<DlLiteLoaderListEntry> Value;

        /// <summary>
        ///     官方源的失败原因。若没有则为 Nothing。
        /// </summary>
        public Exception officialError;
    }


    public class DlLiteLoaderListEntry
    {
        /// <summary>
        ///     实际的文件名，如“liteloader-installer-1.12-00-SNAPSHOT.jar”。
        /// </summary>
        public string FileName;

        /// <summary>
        ///     对应的 Minecraft 版本，如“1.12.2”。
        /// </summary>
        public string Inherit;

        /// <summary>
        ///     是否为 1.7 及更早的远古版。
        /// </summary>
        public bool IsLegacy;

        /// <summary>
        ///     是否为测试版。
        /// </summary>
        public bool IsPreview;

        /// <summary>
        ///     对应的 Json 项。
        /// </summary>
        public JsonNode jsonToken;

        /// <summary>
        ///     文件的 MD5。
        /// </summary>
        public string MD5;

        /// <summary>
        ///     发布时间，格式为“yyyy/mm/dd HH:mm”。
        /// </summary>
        public string ReleaseTime;
    }


    private static void DlLiteLoaderListMain(ModLoader.LoaderTask<int, DlLiteLoaderListResult> loader)
    {
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlLiteLoaderListResult>, int>>
                    {
                        new(dlLiteLoaderListBmclapiLoader, 30), new(dlLiteLoaderListOfficialLoader, 30 + 60)
                    }, loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlLiteLoaderListResult>, int>>
                    {
                        new(dlLiteLoaderListOfficialLoader, 5), new(dlLiteLoaderListBmclapiLoader, 5 + 30)
                    }, loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlLiteLoaderListResult>, int>>
                    {
                        new(dlLiteLoaderListOfficialLoader, 60), new(dlLiteLoaderListBmclapiLoader, 60 + 60)
                    }, loader.isForceRestarting);
                break;
            }
        }
    }


    private static void DlLiteLoaderListOfficialMain(ModLoader.LoaderTask<int, DlLiteLoaderListResult> loader)
    {
        var result =
            (JsonObject)Requester.FetchJson("https://dl.liteloader.com/versions/versions.json");
        try
        {
            var json = (JsonObject)result["versions"];
            var versions = new List<DlLiteLoaderListEntry>();
            foreach (var Pair in json)
            {
                if (Pair.Key.StartsWithF("1.6") || Pair.Key.StartsWithF("1.5"))
                    continue;
                var realEntry =
                    (Pair.Value["artefacts"] ?? Pair.Value["snapshots"])["com.mumfrey:liteloader"]["latest"];
                versions.Add(new DlLiteLoaderListEntry
                {
                    Inherit = Pair.Key,
                    IsLegacy = double.Parse(Pair.Key.Split(".")[1]) < 8d,
                    IsPreview = realEntry["stream"].ToString().ToLower() == "snapshot",
                    FileName = "liteloader-installer-" + Pair.Key +
                               (Pair.Key == "1.8" || Pair.Key == "1.9" ? ".0" : "") + "-00-SNAPSHOT.jar",
                    MD5 = (string)realEntry["md5"],
                    ReleaseTime = TimeUtils.FormatUnixTimestamp(long.Parse(realEntry["timestamp"].ToString())),
                    jsonToken = realEntry
                });
            }

            loader.output = new DlLiteLoaderListResult
                { isOfficial = true, sourceName = Lang.Text("Download.Source.LiteLoaderOfficial"), Value = versions };
        }
        catch (Exception ex)
        {
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "LiteLoader", result),
                ex);
        }
    }


    private static void DlLiteLoaderListBmclapiMain(ModLoader.LoaderTask<int, DlLiteLoaderListResult> loader)
    {
        var result =
            (JsonObject)Requester.FetchJson(
                "https://bmclapi2.bangbang93.com/maven/com/mumfrey/liteloader/versions.json");
        try
        {
            var json = (JsonObject)result["versions"];
            var versions = new List<DlLiteLoaderListEntry>();
            foreach (var Pair in json)
            {
                if (Pair.Key.StartsWithF("1.6") || Pair.Key.StartsWithF("1.5"))
                    continue;
                var realEntry =
                    (Pair.Value["artefacts"] ?? Pair.Value["snapshots"])["com.mumfrey:liteloader"]["latest"];
                versions.Add(new DlLiteLoaderListEntry
                {
                    Inherit = Pair.Key,
                    IsLegacy = double.Parse(Pair.Key.Split(".")[1]) < 8d,
                    IsPreview = realEntry["stream"].ToString().ToLower() == "snapshot",
                    FileName = "liteloader-installer-" + Pair.Key +
                               (Pair.Key == "1.8" || Pair.Key == "1.9" ? ".0" : "") + "-00-SNAPSHOT.jar",
                    MD5 = (string)realEntry["md5"],
                    ReleaseTime = TimeUtils.FormatUnixTimestamp(long.Parse((string)realEntry["timestamp"])),
                    jsonToken = realEntry
                });
            }

            loader.output = new DlLiteLoaderListResult { isOfficial = false, sourceName = "BMCLAPI", Value = versions };
        }
        catch (Exception ex)
        {
            throw new Exception(
                Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "LiteLoader BMCLAPI", result), ex);
        }
    }

}
