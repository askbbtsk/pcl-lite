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
    ///     进行合并安装。返回是否已经开始安装（例如如果没有安装 Java 则会进行提示并返回 False）
    /// </summary>
    public static bool McInstall(McInstallRequest request, string type = mcInstallDefaultType)
    {
        if (!RestrictedFeaturePolicy.Check("minecraft-install")) return false;
        try
        {
            var subLoaders = McInstallLoader(request, ignoreDump: type != mcInstallDefaultType);
            if (subLoaders is null)
                return false;
            var loader = new ModLoader.LoaderCombo<string>(request.targetInstanceName + " " + type, subLoaders)
                { OnStateChanged = McInstallState };

            // 启动
            loader.Start(request.targetInstanceFolder);
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
            return true;
        }

        catch (ModBase.CancelledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始合并安装失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
            try
            {
                if (Directory.Exists(request.targetInstanceFolder))
                {
                    var files = Directory.GetFiles(request.targetInstanceFolder);
                    var dirs = Directory.GetDirectories(request.targetInstanceFolder);
                    if (files.Length <= 1 && dirs.Length == 0)
                    {
                        ((DynamicCacheConfigStorage)ConfigService.GetProvider(ConfigSource.GameInstance))
                            .InvalidateCache(request.targetInstanceFolder);
                        ModBase.DeleteDirectory(request.targetInstanceFolder);
                    }
                }
            }
            catch (Exception innerEx)
            {
                ModBase.Log(innerEx, "清理未完成的实例文件夹失败");
            }
            return false;
        }
    }


    /// <summary>
    ///     获取合并安装加载器列表，并进行前期的缓存清理与 Java 检查工作。
    /// </summary>
    /// <exception cref="ModBase.CancelledException" />
    public static List<ModLoader.LoaderBase> McInstallLoader(McInstallRequest request, bool dontFixLibraries = false,
        bool ignoreDump = false)
    {
        RestrictedFeaturePolicy.ThrowIfRestricted("minecraft-install");
        // 获取缓存目录（安装 Mod 加载器的文件夹不能包含空格）
        var tempMcFolder = ModMain.RequestTaskTempFolder(request.optiFineEntry is not null ||
                                                         request.forgeEntry is not null ||
                                                         request.neoForgeEntry is not null);

        // 获取参数
        var instanceFolder = string.IsNullOrWhiteSpace(request.targetInstanceFolder)
            ? Path.Combine(ModFolder.mcFolderSelected, "versions", request.targetInstanceName)
            : Path.GetFullPath(request.targetInstanceFolder);
        var targetMinecraftRoot = Directory.GetParent(Path.TrimEndingDirectorySeparator(instanceFolder))?.Parent?.FullName
            ?? ModFolder.mcFolderSelected;
        if (Directory.Exists(tempMcFolder))
            ModBase.DeleteDirectory(tempMcFolder);
        string optiFineFolder = null;
        if (request.optiFineVersion is not null)
        {
            if (request.optiFineVersion.Contains("_HD_U_"))
                request.optiFineVersion = "HD_U_" + request.optiFineVersion.AfterLast("_HD_U_"); // #735
            request.optiFineEntry = new ModDownload.DlOptiFineListEntry
            {
                DisplayName = request.minecraftName + " " + request.optiFineVersion.Replace("HD_U_", "")
                    .Replace("_", "").Replace("pre", " pre"),
                Inherit = request.minecraftName,
                IsPreview = request.optiFineVersion.ContainsF("pre", true),
                NameVersion = request.minecraftName + "-OptiFine_" + request.optiFineVersion,
                NameFile = (request.optiFineVersion.ContainsF("pre", true) ? "preview_" : "") + "OptiFine_" +
                           request.minecraftName + "_" + request.optiFineVersion + ".jar"
            };
        }

        if (request.optiFineEntry is not null)
            optiFineFolder = Path.Combine(tempMcFolder, "versions", request.optiFineEntry.NameVersion);
        string forgeFolder = null;
        if (request.forgeEntry is not null)
            request.forgeVersion = request.forgeVersion ?? request.forgeEntry.VersionName;
        if (request.forgeVersion is not null)
            forgeFolder = Path.Combine(tempMcFolder, "versions", "forge-" + request.forgeVersion);
        string neoForgeFolder = null;
        if (request.neoForgeEntry is not null)
            request.neoForgeVersion = request.neoForgeVersion ?? request.neoForgeEntry.VersionName;
        if (request.neoForgeVersion is not null)
            neoForgeFolder = Path.Combine(tempMcFolder, "versions", "neoforge-" + request.neoForgeVersion);
        string cleanroomFolder = null;
        if (request.cleanroomEntry is not null)
            request.cleanroomVersion = request.cleanroomVersion ?? request.cleanroomEntry.VersionName;
        if (request.cleanroomVersion is not null)
            cleanroomFolder = Path.Combine(tempMcFolder, "versions", "cleanroom-" + request.cleanroomVersion);
        string fabricFolder = null;
        if (request.fabricVersion is not null)
            fabricFolder = Path.Combine(tempMcFolder, "versions", "fabric-loader-" + request.fabricVersion + "-" +
                           request.minecraftName);
        string legacyFabricFolder = null;
        if (request.legacyFabricVersion is not null)
            legacyFabricFolder = Path.Combine(tempMcFolder, "versions", "legacy-fabric-loader-" + request.legacyFabricVersion + "-" +
                                 request.minecraftName);
        string labyModFolder = null;
        if (request.labyModCommitRef is not null)
            labyModFolder = Path.Combine(tempMcFolder, "versions", "labymod-" + request.labyModCommitRef + "-" +
                            request.minecraftName);
        string liteLoaderFolder = null;
        if (request.liteLoaderEntry is not null)
            liteLoaderFolder = Path.Combine(tempMcFolder, "versions", request.minecraftName + "-LiteLoader");

        // 判断 OptiFine 是否作为 Mod 进行下载
        var modable = request.fabricVersion is not null || request.legacyFabricVersion is not null ||
                      request.forgeEntry is not null || request.neoForgeEntry is not null ||
                      request.liteLoaderEntry is not null;
        var modsTempFolder = Path.Combine(tempMcFolder, "mods") + @"\";
        var optiFineAsMod = request.optiFineEntry is not null && modable; // 选择了 OptiFine 与任意 Mod 加载器
        if (optiFineAsMod)
        {
            ModBase.Log("[Download] OptiFine 将作为 Mod 进行下载");
            if (request.liteLoaderEntry is not null)
                optiFineFolder = CombineCacheSubfolder(modsTempFolder, request.minecraftName);
            else
                optiFineFolder = modsTempFolder;
        }

        // 记录日志
        if (optiFineFolder is not null)
            ModBase.Log("[Download] OptiFine 缓存：" + optiFineFolder);
        if (forgeFolder is not null)
            ModBase.Log("[Download] Forge 缓存：" + forgeFolder);
        if (neoForgeFolder is not null)
            ModBase.Log("[Download] NeoForge 缓存：" + neoForgeFolder);
        if (cleanroomFolder is not null)
            ModBase.Log("[Download] Cleanroom 缓存：" + cleanroomFolder);
        if (fabricFolder is not null)
            ModBase.Log("[Download] Fabric 缓存：" + fabricFolder);
        if (legacyFabricFolder is not null)
            ModBase.Log("[Download] LegacyFabric 缓存：" + legacyFabricFolder);
        if (labyModFolder is not null)
            ModBase.Log("[Download] LabyMod 缓存：" + labyModFolder);
        if (liteLoaderFolder is not null)
            ModBase.Log("[Download] LiteLoader 缓存：" + liteLoaderFolder);
        ModBase.Log("[Download] 对应的原版版本：" + request.minecraftName);

        // 重复实例检查
        if (File.Exists(Path.Combine(instanceFolder, request.targetInstanceName + ".json")) && !ignoreDump)
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceAlreadyExists", request.targetInstanceName, ""),
                HintType.Error);
            throw new ModBase.CancelledException();
        }

        var loaderList = new List<ModLoader.LoaderBase>();
        // 添加忽略标识
        loaderList.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Minecraft.Download.Stage.AddIgnoreFlag"),
                _ => ModBase.WriteFile(Path.Combine(instanceFolder, ".pclignore"), "用于临时地在 PCL 的实例列表中屏蔽此实例。"))
            { show = false, block = false });
        // Fabric API
        if (request.fabricApi is not null)
            loaderList.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadFabricApi"),
                    new List<DownloadFile>
                    {
                        request.fabricApi.ToNetFile(modsTempFolder, ModComp.DownloadReason.Dependency, null,
                            ModComp.CompLoaderType.Fabric)
                    })
                { ProgressWeight = 3d, block = false });
        // LegacyFabric API
        if (request.legacyFabricApi is not null)
            loaderList.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLegacyFabricApi"),
                    new List<DownloadFile>
                    {
                        request.legacyFabricApi.ToNetFile(modsTempFolder, ModComp.DownloadReason.Dependency, null,
                            ModComp.CompLoaderType.Fabric)
                    })
                { ProgressWeight = 3d, block = false });
        // OptiFabric
        if (request.optiFabric is not null)
            loaderList.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadOptiFabric"),
                    new List<DownloadFile>
                    {
                        request.optiFabric.ToNetFile(modsTempFolder, ModComp.DownloadReason.Dependency, null,
                            ModComp.CompLoaderType.Fabric)
                    })
                { ProgressWeight = 3d, block = false });
        // LabyMod
        if (request.labyModCommitRef is not null)
        {
            loaderList.Add(new ModLoader.LoaderCombo<string>(
                Lang.Text("Minecraft.Download.Stage.LoaderDownloadCombo", "LabyMod", request.labyModCommitRef),
                McDownloadLabyModLoader(request.labyModCommitRef, request.labyModChannel, request.minecraftName,
                    tempMcFolder, false)) { show = false, ProgressWeight = 10d, block = true });
            goto LabyModSkip;
        }

        // 原版
        var clientLoader = new ModLoader.LoaderCombo<string>(
            Lang.Text(
                "Minecraft.Download.Stage.LoaderDownloadCombo",
                Lang.Text("Minecraft.Version.Vanilla"),
                request.minecraftName
            ),
            McDownloadClientLoader(
                request.minecraftName, request.minecraftJson, request.targetInstanceName,
                targetMcFolder: targetMinecraftRoot
            )
        )
        {
            show = false,
            ProgressWeight = 39d,
            block = request.forgeVersion is null && request.neoForgeVersion is null && request.optiFineEntry is null &&
                    request.fabricVersion is null && request.liteLoaderEntry is null &&
                    request.cleanroomEntry is null && request.legacyFabricVersion is null
        };
        loaderList.Add(clientLoader);
        // OptiFine
        if (request.optiFineEntry is not null)
        {
            if (optiFineAsMod)
                loaderList.Add(new ModLoader.LoaderCombo<string>(
                    Lang.Text("Minecraft.Download.Stage.LoaderDownloadCombo", "OptiFine",
                        request.optiFineEntry.DisplayName),
                    McDownloadOptiFineSaveLoader(request.optiFineEntry,
                        Path.Combine(optiFineFolder, request.optiFineEntry.NameFile)))
                {
                    show = false,
                    ProgressWeight = 16d,
                    block = request.forgeVersion is null && request.neoForgeVersion is null &&
                            request.fabricVersion is null && request.liteLoaderEntry is null
                });
            else
                loaderList.Add(new ModLoader.LoaderCombo<string>(
                    Lang.Text("Minecraft.Download.Stage.LoaderDownloadCombo", "OptiFine",
                        request.optiFineEntry.DisplayName),
                    McDownloadOptiFineLoader(request.optiFineEntry, tempMcFolder, clientLoader,
                        request.targetInstanceFolder, false))
                {
                    show = false,
                    ProgressWeight = 24d,
                    block = request.forgeVersion is null && request.neoForgeVersion is null &&
                            request.fabricVersion is null && request.liteLoaderEntry is null
                });
        }

        // Forge
        if (request.forgeVersion is not null)
            loaderList.Add(new ModLoader.LoaderCombo<string>(
                Lang.Text("Minecraft.Download.Stage.LoaderDownloadCombo", "Forge", request.forgeVersion),
                McDownloadForgelikeLoader(ModDownload.DlForgelikeEntry.ForgelikeType.Forge, request.forgeVersion, "forge-" + request.forgeVersion,
                    request.minecraftName, request.forgeEntry, tempMcFolder, clientLoader,
                    request.targetInstanceFolder))
            {
                show = false, ProgressWeight = 25d,
                block = request.fabricVersion is null && request.liteLoaderEntry is null &&
                        request.neoForgeEntry is null
            });
        // NeoForge
        if (request.neoForgeVersion is not null)
            loaderList.Add(new ModLoader.LoaderCombo<string>(
                Lang.Text("Minecraft.Download.Stage.LoaderDownloadCombo", "NeoForge", request.neoForgeVersion),
                McDownloadForgelikeLoader(ModDownload.DlForgelikeEntry.ForgelikeType.NeoForge, request.neoForgeVersion, "neoforge-" + request.neoForgeVersion,
                    request.minecraftName, request.neoForgeEntry, tempMcFolder, clientLoader,
                    request.targetInstanceFolder))
            {
                show = false, ProgressWeight = 25d,
                block = request.forgeEntry is null && request.fabricVersion is null && request.liteLoaderEntry is null
            });
        // Cleanroom
        if (request.cleanroomVersion is not null)
            loaderList.Add(new ModLoader.LoaderCombo<string>(
                Lang.Text("Minecraft.Download.Stage.LoaderDownloadCombo", "Cleanroom", request.cleanroomVersion),
                McDownloadForgelikeLoader(ModDownload.DlForgelikeEntry.ForgelikeType.Cleanroom, request.cleanroomVersion,
                    "cleanroom-" + request.cleanroomVersion, request.minecraftName, request.cleanroomEntry,
                    tempMcFolder, clientLoader, request.targetInstanceFolder))
            {
                show = false, ProgressWeight = 25d,
                block = request.forgeEntry is null && request.fabricVersion is null && request.liteLoaderEntry is null
            });
        // LiteLoader
        if (request.liteLoaderEntry is not null)
            loaderList.Add(new ModLoader.LoaderCombo<string>(
                Lang.Text("Minecraft.Download.Stage.LoaderDownloadCombo", "LiteLoader", request.minecraftName),
                McDownloadLiteLoaderLoader(request.liteLoaderEntry, tempMcFolder, clientLoader, false))
            {
                show = false,
                ProgressWeight = 1d,
                block = request.fabricVersion is null
            });
        // Fabric
        if (request.fabricVersion is not null)
            loaderList.Add(new ModLoader.LoaderCombo<string>(
                Lang.Text("Minecraft.Download.Stage.LoaderDownloadCombo", "Fabric", request.fabricVersion),
                McDownloadFabricLoader(request.fabricVersion, request.minecraftName, tempMcFolder, false))
            {
                show = false,
                ProgressWeight = 2d,
                block = true
            });
        // LegacyFabric
        if (request.legacyFabricVersion is not null)
            loaderList.Add(new ModLoader.LoaderCombo<string>(
                Lang.Text("Minecraft.Download.Stage.LoaderDownloadCombo", "Legacy Fabric", request.legacyFabricVersion),
                McDownloadLegacyFabricLoader(request.legacyFabricVersion, request.minecraftName, tempMcFolder, false))
            {
                show = false,
                ProgressWeight = 2d,
                block = true
            });

        LabyModSkip: ;

        // 合并安装
        loaderList.Add(new ModLoader.LoaderTask<string, string>(Lang.Text("Minecraft.Download.Stage.InstallGame"),
            task =>
        {
            // 合并 JSON
            MergeJson(instanceFolder, instanceFolder, optiFineFolder, optiFineAsMod, forgeFolder, request.forgeVersion,
                neoForgeFolder, request.neoForgeVersion, cleanroomFolder, request.cleanroomVersion, fabricFolder,
                labyModFolder, request.labyModChannel, liteLoaderFolder, request.mmcPackInfo,
                legacyFabricFolder);
            task.Progress = 0.2d;
            // 迁移文件
            if (Directory.Exists(Path.Combine(tempMcFolder, "libraries")))
                ModBase.CopyDirectory(Path.Combine(tempMcFolder, "libraries"), Path.Combine(ModFolder.mcFolderSelected, "libraries"));
            task.Progress = 0.8d;
            // 创建 Mod 和资源包文件夹
            var modsFolder = Path.Combine(new McInstance(instanceFolder).PathIndie, "mods"); // 版本隔离信息在此时被决定
            if (Directory.Exists(modsTempFolder))
            {
                ModBase.CopyDirectory(modsTempFolder, modsFolder);
            }
            else if (modable)
            {
                Directory.CreateDirectory(modsFolder);
                ModBase.Log("[Download] 自动创建 Mod 文件夹：" + modsFolder);
            }

            var resourcepacksFolder = Path.Combine(new McInstance(instanceFolder).PathIndie, "resourcepacks");
            Directory.CreateDirectory(resourcepacksFolder);
            ModBase.Log("[Download] 自动创建资源包文件夹：" + resourcepacksFolder);
        })
        {
            ProgressWeight = 2d,
            block = true
        });
        // 补全文件：MMC 整合包的 JSON Patches 可能引入原版与加载器均不包含的支持库（例如 LWJGL3），也需要补全
        if (!dontFixLibraries && (request.mmcPackInfo is not null || request.optiFineEntry is not null ||
                                  (request.forgeVersion is not null &&
                                   Convert.ToDouble(request.forgeVersion.BeforeFirst(".")) >= 20d) ||
                                  request.neoForgeVersion is not null || request.fabricVersion is not null ||
                                  request.cleanroomVersion is not null ||
                                  request.liteLoaderEntry is not null || request.labyModCommitRef is not null))
        {
            var loadersLib = new List<ModLoader.LoaderBase>();
            if (request.labyModCommitRef is not null)
            {
                var labyModClientLoader = new ModLoader.LoaderCombo<string>(
                    Lang.Text(
                        "Minecraft.Download.Stage.LoaderDownloadCombo",
                        Lang.Text("Minecraft.Version.Vanilla"), request.minecraftName
                    ),
                    McDownloadLabyModClientLoader(
                        request.minecraftName, request.labyModChannel,
                        request.labyModCommitRef, request.targetInstanceName
                    )
                )
                {
                    show = false, ProgressWeight = 39d, block = false
                };
                loaderList.Add(labyModClientLoader);
            }
            else
            {
                loadersLib.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                        Lang.Text("Minecraft.Download.Stage.AnalyzeGameLibrariesSide"),
                        task => task.output =
                            ModLibrary.McLibNetFilesFromInstance(new McInstance(instanceFolder)))
                    { ProgressWeight = 1d, show = false });
                loadersLib.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadGameLibrariesSide"),
                        new List<DownloadFile>())
                    { ProgressWeight = 7d, show = false });
                loaderList.Add(
                    new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Download.Stage.DownloadGameLibraries"),
                        loadersLib) { ProgressWeight = 8d });
            }
        }

        // 删除忽略标识
        loaderList.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Minecraft.Download.Stage.DeleteIgnoreFlag"),
                _ => File.Delete(Path.Combine(instanceFolder, ".pclignore")))
            { show = false });
        // 总加载器
        return loaderList;
    }

}
