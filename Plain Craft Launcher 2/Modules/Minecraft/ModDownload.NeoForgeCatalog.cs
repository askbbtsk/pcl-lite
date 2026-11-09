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



    public struct DlNeoForgeListResult
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
        public List<DlNeoForgeListEntry> Value;
    }


    public class DlNeoForgeListEntry : DlForgelikeEntry
    {
        /// <summary>
        ///     API 使用的原始版本字符串，如 “20.4.30-beta”、“1.20.1-47.1.99”（Legacy）。
        /// </summary>
        public string ApiName;

        /// <summary>
        ///     是否是 Beta 版。
        /// </summary>
        public bool IsBeta;

        public DlNeoForgeListEntry(string apiName)
        {
            forgeType = ForgelikeType.NeoForge;
            this.ApiName = apiName;
            IsBeta = apiName.Contains("beta") || apiName.Contains("alpha");
            if (apiName.Contains("1.20.1")) // 1.20.1-47.1.99
            {
                VersionName = apiName.Replace("1.20.1-", "");
                version = new Version("19." + VersionName);
                Inherit = "1.20.1";
            }
            else if (apiName.StartsWith("0.")) // 0.25w14craftmine.3-beta
            {
                VersionName = apiName;
                var segments = apiName.BeforeFirst("-").Split('.');
                version = new Version(0, 0, int.Parse(segments.Last()));
                Inherit = segments[1];
            }
            else // 20.4.30-beta；26.1.0.0-alpha.1+snapshot-1
            {
                VersionName = apiName;
                version = new Version(apiName.BeforeFirst("-"));
                if (version.Major >= 24)
                    Inherit = $"{version.Major}.{version.Minor}{(version.Build > 0 ? $".{version.Build}" : "")}";
                else
                    Inherit = "1." + version.Major + (version.Minor > 0 ? "." + version.Minor : "");
                if (VersionName.Contains("+"))
                    Inherit += "-" + VersionName.AfterFirst("+");
            }
        }

        /// <summary>
        ///     文件在官网的基础地址，不包含后缀。
        /// </summary>
        public string UrlBase
        {
            get
            {
                var packageName = IsLegacy ? "forge" : "neoforge";
                return
                    $"https://maven.neoforged.net/releases/net/neoforged/{packageName}/{ApiName}/{packageName}-{ApiName}";
            }
        }
    }


    private static void DlNeoForgeListMain(ModLoader.LoaderTask<int, DlNeoForgeListResult> loader)
    {
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlNeoForgeListResult>, int>>
                        { new(dlNeoForgeListBmclapiLoader, 30), new(dlNeoForgeListOfficialLoader, 30 + 60) },
                    loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlNeoForgeListResult>, int>>
                        { new(dlNeoForgeListOfficialLoader, 5), new(dlNeoForgeListBmclapiLoader, 5 + 30) },
                    loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlNeoForgeListResult>, int>>
                        { new(dlNeoForgeListOfficialLoader, 60), new(dlNeoForgeListBmclapiLoader, 60 + 60) },
                    loader.isForceRestarting);
                break;
            }
        }
    }


    private static void DlNeoForgeListOfficialMain(ModLoader.LoaderTask<int, DlNeoForgeListResult> loader)
    {
        // 获取版本列表 JSON
        var resultLatest = Requester.FetchJson(
            "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge",
            new RequestParam
            {
                UseBrowserUserAgent = true
            }).ToString();
        var resultLegacy = Requester.FetchJson(
            "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/forge",
            new RequestParam
            {
                UseBrowserUserAgent = true
            }).ToString();
        if (resultLatest.Length < 100 || resultLegacy.Length < 100)
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "NeoForge",
                resultLatest + "\r\n\r\n" + resultLegacy));
        // 解析
        try
        {
            loader.output = new DlNeoForgeListResult
            {
                isOfficial = true,
                sourceName = Lang.Text("Download.Source.NeoForgeOfficial"),
                Value = GetNeoForgeEntries(resultLatest, resultLegacy)
            };
        }
        catch (Exception ex)
        {
            throw new Exception(
                Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "NeoForge",
                    resultLatest + "\r\n\r\n" + resultLegacy), ex);
        }
    }


    public static void DlNeoForgeListBmclapiMain(ModLoader.LoaderTask<int, DlNeoForgeListResult> loader)
    {
        // 获取版本列表 JSON
        var resultLatest = Requester.FetchJson(
            "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/neoforge",
            new RequestParam
            {
                UseBrowserUserAgent = true
            }).ToString();
        var resultLegacy = Requester.FetchJson(
            "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/forge",
            new RequestParam
            {
                UseBrowserUserAgent = true
            }).ToString();
        if (resultLatest.Length < 100 || resultLegacy.Length < 100)
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "NeoForge BMCLAPI",
                resultLatest + "\r\n\r\n" + resultLegacy));
        // 解析
        try
        {
            loader.output = new DlNeoForgeListResult
            {
                isOfficial = true,
                sourceName = "BMCLAPI",
                Value = GetNeoForgeEntries(resultLatest, resultLegacy)
            };
        }
        catch (Exception ex)
        {
            throw new Exception(
                Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "NeoForge BMCLAPI",
                    resultLatest + "\r\n\r\n" + resultLegacy),
                ex);
        }
    }


    private static List<DlNeoForgeListEntry> GetNeoForgeEntries(string latestJson, string latestLegacyJson)
    {
        var versionNames = ModBase.RegexSearch(latestLegacyJson + latestJson, RegexPatterns.DlNeoForgeVersion);
        var versions = versionNames.Where(name => name != "47.1.82").Select(name => new DlNeoForgeListEntry(name))
            .OrderByDescending(a => a).ToList(); // 这个版本虽然在版本列表中，但不能下载
        if (!versions.Any())
            throw new Exception(Lang.Text("Minecraft.Download.Error.NotFound"));
        return versions;
    }

}
