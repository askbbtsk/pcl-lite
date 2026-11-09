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

    /// <summary>
    ///     将多个实例 JSON 进行合并，如果目标已存在则直接覆盖。失败会抛出异常。
    /// </summary>
    private static void MergeJson(string outputFolder, string minecraftFolder, string optiFineFolder = null,
        bool optiFineAsMod = false, string forgeFolder = null, string forgeVersion = null, string neoForgeFolder = null,
        string neoForgeVersion = null, string cleanroomFolder = null, string cleanroomVersion = null,
        string fabricFolder = null, string labyModFolder = null,
        string labyModChannel = null, string liteLoaderFolder = null, ModModpack.MMCPackInfo mMCPackInfo = null,
        string legacyFabricFolder = null)
=>VersionJsonMerger.MergeJson(outputFolder, minecraftFolder, optiFineFolder, optiFineAsMod, forgeFolder, forgeVersion, neoForgeFolder, neoForgeVersion, cleanroomFolder, cleanroomVersion, fabricFolder, labyModFolder, labyModChannel, liteLoaderFolder, mMCPackInfo, legacyFabricFolder);


    /// <summary>
    ///     判断支持库是否为 LWJGL 3.x（Maven 坐标形如 org.lwjgl:lwjgl:3.x.y）。
    /// </summary>
    private static bool IsLwjgl3Library(JsonNode library)
=>VersionJsonMerger.IsLwjgl3Library(library);


    /// <summary>
    ///     判断支持库是否为与 LWJGL 3 冲突的 LWJGL 2.x。
    /// </summary>
    private static bool IsLwjgl2Library(JsonNode library)
=>VersionJsonMerger.IsLwjgl2Library(library);

}
