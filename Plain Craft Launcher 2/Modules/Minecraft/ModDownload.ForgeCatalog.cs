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



    public struct DlForgeListResult
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
        public List<string> Value;
    }


    private static void DlForgeListMain(ModLoader.LoaderTask<int, DlForgeListResult> loader)
    {
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlForgeListResult>, int>>
                        { new(dlForgeListBmclapiLoader, 30), new(dlForgeListOfficialLoader, 30 + 60) },
                    loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlForgeListResult>, int>>
                        { new(dlForgeListOfficialLoader, 5), new(dlForgeListBmclapiLoader, 5 + 30) },
                    loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<int, DlForgeListResult>, int>>
                        { new(dlForgeListOfficialLoader, 60), new(dlForgeListBmclapiLoader, 60 + 60) },
                    loader.isForceRestarting);
                break;
            }
        }
    }


    private static void DlForgeListOfficialMain(ModLoader.LoaderTask<int, DlForgeListResult> loader)
    {
        var result = Requester.FetchString(
            "https://files.minecraftforge.net/maven/net/minecraftforge/forge/index_1.2.4.html", new RequestParam
            {
                Encoding = Encoding.Default,
                UseBrowserUserAgent = true
            });
        if (result.Length < 200)
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Forge", result));
        // 获取所有版本信息
        var names = result.RegexSearch("(?<=a href=\"index_)[0-9.]+(_pre[0-9]?)?(?=.html)");
        names.Add("1.2.4"); // 1.2.4 不会被匹配上
        if (names.Count < 10)
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Forge", result));
        loader.output = new DlForgeListResult
            { isOfficial = true, sourceName = Lang.Text("Download.Source.ForgeOfficial"), Value = names };
    }


    private static void DlForgeListBmclapiMain(ModLoader.LoaderTask<int, DlForgeListResult> loader)
    {
        var result =
            Requester.FetchJson("https://bmclapi2.bangbang93.com/forge/minecraft",
                new RequestParam
                {
                    Encoding = Encoding.Default,
                })?.ToString() ?? "";
        if (result.Length < 200)
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Forge BMCLAPI",
                result));
        // 获取所有版本信息
        var names = result.RegexSearch("[0-9.]+(_pre[0-9]?)?");
        if (names.Count < 10)
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Forge BMCLAPI",
                result));
        loader.output = new DlForgeListResult { isOfficial = false, sourceName = "BMCLAPI", Value = names };
    }




    public abstract class DlForgelikeEntry : IComparable<DlForgelikeEntry>
    {
        public enum ForgelikeType
        {
            Forge,
            NeoForge,
            Cleanroom
        }

        /// <summary>
        ///     Forgelike 种类。Forge、NeoForge、Cleanroom。
        /// </summary>
        public ForgelikeType forgeType;

        /// <summary>
        ///     对应的 Minecraft 版本，如“1.12.2”。
        /// </summary>
        public string Inherit;

        /// <summary>
        ///     标准化后的版本号，仅可用于比较与排序。
        ///     格式：Major.Minor.Build.Revision
        ///     Forge：如 “50.1.9.0”（最后一位固定为 0）、“14.22.1.2478”（Legacy）。
        ///     NeoForge：如 “20.4.30.0”（最后一位固定为 0）、“19.47.1.99”（Legacy：第一位固定为 19）。
        ///     Cleanroom：如 “0.2.4.1”（Alpha：最后一位固定为 1）。
        /// </summary>
        public Version version;

        /// <summary>
        ///     可对玩家显示的非格式化版本名。
        ///     Forge：如 “50.1.9”、“14.22.1.2478”（Legacy）。
        ///     NeoForge：如 “20.4.30-beta”、“47.1.99”（Legacy）。
        ///     Cleanroom：如 “0.2.4-alpha”。
        /// </summary>
        public string VersionName;

        /// <summary>
        ///     加载器名称。Forge / NeoForge / Cleanroom。
        /// </summary>
        public string LoaderName => forgeType.ToString();

        /// <summary>
        ///     文件扩展名。不以小数点开头。
        /// </summary>
        public string FileExtension
        {
            get
            {
                if (forgeType == 0) return ((DlForgeVersionEntry)this).Category == "installer" ? "jar" : "zip";

                return "jar";
            }
        }

        /// <summary>
        ///     Forge：MC 版本是否小于 1.13。
        ///     NeoForge：MC 版本是否为 1.20.1。
        ///     Cleanroom：固定为 False。
        /// </summary>
        public bool IsLegacy
        {
            get
            {
                // Cleanroom 始终为 False
                if ((int)forgeType == 2)
                    return false;
                // 虽然很抽象，但确实可以这样判断
                // Forge：1.13+ 的版本号首位都大于 20
                // NeoForge：1.20.1 的版本号首位人为规定为 19 开头
                return version.Major < 20;
            }
        }

        public int CompareTo(DlForgelikeEntry other)
        {
            if (version != other.version) return version.CompareTo(other.version);

            return McVersionComparer.CompareVersion(VersionName, other.VersionName);
        }
    }


    public class DlForgeVersionEntry : DlForgelikeEntry
    {
        /// <summary>
        ///     安装类型。有 installer、client、universal 三种。
        /// </summary>
        public string Category;

        /// <summary>
        ///     用于下载的文件版本名。可能在 Version 的基础上添加了分支。
        /// </summary>
        public string FileVersion;

        /// <summary>
        ///     文件的 MD5 或 SHA1（BMCLAPI 的老版本是 MD5，新版本是 SHA1；官方源总是 MD5）。
        /// </summary>
        public string Hash;

        /// <summary>
        ///     是否为推荐版本。
        /// </summary>
        public bool IsRecommended;

        /// <summary>
        ///     发布时间，格式为“yyyy/MM/dd HH:mm”。
        /// </summary>
        public string ReleaseTime;

        public DlForgeVersionEntry(string version, string branch, string inherit)
        {
            // 司马版本的特殊处理
            if (version == "11.15.1.2318" || version == "11.15.1.1902" || version == "11.15.1.1890")
                branch = "1.8.9";
            if (branch is null && inherit == "1.7.10" && double.Parse(version.Split(".")[3]) >= 1300d)
                branch = "1.7.10";
            // 为 DlForgelikeEntry 提供所有信息
            forgeType = ForgelikeType.Forge;
            VersionName = version;
            this.version = new Version(version);
            this.Inherit = inherit;
            FileVersion = version + (branch is null ? "" : "-" + branch);
        }
    }


    /// <summary>
    ///     Forge 版本列表，主加载器。
    /// </summary>
    public static void DlForgeVersionMain(ModLoader.LoaderTask<string, List<DlForgeVersionEntry>> loader)
    {
        var dlForgeVersionOfficialLoader =
            new ModLoader.LoaderTask<string, List<DlForgeVersionEntry>>("DlForgeVersion Official",
                DlForgeVersionOfficialMain);
        var dlForgeVersionBmclapiLoader =
            new ModLoader.LoaderTask<string, List<DlForgeVersionEntry>>("DlForgeVersion Bmclapi",
                DlForgeVersionBmclapiMain);
        switch (Config.Download.VersionListSource)
        {
            case 0:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<string, List<DlForgeVersionEntry>>, int>>
                        { new(dlForgeVersionBmclapiLoader, 30), new(dlForgeVersionOfficialLoader, 30 + 60) },
                    loader.isForceRestarting);
                break;
            }
            case 1:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<string, List<DlForgeVersionEntry>>, int>>
                        { new(dlForgeVersionOfficialLoader, 5), new(dlForgeVersionBmclapiLoader, 5 + 30) },
                    loader.isForceRestarting);
                break;
            }

            default:
            {
                DlSourceLoader(loader,
                    new List<KeyValuePair<ModLoader.LoaderTask<string, List<DlForgeVersionEntry>>, int>>
                        { new(dlForgeVersionOfficialLoader, 60), new(dlForgeVersionBmclapiLoader, 60 + 60) },
                    loader.isForceRestarting);
                break;
            }
        }
    }


    /// <summary>
    ///     Forge 版本列表，官方源。
    /// </summary>
    public static void DlForgeVersionOfficialMain(ModLoader.LoaderTask<string, List<DlForgeVersionEntry>> loader)
    {
        string result;
        try
        {
            result = Requester.FetchString(
                "https://files.minecraftforge.net/maven/net/minecraftforge/forge/index_" +
                loader.input.Replace("-", "_") + ".html", new RequestParam
                {
                    UseBrowserUserAgent = true
                }); // 兼容 Forge 1.7.10-pre4，#4057
        }
        catch (WebException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("(404)")) throw new Exception(Lang.Text("Minecraft.Download.Error.NotFound"));

            throw;
        }

        if (result.Length < 1000)
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Forge", result));
        var versions = new List<DlForgeVersionEntry>();
        try
        {
            // 分割版本信息
            var versionCodes = result.Substring(0, result.LastIndexOfF("</table>"))
                .Split("<td class=\"download-version");
            // 获取所有版本信息
            for (int i = 1, loopTo = versionCodes.Count() - 1; i <= loopTo; i++)
            {
                var versionCode = versionCodes[i];
                try
                {
                    // 基础信息获取
                    var name = versionCode.RegexSeek(@"(?<=[^(0-9)]+)[0-9\.]+");
                    var isRecommended = versionCode.Contains("fa promo-recommended");
                    var inherit = loader.input;
                    // 分支获取
                    var branch = versionCode.RegexSeek($"(?<=-{name}-)[^-\"]+(?=-[a-z]+.[a-z]{{3}})");
                    if (string.IsNullOrWhiteSpace(branch))
                        branch = null;
                    // 发布时间获取
                    var releaseTimeOriginal = versionCode.RegexSeek("(?<=\"download-time\" title=\")[^\"]+");
                    // Dim ReleaseTimeSplit = ReleaseTimeOriginal.Split(" -:".ToCharArray) '原格式："2021-02-15 03:24:02"
                    var releaseDate =
                        DateTime.Parse(releaseTimeOriginal, null, DateTimeStyles.AssumeUniversal); // 以 UTC 时间作为标准
                    var releaseTime = Lang.Date(releaseDate.ToLocalTime(), "g"); // 时区与格式转换
                    // 分类与 MD5 获取
                    string mD5;
                    string category;
                    if (versionCode.Contains("classifier-installer\""))
                    {
                        // 类型为 installer.jar，支持范围 ~753 (~ 1.6.1 部分), 738~684 (1.5.2 全部)
                        versionCode = versionCode.Substring(versionCode.IndexOfF("installer.jar"));
                        mD5 = versionCode.RegexSeek("(?<=MD5:</strong> )[^<]+");
                        category = "installer";
                    }
                    else if (versionCode.Contains("classifier-universal\""))
                    {
                        // 类型为 universal.zip，支持范围 751~449 (1.6.1 部分), 682~183 (1.5.1 ~ 1.3.2 部分)
                        versionCode = versionCode.Substring(versionCode.IndexOfF("universal.zip"));
                        mD5 = versionCode.RegexSeek("(?<=MD5:</strong> )[^<]+");
                        category = "universal";
                    }
                    else if (versionCode.Contains("client.zip"))
                    {
                        // 类型为 client.zip，支持范围 182~ (1.3.2 部分 ~)
                        versionCode = versionCode.Substring(versionCode.IndexOfF("client.zip"));
                        mD5 = versionCode.RegexSeek("(?<=MD5:</strong> )[^<]+");
                        category = "client";
                    }
                    else
                    {
                        // 没有任何下载（1.6.4 有一部分这种情况）
                        continue;
                    }

                    // 添加进列表
                    versions.Add(new DlForgeVersionEntry(name, branch, inherit)
                    {
                        Category = category, IsRecommended = isRecommended,
                        Hash = mD5.Trim('\r', '\n'),
                        ReleaseTime = releaseTime
                    });
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Forge", versionCode), ex);
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Forge", result), ex);
        }

        if (!versions.Any())
            throw new Exception(Lang.Text("Minecraft.Download.Error.NotFound"));
        loader.output = versions;
    }


    /// <summary>
    ///     Forge 版本列表，BMCLAPI。
    /// </summary>
    public static void DlForgeVersionBmclapiMain(ModLoader.LoaderTask<string, List<DlForgeVersionEntry>> loader)
    {
        var json = (JsonArray)Requester.FetchJson(
            "https://bmclapi2.bangbang93.com/forge/minecraft/" +
            loader.input.Replace("-", "_")); // 兼容 Forge 1.7.10-pre4，#4057
        var versions = new List<DlForgeVersionEntry>();
        try
        {
            var recommended = ModDownloadLib.McDownloadForgeRecommendedGet(loader.input);
            foreach (JsonObject Token in json)
            {
                // 分类与 Hash 获取
                string hash = null;
                var category = "unknown";
                var proi = -1;
                foreach (JsonObject File in Token["files"].AsArray())
                    switch (File["category"].ToString() ?? "")
                    {
                        case "installer":
                        {
                            if (File["format"].ToString() == "jar")
                            {
                                // 类型为 installer.jar，支持范围 ~753 (~ 1.6.1 部分), 738~684 (1.5.2 全部)
                                hash = (string)File["hash"];
                                category = "installer";
                                proi = 2;
                            }

                            break;
                        }
                        case "universal":
                        {
                            if (proi <= 1 && File["format"].ToString() == "zip")
                            {
                                // 类型为 universal.zip，支持范围 751~449 (1.6.1 部分), 682~183 (1.5.1 ~ 1.3.2 部分)
                                hash = (string)File["hash"];
                                category = "universal";
                                proi = 1;
                            }

                            break;
                        }
                        case "client":
                        {
                            if (proi <= 0 && File["format"].ToString() == "zip")
                            {
                                // 类型为 client.zip，支持范围 182~ (1.3.2 部分 ~)
                                hash = (string)File["hash"];
                                category = "client";
                                proi = 0;
                            }

                            break;
                        }
                    }

                // 获取 Entry
                var branch = (string)Token["branch"];
                var name = (string)Token["version"];
                // 基础信息获取
                var entry = new DlForgeVersionEntry(name, branch, loader.input)
                    { Hash = hash, Category = category, IsRecommended = (recommended ?? "") == (name ?? "") };
                var timeSplit = Token["modified"].ToString().Split('-', 'T', ':', '.', ' ', '/');
                entry.ReleaseTime = Lang.Date(Token["modified"].ToObject<DateTime>().ToLocalTime(), "g");
                // 添加项
                versions.Add(entry);
            }
        }
        catch (Exception ex)
        {
            throw new Exception(Lang.Text("Minecraft.Download.Error.VersionListOperationFailed", "Forge BMCLAPI", json),
                ex);
        }

        if (!versions.Any())
            throw new Exception(Lang.Text("Minecraft.Download.Error.NotFound"));
        loader.output = versions;
    }

}
