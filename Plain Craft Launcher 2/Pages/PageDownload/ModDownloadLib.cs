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
    ///     如果 OptiFine 与 Forge 同时开始安装，就会导致 Forge 安装失败。
    /// </summary>
    private static readonly object installSyncLock = new();

    /// <summary>
    ///     如果 OptiFine 与 Forge 同时复制原版 Jar，就会导致复制文件时冲突。
    /// </summary>
    private static readonly object vanillaSyncLock = new();

    /// <summary>
    ///     将远程元数据提供的名称作为单个目录名拼接到缓存目录下，并阻止路径穿越。
    /// </summary>
    private static string CombineCacheSubfolder(string parentFolder, string childFolderName)
    {
        if (string.IsNullOrWhiteSpace(childFolderName) || childFolderName is "." or ".." ||
            childFolderName.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0 ||
            Path.IsPathRooted(childFolderName))
            CancelUnsafeCacheSubfolder(childFolderName, "包含非法路径字符");

        var parentFullPath = Path.GetFullPath(parentFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var combinedFullPath = Path.GetFullPath(Path.Combine(parentFullPath, childFolderName));
        if (!combinedFullPath.StartsWith(parentFullPath, StringComparison.OrdinalIgnoreCase))
            CancelUnsafeCacheSubfolder(childFolderName, "导致缓存路径越界");
        return combinedFullPath;
    }

    private static void CancelUnsafeCacheSubfolder(string childFolderName, string reason)
    {
        var message = "远程版本名" + reason + "：" + childFolderName;
        ModBase.Log("[Download] " + message);
        HintService.Hint(message, HintType.Error);
        throw new ModBase.CancelledException();
    }

    private static readonly string mcDownloadClientLibName = Lang.Text("Minecraft.Download.Stage.VanillaLibrariesDownload");
    private static readonly string mcDownloadClientJsonName = Lang.Text("Minecraft.Download.Stage.VanillaJsonDownload");

    private static bool isForgeRecommendedRefreshed;

    private const string mcInstallDefaultType = "安装";

}
