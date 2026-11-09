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
    ///     安装请求。
    /// </summary>
    public class McInstallRequest
    {
        /// <summary>
        ///     欲下载的 Cleanroom。
        /// </summary>
        public ModDownload.DlCleanroomListEntry cleanroomEntry = null;

        // 若要下载 Cleanroom，则需要在下面两项中完成至少一项
        /// <summary>
        ///     欲下载的 Cleanroom 版本名。
        /// </summary>
        public string cleanroomVersion;

        /// <summary>
        ///     欲下载的 Fabric API 信息。
        /// </summary>
        public ModComp.CompFile fabricApi = null;

        /// <summary>
        ///     欲下载的 Fabric Loader 版本名。
        /// </summary>
        public string fabricVersion = null;

        /// <summary>
        ///     欲下载的 Forge。
        /// </summary>
        public ModDownload.DlForgeVersionEntry forgeEntry = null;

        // 若要下载 Forge，则需要在下面两项中完成至少一项
        /// <summary>
        ///     欲下载的 Forge 版本名。接受例如 36.1.4 / 14.23.5.2859 / 1.19-41.1.0 的输入。
        /// </summary>
        public string forgeVersion;

        /// <summary>
        ///     欲下载的 LabyMod 通道。
        /// </summary>
        public string labyModChannel = null;

        /// <summary>
        ///     欲下载的 LabyMod 版本。
        /// </summary>
        public string labyModCommitRef = null;

        /// <summary>
        ///     欲下载的 Legacy Fabric API 信息。
        /// </summary>
        public ModComp.CompFile legacyFabricApi = null;

        /// <summary>
        ///     欲下载的 Legacy Fabric Loader 版本名。
        /// </summary>
        public string legacyFabricVersion = null;

        /// <summary>
        ///     欲下载的 LiteLoader 详细信息。
        /// </summary>
        public ModDownload.DlLiteLoaderListEntry liteLoaderEntry = null;

        /// <summary>
        ///     可选。欲下载的 Minecraft Json 地址。
        /// </summary>
        public string minecraftJson = null;

        /// <summary>
        ///     必填。欲下载的 Minecraft 的版本名。
        /// </summary>
        public string minecraftName = null;

        /// <summary>
        ///     若 MMC 整合包安装包含特殊参数，则填写此项。
        /// </summary>
        public ModModpack.MMCPackInfo mmcPackInfo = null;

        /// <summary>
        ///     欲下载的 NeoForge。
        /// </summary>
        public ModDownload.DlNeoForgeListEntry neoForgeEntry = null;

        // 若要下载 NeoForge，则需要在下面两项中完成至少一项
        /// <summary>
        ///     欲下载的 NeoForge 版本名。
        /// </summary>
        public string neoForgeVersion;

        /// <summary>
        ///     欲下载的 OptiFabric 信息。
        /// </summary>
        public ModComp.CompFile optiFabric = null;

        /// <summary>
        ///     欲下载的 OptiFine 详细信息。
        /// </summary>
        public ModDownload.DlOptiFineListEntry optiFineEntry;

        // 若要下载 OptiFine，则需要在下面两项中完成至少一项
        /// <summary>
        ///     欲下载的 OptiFine 版本名。例如 HD_U_F6_pre1。
        /// </summary>
        public string optiFineVersion;

        /// <summary>
        ///     必填。安装目标文件夹。
        /// </summary>
        public string targetInstanceFolder;

        /// <summary>
        ///     必填。安装目标实例名称。
        /// </summary>
        public string targetInstanceName;
    }


    /// <summary>
    ///     在加载器状态改变后显示一条提示。
    ///     不会进行任何其他操作。
    /// </summary>
    public static void LoaderStateChangedHintOnly(object loaderObj)
    {
        var loader = (ModLoader.LoaderBase)loaderObj;
        switch (loader.State)
        {
            case ModBase.LoadState.Finished:
                HintService.Hint($"{loader.name}{Lang.Text("Common.Status.Success")}", HintType.Success);
                break;
            case ModBase.LoadState.Failed:
                HintService.Hint($"{loader.name}{Lang.Text("Common.Status.Failure")}{loader.Error.Message}", HintType.Error);
                break;
            case ModBase.LoadState.Aborted:
                HintService.Hint($"{loader.name}{Lang.Text("Common.Status.Cancelled")}");
                break;
        }
    }


    /// <summary>
    ///     安装加载器状态改变后进行提示和重载文件夹列表的方法。
    /// </summary>
    public static void McInstallState(object loaderObj)
    {
        var loader = (ModLoader.LoaderBase)loaderObj;
        var combo = (ModLoader.LoaderCombo)loader;
        switch (loader.State)
        {
            case ModBase.LoadState.Finished:
            {
                if (Config.Download.AutoSelectInstance)
                {
                    var versionName = loader.name;
                    ModBase.WriteIni(ModFolder.mcFolderSelected + "PCL.ini", "Version",
                        versionName.Remove(versionName.Length - 3, 3));
                }

                ModBase.WriteIni(ModFolder.mcFolderSelected + "PCL.ini", "InstanceCache",
                    ""); // 清空缓存（合并安装会先生成文件夹，这会在刷新时误判为可以使用缓存）
                ModBase.DeleteDirectory($"{combo.input}PCLInstallBackups\\");
                HintService.Hint($"{loader.name}{Lang.Text("Common.Status.Success")}",
                    HintType.Success);
                break;
            }
            case ModBase.LoadState.Failed:
            {
                HintService.Hint(
                    $"{loader.name}{Lang.Text("Common.Status.Failure")}{loader.Error.Message}",
                    HintType.Error);
                break;
            }
            case ModBase.LoadState.Aborted:
            {
                HintService.Hint($"{loader.name}{Lang.Text("Common.Status.Cancelled")}");
                break;
            }
            case ModBase.LoadState.Loading:
            {
                return; // 不重新加载实例列表
            }
        }

        if (loader.State != ModBase.LoadState.Finished &&
                Directory.Exists(
                    $"{combo.input}PCLInstallBackups\\")) // 实例修改失败回滚
        {
            ModBase.CopyDirectory(
                $"{combo.input}PCLInstallBackups\\",
                (string)combo.input);
            File.Delete($"{combo.input}.pclignore");
            ModBase.DeleteDirectory(
                $"{combo.input}PCLInstallBackups\\");
        }
        else
        {
            McInstallFailedClearFolder(loader);
        }

        ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
            ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
    }


    public static void McInstallFailedClearFolder(object loader)
    {
        try
        {
            Thread.Sleep(1000); // 防止存在尚未完全释放的文件，导致清理失败（例如整合包安装）
            if (((ModLoader.LoaderBase)loader).State == ModBase.LoadState.Failed ||
                ((ModLoader.LoaderBase)loader).State == ModBase.LoadState.Aborted)
            {
                // 删除实例文件夹
                ModBase.Log($"[Download] 由于下载失败或取消，清理实例文件夹：{((ModLoader.LoaderCombo)loader).input}", ModBase.LogLevel.Developer);
                var instancePath = (string)((ModLoader.LoaderCombo)loader).input;
                    ((DynamicCacheConfigStorage)ConfigService.GetProvider(ConfigSource.GameInstance)).InvalidateCache(instancePath);
                    ModBase.DeleteDirectory(instancePath);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "下载失败或取消后清理实例文件夹失败");
        }
    }

}
