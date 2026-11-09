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
    ///     获取下载某个 Forgelike 实例的加载器列表。
    /// </summary>
    private static List<ModLoader.LoaderBase> McDownloadForgelikeLoader(ModDownload.DlForgelikeEntry.ForgelikeType forgeType, string loaderVersion,
        string targetVersion, string inherit, ModDownload.DlForgelikeEntry info = null, string mcFolder = null, ModLoader.LoaderCombo<string> clientDownloadLoader = null, string clientFolder = null)
    {
        // 参数初始化
        mcFolder = mcFolder ?? ModFolder.mcFolderSelected;
        if (forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.NeoForge && info is null)
        {
            // 需要传入 API Name，但整合包版本可能不以 1.20.1- 开头，所以需要进行特别处理
            if (inherit == "1.20.1" && !loaderVersion.StartsWithF("1.20.1-"))
                info = new ModDownload.DlNeoForgeListEntry("1.20.1-" + loaderVersion);
            else
                info = new ModDownload.DlNeoForgeListEntry(loaderVersion);
        }

        if (forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.Cleanroom && info is null) info = new ModDownload.DlCleanroomListEntry(loaderVersion);
        if (forgeType != ModDownload.DlForgelikeEntry.ForgelikeType.NeoForge && loaderVersion.StartsWithF("1.") && loaderVersion.Contains("-"))
        {
            // 类似 1.19.3-41.2.8 格式，优先使用 Version 中要求的版本而非 Inherit（例如 1.19.3 却使用了 1.19 的 Forge）
            inherit = loaderVersion.BeforeFirst("-");
            loaderVersion = loaderVersion.AfterLast("-");
        }

        string loaderName = ModBase.GetStringFromEnum(forgeType);
        var isCustomFolder = (mcFolder ?? "") != (ModFolder.mcFolderSelected ?? "");
        var installerAddress = ModMain.RequestTaskTempFolder() + "forge_installer.jar";
        var versionFolder = $@"{mcFolder}versions\{targetVersion}\";
        var displayName = $"{loaderName} {inherit} - {loaderVersion}";
        var loaders = new List<ModLoader.LoaderBase>();
        var libVersionFolder = $@"{ModFolder.mcFolderSelected}versions\{targetVersion}\"; // 作为 Lib 文件目标的实例文件夹

        // 获取 Forge 下载信息
        if (info is null)
            loaders.Add(new ModLoader.LoaderTask<string, string>(
                Lang.Text("Minecraft.Download.Stage.ObtainLoaderDetails", loaderName), task =>
            {
                // 获取 Forge 对应 MC 版本列表
                var forgeLoader =
                    new ModLoader.LoaderTask<string, List<ModDownload.DlForgeVersionEntry>>(
                        "McDownloadForgeLoader " + inherit, ModDownload.DlForgeVersionMain);
                forgeLoader.WaitForExit(inherit);
                task.Progress = 0.8d;
                // 查找对应版本
                foreach (var ForgeVersion in forgeLoader.output)
                    if (McVersionComparer.CompareVersion(ForgeVersion.version.ToString(), loaderVersion) == 0)
                    {
                        info = ForgeVersion;
                        return;
                    }

                throw new Exception(Lang.Text("Minecraft.Download.Error.LoaderDetailsNotFound", loaderName, inherit,
                    loaderVersion));
            })
            {
                ProgressWeight = 3d
            });
        // 下载 Forgelike 主文件
        loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.PrepareLoaderDownload", loaderName), task =>
        {
            // 启动依赖实例的下载
            if (clientDownloadLoader is null)
            {
                if (isCustomFolder)
                    throw new Exception(Lang.Text("Minecraft.Download.Error.NoVanillaLoaderSpecified"));
                clientDownloadLoader =
                    McDownloadClient(NetPreDownloadBehaviour.ExitWhileExistsOrDownloading, inherit);
            }

            // 添加主文件下载
            var files = new List<DownloadFile>();
            if (info.forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.NeoForge)
            {
                // NeoForge
                var neo = (ModDownload.DlNeoForgeListEntry)info;
                var url = neo.UrlBase + "-installer.jar";
                files.Add(new DownloadFile(
                    new[] { url.Replace("maven.neoforged.net/releases", "bmclapi2.bangbang93.com/maven"), url },
                    installerAddress, new ModBase.FileChecker(64 * 1024)));
            }
            else if (info.forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.Cleanroom)
            {
                // Cleanroom
                var clr = (ModDownload.DlCleanroomListEntry)info;
                var url = clr.UrlBase + "-installer.jar";
                files.Add(new DownloadFile(new[] { url }, installerAddress, new ModBase.FileChecker(64 * 1024)));
            }
            else
            {
                // Forge
                var forge = (ModDownload.DlForgeVersionEntry)info;
                var fileName =
                    $"{forge.Inherit.Replace("-", "_")}-{forge.FileVersion}/forge-{forge.Inherit.Replace("-", "_")}-{forge.FileVersion}-{forge.Category}.{forge.FileExtension}";
                files.Add(new DownloadFile(
                    new[]
                    {
                        $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{fileName}",
                        $"https://files.minecraftforge.net/maven/net/minecraftforge/forge/{fileName}"
                    }, installerAddress, new ModBase.FileChecker(64 * 1024, hash: forge.Hash)));
            }

            task.output = files;
        })
        {
            ProgressWeight = 0.5d,
            show = false
        });
        loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLoaderMainFile", loaderName),
                new List<DownloadFile>())
            { ProgressWeight = 9d });

        // 安装（仅在新版安装时需要原版 Jar）
        if (forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.NeoForge || Convert.ToDouble(loaderVersion.BeforeFirst(".")) >= 20d)
        {
            ModBase.Log($"[Download] 检测为{(forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.Forge ? "新版 Forge" : " " + forgeType)}：" + loaderVersion);
            List<ModLibrary.McLibToken> libs = null;
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                Lang.Text("Minecraft.Download.Stage.AnalyzeLoaderLibraries", loaderName), task =>
            {
                task.output = new List<DownloadFile>();
                ZipArchive installer = null;
                try
                {
                    // 解压并获取、合并两个 Json 的信息
                    ModBase.WaitForFileReady(installerAddress);
                    installer = new ZipArchive(new FileStream(installerAddress, FileMode.Open));
                    task.Progress = 0.2d;
                    var json = (JsonObject)ModBase.GetJson(
                        ModBase.ReadFile(installer.GetEntry("install_profile.json").Open()));
                    var json2 = (JsonObject)ModBase.GetJson(ModBase.ReadFile(installer.GetEntry("version.json").Open()));
                    json.Merge(json2);
                    // 如果是 1.16.5 就升级一下 Authlib
                    if (inherit == "1.16.5" && (bool)Config.Download.FixAuthLib)
                        json = (JsonObject)ModBase.GetJson(json.ToString()
                            .Replace("2.1.28/authlib-2.1.28.jar", "2.3.31/authlib-2.3.31.jar")
                            .Replace("com.mojang:authlib:2.1.28", "com.mojang:authlib:2.3.31")
                            .Replace("ad54da276bf59983d02d5ed16fc14541354c71fd",
                                "bbd00ca33b052f73a6312254780fc580d2da3535").Replace("76328", "87662"));
                    // 获取 Lib 下载信息
                    libs = ModLibrary.McLibListGetWithJson(json, true);
                    // 添加 Mappings 下载信息
                    if (json["data"] is not null && json["data"]["MOJMAPS"] is not null)
                    {
                        // 下载原版 Json 文件
                        task.Progress = 0.4d;
                        var rawJson = (JsonObject)ModBase.GetJson(ModNet.NetGetCodeByLoader(
                            ModDownload.DlSourceLauncherOrMetaGet(
                                ModDownload.DlClientListGet(inherit)?.ToString()), isJson: true));
                        // [net.minecraft:client:1.17.1-20210706.113038:mappings@txt] 或 @tsrg]
                        var originalName = json["data"]["MOJMAPS"]["client"].ToString().Trim("[]".ToCharArray())
                            .BeforeFirst("@");
                        var address = ModLibrary.McLibGet(originalName).Replace(".jar",
                            "-mappings." + json["data"]["MOJMAPS"]["client"].ToString().Trim("[]".ToCharArray())
                                .Split("@")[1]);
                        var clientMappings = rawJson["downloads"]["client_mappings"];
                        libs.Add(new ModLibrary.McLibToken
                        {
                            IsNatives = false,
                            LocalPath = address,
                            OriginalName = originalName,
                            Url = (string)clientMappings["url"],
                            size = (long)clientMappings["size"],
                            Sha1 = (string)clientMappings["sha1"]
                        });
                        ModBase.Log(
                            $"[Download] 需要下载 Mappings：{clientMappings["url"]} (SHA1: {clientMappings["sha1"]})");
                    }

                    task.Progress = 0.8d;
                    // 去除其中的原始 Forgelike 项
                    for (int i = 0, loopTo = libs.Count - 1; i <= loopTo; i++)
                        if (libs[i].LocalPath.EndsWithF($"{loaderName.ToLower()}-{inherit}-{loaderVersion}.jar") ||
                            libs[i].LocalPath.EndsWithF($"{loaderName.ToLower()}-{inherit}-{loaderVersion}-client.jar"))
                        {
                            ModBase.Log($"[Download] 已从待下载 {loaderName} 支持库中移除：" + libs[i].LocalPath,
                                ModBase.LogLevel.Debug);
                            libs.RemoveAt(i);
                            break;
                        }

                    task.output = ModLibrary.McLibNetFilesFromTokens(libs);
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        Lang.Text("Minecraft.Download.Error.LoaderLibraryListFailed",
                            forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.Forge
                                ? Lang.Text("Minecraft.Download.Loader.NewForge")
                                : " " + forgeType), ex);
                }
                finally
                {
                    // 释放文件
                    if (installer is not null)
                        installer.Dispose();
                }
            })
            {
                ProgressWeight = 2d
            });
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadLoaderLibraries", loaderName),
                    new List<DownloadFile>())
                { ProgressWeight = 12d });
            loaders.Add(new ModLoader.LoaderTask<List<DownloadFile>, bool>(
                Lang.Text("Minecraft.Download.Stage.GetLoaderLibraries", loaderName), task =>
            {

                if (isCustomFolder)
                    foreach (var LibFile in libs)
                    {
                        var realPath = LibFile.LocalPath.Replace(ModFolder.mcFolderSelected, mcFolder);
                        if (!File.Exists(realPath))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(realPath));
                            ModBase.CopyFile(LibFile.LocalPath, realPath);
                        }

                        if (ModBase.modeDebug)
                            ModBase.Log($"[Download] 复制的 {loaderName} 支持库文件：" + LibFile.LocalPath);
                    }



                // 等待原版文件下载完成
                if (clientDownloadLoader is null)
                    return;
                var targetLoaders = clientDownloadLoader.GetLoaderList()
                    .Where(l => (l.name ?? "") == mcDownloadClientLibName || (l.name ?? "") == mcDownloadClientJsonName)
                    .Where(l => l.State != ModBase.LoadState.Finished).ToList();
                if (targetLoaders.Any())
                    ModBase.Log($"[Download] {loaderName} 安装正在等待原版文件下载完成");
                while (targetLoaders.Any() && !task.IsAborted)
                {
                    targetLoaders = targetLoaders.Where(l => l.State != ModBase.LoadState.Finished).ToList();
                    Thread.Sleep(50);
                }

                if (task.IsAborted)
                    return;
                // 拷贝原版文件
                if (!isCustomFolder)
                    return;
                lock (vanillaSyncLock)
                {
                    var clientName = ModBase.GetFolderNameFromPath(clientFolder);
                    Directory.CreateDirectory(Path.Combine(mcFolder, "versions", inherit));
                    if (!File.Exists(Path.Combine(mcFolder, "versions", inherit, inherit + ".json")))
                        ModBase.CopyFile(Path.Combine(clientFolder, clientName + ".json"),
                            Path.Combine(mcFolder, "versions", inherit, inherit + ".json"));
                    if (!File.Exists(Path.Combine(mcFolder, "versions", inherit, inherit + ".jar")))
                        ModBase.CopyFile(Path.Combine(clientFolder, clientName + ".jar"),
                            Path.Combine(mcFolder, "versions", inherit, inherit + ".jar"));
                }

            })
            {
                ProgressWeight = 0.1d,
                show = false
            });
            loaders.Add(new ModLoader.LoaderTask<bool, bool>(
                forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.Forge
                    ? Lang.Text("Minecraft.Download.Stage.InstallForge.MethodA")
                    : Lang.Text("Minecraft.Download.Stage.InstallForgeType", forgeType), task =>
                {
                    ModBase.WaitForFileReady(installerAddress);
                    var installer = new ZipArchive(new FileStream(installerAddress, FileMode.Open));
                    try
                    {
                        // 记录当前文件夹列表（在新建目标文件夹之前）
                        ModBase.Log("[Download] 开始进行 Forgelike 安装：" + installerAddress);
                        // 解压并获取信息
                        var oldList = new DirectoryInfo(mcFolder + "versions/")
                            .EnumerateDirectories().Select(i => i.FullName).ToList();


                        // 新建目标实例文件夹
                        var json = ModBase.GetJson(ModBase.ReadFile(installer.GetEntry("install_profile.json").Open()));
                        Directory.CreateDirectory(versionFolder);
                        task.Progress = 0.04d;
                        // 释放 launcher_installer.json
                        ModFolder.McFolderLauncherProfilesJsonCreate(mcFolder);
                        task.Progress = 0.05d;
                        // 运行 Forge 安装器
                        var useJavaWrapper = ModBase.IsUtf8CodePage();
                        Retry:

                        try
                        {
                            // 释放 Forge 注入器
                            ModBase.WriteFile(Path.Combine(ModBase.pathTemp, "Cache", "forge_installer.jar"),
                                ModBase.GetResourceStream("Resources/forge-installer.jar"));
                            task.Progress = 0.06d;
                            // 运行注入器
                            ForgelikeInjector(installerAddress, task, mcFolder, useJavaWrapper, forgeType);
                            task.Progress = 0.97d;
                        }
                        catch (Exception ex)
                        {
                            if (!useJavaWrapper)
                            {
                                ModBase.Log(ex, $"不使用 JavaWrapper 安装 {loaderName} 失败，将使用 JavaWrapper 并重试");
                                useJavaWrapper = true;
                                goto Retry;
                            }

                            throw new Exception(
                                Lang.Text("Minecraft.Download.Error.LoaderInstallerRunFailed", loaderName), ex);
                            // 拷贝新增的实例 Json
                        }

                        var deltaList = new DirectoryInfo(mcFolder + "versions/").EnumerateDirectories()
                            .SkipWhile(i => oldList.Contains(i.FullName)).ToList();

                        if (deltaList.Count > 1)
                            // 它可能和 OptiFine 安装同时运行，导致增加的文件不止一个（这导致了 #151）
                            // 也可能是因为 Forge 安装器的 Bug，生成了一个名字错误的文件夹，所以需要检查文件夹是否为空
                            deltaList = deltaList
                                .Where(l => l.Name.ContainsF("forge", true) && l.EnumerateFiles().Any())
                                .ToList();
                        // 如果没有新增文件夹，那么预测的文件夹名就是正确的
                        // 如果只新增 1 个文件夹，那么拷贝 Json 文件
                        if (deltaList.Count == 1)
                        {
                            var jsonFile = deltaList[0].EnumerateFiles().First();
                            ModBase.WriteFile(Path.Combine(versionFolder, targetVersion + ".json"),
                                ModBase.ReadFile(jsonFile.FullName));
                            ModBase.Log(
                                $"[Download] 已拷贝新增的实例 Json 文件：{jsonFile.FullName} -> {versionFolder}{targetVersion}.json");
                        }
                        else if (deltaList.Count > 1)
                        {
                            // 新增了多个文件夹
                            //Enumerable.Select<string>((IEnumerable<DirectoryInfo>)DeltaList, d => d.Name).Join(";")
                            ModBase.Log(
                                $"[Download] 有多个疑似的新增实例，无法确定：{string.Join(";", deltaList.Select<DirectoryInfo, string>(d => d.Name))}");
                        }
                        else
                        {
                            // 没有新增文件夹
                            ModBase.Log("[Download] 未找到新增的实例文件夹");
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(Lang.Text("Minecraft.Download.Error.LoaderInstallFailed", loaderName), ex);
                    }
                    finally
                    {
                        // 清理文件
                        try
                        {
                            if (installer is not null)
                                installer.Dispose();
                            if (File.Exists(installerAddress))
                                File.Delete(installerAddress);
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(ex, $"安装 {loaderName} 清理文件时出错");
                        }
                    }
                })
            {
                ProgressWeight = 10d
            });
        }
        else
        {
            ModBase.Log("[Download] 检测为非新版 Forge：" + loaderVersion);
            loaders.Add(new ModLoader.LoaderTask<List<DownloadFile>, bool>(
                $"{(forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.Forge ? Lang.Text("Minecraft.Download.Stage.InstallForge.MethodB") : Lang.Text("Minecraft.Download.Stage.InstallForgeType", forgeType))}",
                task =>
                {
                    ZipArchive installer = null;
                    try
                    {
                        // 解压并获取信息
                        ModBase.WaitForFileReady(installerAddress);
                        installer = new ZipArchive(new FileStream(installerAddress, FileMode.Open));
                        task.Progress = 0.2d;
                        var json = (JsonObject)ModBase.GetJson(
                            ModBase.ReadFile(installer.GetEntry("install_profile.json").Open()));
                        task.Progress = 0.4d;
                        // 新建实例文件夹
                        Directory.CreateDirectory(versionFolder);
                        task.Progress = 0.5d;
                        if (json["install"] is null)
                        {
                            // 中版：Legacy 方式 1
                            ModBase.Log("[Download] 开始进行 Forge 安装，Legacy 方式 1：" + installerAddress);
                            // 建立 Json 文件
                            var jsonVersion = (JsonObject)ModBase.GetJson(
                                ModBase.ReadFile(installer.GetEntry(json["json"].ToString().TrimStart('/')).Open()));
                            jsonVersion["id"] = targetVersion;
                            ModBase.WriteFile(Path.Combine(versionFolder, targetVersion + ".json"), jsonVersion.ToString());
                            task.Progress = 0.6d;
                            // 解压支持库文件
                            installer.Dispose();
                            var unrarDir = Path.Combine(Path.GetDirectoryName(installerAddress), "_unrar");
                            ModBase.ExtractFile(installerAddress, unrarDir);
                            ModBase.CopyDirectory(Path.Combine(unrarDir, "maven"), Path.Combine(mcFolder, "libraries"));
                            ModBase.DeleteDirectory(unrarDir);
                        }
                        else
                        {
                            // 旧版：Legacy 方式 2
                            ModBase.Log("[Download] 开始进行 Forge 安装，Legacy 方式 2：" + installerAddress);
                            // 解压 Jar 文件
                            var jarAddress = ModLibrary.McLibGet((string)json["install"]["path"],
                                customMcFolder: mcFolder);
                            if (File.Exists(jarAddress))
                                File.Delete(jarAddress);
                            ModBase.WriteFile(jarAddress,
                                installer.GetEntry((string)json["install"]["filePath"]).Open());
                            task.Progress = 0.9d;
                            // 建立 Json 文件
                            json["versionInfo"]["id"] = targetVersion;
                            if (json["versionInfo"]["inheritsFrom"] is null)
                                ((JsonObject)json["versionInfo"]).Add("inheritsFrom", inherit);
                            ModBase.WriteFile(Path.Combine(versionFolder, targetVersion + ".json"), json["versionInfo"].ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(Lang.Text("Minecraft.Download.Error.ForgeOldInstallFailed"), ex);
                    }
                    finally
                    {
                        try
                        {
                            // 清理文件
                            if (installer is not null)
                                installer.Dispose();
                            if (File.Exists(installerAddress))
                                File.Delete(installerAddress);
                            var unrarDir = Path.Combine(Path.GetDirectoryName(installerAddress), "_unrar");
                            if (Directory.Exists(unrarDir))
                                ModBase.DeleteDirectory(unrarDir);
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(ex, "非新版方式安装 Forge 清理文件时出错");
                        }
                    }
                })
            {
                ProgressWeight = 1d
            });
        }

        return loaders;
    }

}
