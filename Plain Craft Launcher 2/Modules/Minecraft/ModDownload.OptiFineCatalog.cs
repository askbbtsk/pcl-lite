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



    public struct DlOptiFineListResult
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
        public List<DlOptiFineListEntry> Value;
    }


    public class DlOptiFineListEntry
    {
        /// <summary>
        ///     显示名称，已去除 HD_U 字样，如“1.12.2 C8”。
        /// </summary>
        public string DisplayName;

        /// <summary>
        ///     是否为测试版。
        /// </summary>
        public bool IsPreview;

        /// <summary>
        ///     原始文件名称，如“preview_OptiFine_1.11_HD_U_E1_pre.jar”。
        /// </summary>
        public string NameFile;

        /// <summary>
        ///     对应的版本名称，如“1.13.2-OptiFine_HD_U_E6”。
        /// </summary>
        public string NameVersion;

        /// <summary>
        ///     发布时间，格式为“yyyy/mm/dd”。OptiFine 源无此数据。
        /// </summary>
        public string ReleaseTime;

        /// <summary>
        ///     需要的最低 Forge 版本。空字符串为无限制，Nothing 为不兼容，“28.1.56” 表示版本号，“1161” 表示版本号的最后一位。
        /// </summary>
        public string RequiredForgeVersion;

        /// <summary>
        ///     对应的 Minecraft 版本，如“1.12.2”。
        /// </summary>
        public string Inherit
        {
            get => field;
            set
            {
                if (value.EndsWithF(".0"))
                    value = value.Substring(0, value.Length - 2);
                field = value;
            }
        }
    }


    private static void DlOptiFineListMain(ModLoader.LoaderTask<int, DlOptiFineListResult> loader)
    {
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlOptiFineListResult>, int>>
                        { new(dlOptiFineListBmclapiLoader, 30), new(dlOptiFineListOfficialLoader, 30 + 60) },
                    loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlOptiFineListResult>, int>>
                        { new(dlOptiFineListOfficialLoader, 5), new(dlOptiFineListBmclapiLoader, 5 + 30) },
                    loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlOptiFineListResult>, int>>
                        { new(dlOptiFineListOfficialLoader, 60), new(dlOptiFineListBmclapiLoader, 60 + 60) },
                    loader.isForceRestarting);
                break;
            }
        }
    }


    private static void DlOptiFineListOfficialMain(ModLoader.LoaderTask<int, DlOptiFineListResult> loader)
    {
        string result = "";
        using var resp = HttpRequest
            .Create("https://optifine.net/downloads")
            .WithHeader("Accept", "application/json, text/javascript, */*; q=0.01")
            .WithHeader("Accept-Language", "en-US,en;q=0.5")
            .WithHeader("X-Requested-With", "XMLHttpRequest")
            .SendAsync()
            .GetAwaiter()
            .GetResult();
        resp.EnsureSuccessStatusCode();
        result = resp.AsString();
        if (result.Length < 200)
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "OptiFine", result));
        try
        {
            var forge = result.RegexSearch("(?<=colForge'>)[^<]*");
            var releaseTime = result.RegexSearch("(?<=colDate'>)[^<]+");
            var name = result.RegexSearch("(?<=OptiFine_)[0-9A-Za-z_.]+(?=.jar\")");
            if (releaseTime.Count != name.Count)
                throw new Exception(Lang.Text("Minecraft.Download.Error.OptiFineTimeDataMismatch"));
            if (forge.Count != name.Count)
                throw new Exception(Lang.Text("Minecraft.Download.Error.OptiFineForgeCompatMismatch"));
            if (releaseTime.Count < 10)
                throw new Exception(
                    Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "OptiFine", result));
            // 转化为列表输出
            var versions = new List<DlOptiFineListEntry>();
            for (int i = 0, loopTo = releaseTime.Count - 1; i <= loopTo; i++)
            {
                name[i] = name[i].Replace("_", " ");
                var releaseDate = DateTime.ParseExact(releaseTime[i],
                    ["d.M.yyyy", "dd.M.yyyy", "d.MM.yyyy", "dd.MM.yyyy"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None);
                var entry = new DlOptiFineListEntry
                {
                    DisplayName = name[i].Replace("HD U ", "").Replace(".0 ", " "),
                    ReleaseTime = Lang.Date(releaseDate, "d"),
                    IsPreview = name[i].ContainsF("pre", true),
                    Inherit = name[i].Split(" ")[0],
                    NameFile = (name[i].ContainsF("pre", true) ? "preview_" : "") + "OptiFine_" +
                               name[i].Replace(" ", "_") + ".jar",
                    RequiredForgeVersion = forge[i].Replace("Forge ", "").Replace("#", "")
                };
                if (entry.RequiredForgeVersion.Contains("N/A"))
                    entry.RequiredForgeVersion = null;
                entry.NameVersion = entry.Inherit + "-OptiFine_" +
                                    name[i].Replace(" ", "_").Replace(entry.Inherit + "_", "");
                versions.Add(entry);
            }

            loader.output = new DlOptiFineListResult
                { isOfficial = true, sourceName = Lang.Text("Download.Source.OptiFineOfficial"), Value = versions };
        }
        catch (Exception ex)
        {
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "OptiFine", result),
                ex);
        }
    }


    private static void DlOptiFineListBmclapiMain(ModLoader.LoaderTask<int, DlOptiFineListResult> loader)
    {
        var json = (JsonArray)Requester.FetchJson("https://bmclapi2.bangbang93.com/optifine/versionList");
        try
        {
            var versions = new List<DlOptiFineListEntry>();
            foreach (JsonObject Token in json)
            {
                var entry = new DlOptiFineListEntry
                {
                    DisplayName =
                        (Token["mcversion"] + Token["type"].ToString().Replace("HD_U", "").Replace("_", " ") + " " +
                         Token["patch"]).Replace(".0 ", " "),
                    ReleaseTime = "",
                    IsPreview = Token["patch"].ToString().ContainsF("pre", true),
                    Inherit = Token["mcversion"].ToString(),
                    NameFile = Token["filename"].ToString(),
                    RequiredForgeVersion = (Token["forge"] ?? "").ToString().Replace("Forge ", "").Replace("#", "")
                };
                if (entry.RequiredForgeVersion.Contains("N/A"))
                    entry.RequiredForgeVersion = null;
                entry.NameVersion = entry.Inherit + "-OptiFine_" + (Token["type"] + " " + Token["patch"])
                    .Replace(".0 ", " ").Replace(" ", "_").Replace(entry.Inherit + "_", "");
                versions.Add(entry);
            }

            loader.output = new DlOptiFineListResult { isOfficial = false, sourceName = "BMCLAPI", Value = versions };
        }
        catch (Exception ex)
        {
            throw new Exception(
                Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "OptiFine BMCLAPI", json), ex);
        }
    }

}
