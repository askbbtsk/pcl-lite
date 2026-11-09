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

    private static readonly object _allDropsLock = new();

    /// <summary>
    ///     Minecraft 客户端 版本列表，主加载器。
    ///     若要求镜像源必须包含某个版本，则将该版本 ID 作为输入（#5195）。
    /// </summary>
    public static ModLoader.LoaderTask<string, DlClientListResult> dlClientListLoader =
        new("DlClientList Main", DlClientListMain);

    // 各个下载源的分加载器
    /// <summary>
    ///     Minecraft 客户端 版本列表，Mojang 官方源加载器。
    /// </summary>
    public static ModLoader.LoaderTask<string, DlClientListResult> dlClientListMojangLoader =
        new("DlClientList Mojang", DlClientListMojangMain);


    // MC 更新提示
    private static bool _DlClientListMojangMain_IsHinted;

    /// <summary>
    ///     Minecraft 客户端 版本列表，BMCLAPI 源加载器。
    /// </summary>
    public static ModLoader.LoaderTask<string, DlClientListResult> dlClientListBmclapiLoader =
        new("DlClientList Bmclapi", DlClientListBmclapiMain);

    /// <summary>
    ///     OptiFine 版本列表，主加载器。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlOptiFineListResult> dlOptiFineListLoader =
        new("DlOptiFineList Main", DlOptiFineListMain);

    /// <summary>
    ///     OptiFine 版本列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlOptiFineListResult> dlOptiFineListOfficialLoader =
        new("DlOptiFineList Official", DlOptiFineListOfficialMain);

    /// <summary>
    ///     OptiFine 版本列表，BMCLAPI。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlOptiFineListResult> dlOptiFineListBmclapiLoader =
        new("DlOptiFineList Bmclapi", DlOptiFineListBmclapiMain);

    /// <summary>
    ///     Forge 版本列表，主加载器。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlForgeListResult> dlForgeListLoader =
        new("DlForgeList Main", DlForgeListMain);

    /// <summary>
    ///     Forge 版本列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlForgeListResult> dlForgeListOfficialLoader =
        new("DlForgeList Official", DlForgeListOfficialMain);

    /// <summary>
    ///     Forge 版本列表，BMCLAPI。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlForgeListResult> dlForgeListBmclapiLoader =
        new("DlForgeList Bmclapi", DlForgeListBmclapiMain);

    /// <summary>
    ///     NeoForge 版本列表，主加载器。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlNeoForgeListResult> dlNeoForgeListLoader =
        new("DlNeoForgeList Main", DlNeoForgeListMain);

    /// <summary>
    ///     NeoForge 版本列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlNeoForgeListResult> dlNeoForgeListOfficialLoader =
        new("DlNeoForgeList Official", DlNeoForgeListOfficialMain);

    /// <summary>
    ///     NeoForge 版本列表，BMCLAPI。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlNeoForgeListResult> dlNeoForgeListBmclapiLoader =
        new("DlNeoForgeList Bmclapi", DlNeoForgeListBmclapiMain);

    /// <summary>
    ///     Cleanroom 版本列表，主加载器。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlCleanroomListResult> dlCleanroomListLoader =
        new("DlCleanroomList Main", DlCleanroomListMain);

    /// <summary>
    ///     Cleanroom 版本列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlCleanroomListResult> dlCleanroomListOfficialLoader =
        new("DlCleanroomList Official", DlCleanroomListOfficialMain);

    /// <summary>
    ///     LiteLoader 版本列表，主加载器。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlLiteLoaderListResult> dlLiteLoaderListLoader =
        new("DlLiteLoaderList Main", DlLiteLoaderListMain);

    /// <summary>
    ///     LiteLoader 版本列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlLiteLoaderListResult> dlLiteLoaderListOfficialLoader =
        new("DlLiteLoaderList Official", DlLiteLoaderListOfficialMain);

    /// <summary>
    ///     LiteLoader 版本列表，BMCLAPI。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlLiteLoaderListResult> dlLiteLoaderListBmclapiLoader =
        new("DlLiteLoaderList Bmclapi", DlLiteLoaderListBmclapiMain);

    /// <summary>
    ///     Fabric 列表，主加载器。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlFabricListResult> dlFabricListLoader =
        new("DlFabricList Main", DlFabricListMain);

    /// <summary>
    ///     Fabric 列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlFabricListResult> dlFabricListOfficialLoader =
        new("DlFabricList Official", DlFabricListOfficialMain);

    /// <summary>
    ///     Fabric 列表，BMCLAPI。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlFabricListResult> dlFabricListBmclapiLoader =
        new("DlFabricList Bmclapi", DlFabricListBmclapiMain);

    /// <summary>
    ///     Fabric API 列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, List<ModComp.CompFile>> dlFabricApiLoader = new("Fabric API List Loader",
        task => task.output = ModComp.CompFilesGet("fabric-api", false));

    /// <summary>
    ///     OptiFabric 列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, List<ModComp.CompFile>> dlOptiFabricLoader =
        new("OptiFabric List Loader", task => task.output = ModComp.CompFilesGet("322385", true));

    /// <summary>
    ///     LabyMod 列表，主加载器。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlLabyModListResult> dlLabyModListLoader =
        new("DlLabyModList Main", DlLabyModListMain);

    /// <summary>
    ///     LabyMod 列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlLabyModListResult> dlLabyModListOfficialLoader =
        new("DlLabyModList Official", DlLabyModListOfficialMain);



    public struct DlLegacyFabricListResult
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
        public JsonObject Value;
    }

    /// <summary>
    ///     LegacyFabric 列表，主加载器。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlLegacyFabricListResult> dlLegacyFabricListLoader =
        new("DlLegacyFabricList Main", DlLegacyFabricListMain);

    /// <summary>
    ///     LegacyFabric 列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, DlLegacyFabricListResult> dlLegacyFabricListOfficialLoader =
        new("DlLegacyFabricList Official", DlLegacyFabricListOfficialMain);

    /// <summary>
    ///     Legacy Fabric API 列表，官方源。
    /// </summary>
    public static ModLoader.LoaderTask<int, List<ModComp.CompFile>> dlLegacyFabricApiLoader =
        new("Legacy Fabric API List Loader", task => task.output = ModComp.CompFilesGet("legacy-fabric-api", false));
}
