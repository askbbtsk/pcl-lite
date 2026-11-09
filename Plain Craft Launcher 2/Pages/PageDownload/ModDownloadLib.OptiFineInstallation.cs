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



    public static void McDownloadOptiFine(ModDownload.DlOptiFineListEntry downloadInfo)
    {
        try
        {
            var id = downloadInfo.NameVersion;
            var versionFolder = Path.Combine(ModFolder.mcFolderSelected, "versions", id);
            var isNewVersion = McVersionComparer.CompareVersionGe(downloadInfo.Inherit, "1.14");
            var target = isNewVersion
                ? Path.Combine(ModBase.pathTemp, "Cache", "Code", downloadInfo.NameVersion + "_" + ModBase.GetUuid())
                : Path.Combine(ModFolder.mcFolderSelected, "libraries", "optifine", "OptiFine",
                    downloadInfo.NameFile.Replace("OptiFine_", "").Replace(".jar", "").Replace("preview_", ""),
                    downloadInfo.NameFile.Replace("OptiFine_", "OptiFine-").Replace("preview_", ""));

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar)
            {
                if ((OngoingLoader.name ?? "") !=
                    (Lang.Text("Minecraft.Download.Stage.OptiFineDownload", downloadInfo.DisplayName) ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            // 已有实例检查
            if (File.Exists(Path.Combine(versionFolder, id + ".json")))
            {
                if (ModMain.MyMsgBox(
                        Lang.Text("Minecraft.Download.Error.InstanceAlreadyExists", id, "\r\n"),
                        Lang.Text("Minecraft.Download.Error.InstanceAlreadyExists.Title"),
                        Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel")
                    ) == 1)
                {
                    File.Delete(Path.Combine(versionFolder, id + ".jar"));
                    File.Delete(Path.Combine(versionFolder, id + ".json"));
                }
                else
                {
                    return;
                }
            }

            // 启动
            var loader =
                new ModLoader.LoaderCombo<string>(
                    Lang.Text("Minecraft.Download.Stage.OptiFineDownload", downloadInfo.DisplayName),
                    McDownloadOptiFineLoader(downloadInfo)) { OnStateChanged = McInstallState };
            loader.Start(versionFolder);
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始 OptiFine 下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    private static void McDownloadOptiFineSave(ModDownload.DlOptiFineListEntry downloadInfo)
    {
        try
        {
            var id = downloadInfo.NameVersion;
            var target = SystemDialogs.SelectSaveFile(Lang.Text("Download.Version.SelectSaveLocation"), downloadInfo.NameFile, "OptiFine Jar (*.jar)|*.jar");
            if (!target.Contains(@"\"))
                return;

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") !=
                    (Lang.Text("Minecraft.Download.Stage.OptiFineDownload", downloadInfo.DisplayName) ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            var loader =
                new ModLoader.LoaderCombo<ModDownload.DlOptiFineListEntry>(
                        Lang.Text("Minecraft.Download.Stage.OptiFineDownload", downloadInfo.DisplayName),
                        McDownloadOptiFineSaveLoader(downloadInfo, target))
                    { OnStateChanged = LoaderStateChangedHintOnly };
            loader.Start(downloadInfo);
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "开始 OptiFine 下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    private static void McDownloadOptiFineInstall(string baseMcFolderHome, string target, ModLoader.LoaderTask<List<DownloadFile>, bool> task, bool useJavaWrapper, Version javaVersion)
    {
        // 选择 Java
        JavaEntry java;
        lock (ModJava.javaLock)
        {
            java = ModJava.JavaSelect(Lang.Text("Minecraft.Download.Error.InstallationCanceled"),
                javaVersion, enforceVersionRange: true);
            if (java is null)
            {
                if (!ModJava.JavaDownloadConfirm(Lang.Text("Minecraft.Download.Error.JavaVersionRequired")))
                    throw new Exception(Lang.Text("Minecraft.Download.Error.JavaNotFoundInstallCanceled"));
                // 开始自动下载
                var downloadJavaMajor = javaVersion.Major >= 9 ? javaVersion.Major : 8;
                var javaLoader = ModJava.GetJavaDownloadLoader();
                try
                {
                    javaLoader.Start(downloadJavaMajor, true);
                    while (javaLoader.State == ModBase.LoadState.Loading && !task.IsAborted)
                        Thread.Sleep(10);
                }
                finally
                {
                    javaLoader.Abort(); // 确保取消时中止 Java 下载
                }

                // 检查下载结果
                java = ModJava.JavaSelect(Lang.Text("Minecraft.Download.Error.InstallationCanceled"),
                        javaVersion, enforceVersionRange: true);
                if (task.IsAborted)
                    return;
                if (java is null)
                    throw new Exception(Lang.Text("Minecraft.Download.Error.JavaNotFoundInstallCanceled"));
            }
        }

        // 添加 Java Wrapper 作为主 Jar
        string arguments;
        if (useJavaWrapper &&
                                  !(dynamic)Config.Launch.DisableJlw) // dynamic!
            arguments =
                $"-Doolloo.jlw.tmpdir=\"{ModBase.pathPure.TrimEnd('\\')}\" -Duser.home=\"{baseMcFolderHome.TrimEnd('\\')}\" -cp \"{target}\" -jar \"{ModLaunch.ExtractJavaWrapper()}\" optifine.Installer";
        else
            arguments = $"-Duser.home=\"{baseMcFolderHome.TrimEnd('\\')}\" -cp \"{target}\" optifine.Installer";
        if (java.Installation.MajorVersion >= 9)
            arguments = "--add-exports cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED " + arguments;
        // 开始启动
        lock (installSyncLock)
        {
            var info = new ProcessStartInfo
            {
                FileName = java.Installation.JavaExePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = ModBase.ShortenPath(baseMcFolderHome)
            };
            if (info.EnvironmentVariables.ContainsKey("appdata"))
                info.EnvironmentVariables["appdata"] = baseMcFolderHome;
            else
                info.EnvironmentVariables.Add("appdata", baseMcFolderHome);
            ModBase.Log("[Download] 开始安装 OptiFine：" + target);
            var totalLength = 0;
            var process = new Process { StartInfo = info };
            var lastResult = "";
            using (var outputWaitHandle = new AutoResetEvent(false))
            {
                using (var errorWaitHandle = new AutoResetEvent(false))
                {
                    process.OutputDataReceived += (_, e) =>
                    {
                        try
                        {
                            if (e.Data is null)
                            {
                                outputWaitHandle.Set();
                            }
                            else
                            {
                                lastResult = e.Data;
                                if (ModBase.modeDebug)
                                    ModBase.Log("[Installer] " + lastResult);
                                totalLength += 1;
                                task.Progress += 0.9d / 7000d;
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(ex, "读取 OptiFine 安装器信息失败");
                        }

                        try
                        {
                            if (task.State == ModBase.LoadState.Aborted && !process.HasExited)
                            {
                                ModBase.Log("[Installer] 由于任务取消，已中止 OptiFine 安装");
                                process.Kill();
                            }
                        }
                        catch
                        {
                        }
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        try
                        {
                            if (e.Data is null)
                            {
                                errorWaitHandle.Set();
                            }
                            else
                            {
                                lastResult = e.Data;
                                if (ModBase.modeDebug)
                                    ModBase.Log("[Installer] " + lastResult);
                                totalLength += 1;
                                task.Progress += 0.9d / 7000d;
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(ex, "读取 OptiFine 安装器错误信息失败");
                        }

                        try
                        {
                            if (task.State == ModBase.LoadState.Aborted && !process.HasExited)
                            {
                                ModBase.Log("[Installer] 由于任务取消，已中止 OptiFine 安装");
                                process.Kill();
                            }
                        }
                        catch
                        {
                        }
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    // 等待
                    while (!process.HasExited)
                        Thread.Sleep(10);
                    // 输出
                    outputWaitHandle.WaitOne(10000);
                    errorWaitHandle.WaitOne(10000);
                    process.Dispose();
                    if (totalLength < 1000 || lastResult.Contains("at "))
                        throw new Exception(Lang.Text("Minecraft.Download.Error.InstallerFailedLastLine", lastResult));
                }
            }
        }
    }


    /// <summary>
    ///     获取下载某个 OptiFine 实例的加载器列表。
    /// </summary>
    private static List<ModLoader.LoaderBase> McDownloadOptiFineLoader(ModDownload.DlOptiFineListEntry downloadInfo,
        string mcFolder = null, ModLoader.LoaderCombo<string> clientDownloadLoader = null, string clientFolder = null,
        bool fixLibrary = true)
    {
        // 参数初始化
        mcFolder = mcFolder ?? ModFolder.mcFolderSelected;
        var isCustomFolder = (mcFolder ?? "") != (ModFolder.mcFolderSelected ?? "");
        var id = downloadInfo.NameVersion;
        var versionFolder = Path.Combine(mcFolder, "versions", id);
        var isNewVersion = downloadInfo.Inherit.Contains("w") || McVersionComparer.CompareVersionGe(downloadInfo.Inherit, "1.14");
        var target = isNewVersion
            ? $"{ModMain.RequestTaskTempFolder()}OptiFine.jar"
            : $@"{mcFolder}libraries\optifine\OptiFine\{downloadInfo.NameFile.Replace("OptiFine_", "").Replace(".jar", "").Replace("preview_", "")}\{downloadInfo.NameFile.Replace("OptiFine_", "OptiFine-").Replace("preview_", "")}";
        var loaders = new List<ModLoader.LoaderBase>();
        var javaVersion = McVersionComparer.CompareVersionGe(downloadInfo.Inherit, "26.1.2")
            ? new Version(21, 0, 0, 0)
            : new Version(1, 8, 0, 0);

        // 获取下载地址
        loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.ObtainOptiFineUrl"), task =>
        {
            // 启动依赖实例的下载
            if (clientDownloadLoader is null)
            {
                if (isCustomFolder)
                    throw new Exception(Lang.Text("Minecraft.Download.Error.NoVanillaLoaderSpecified"));
                clientDownloadLoader = McDownloadClient(NetPreDownloadBehaviour.ExitWhileExistsOrDownloading,
                    downloadInfo.Inherit);
            }

            task.Progress = 0.1d;
            var sources = new List<string>();
            // BMCLAPI 源
            var bmclapiInherit = downloadInfo.Inherit;
            if (bmclapiInherit == "1.8" || bmclapiInherit == "1.9")
                bmclapiInherit += ".0"; // #4281
            if (downloadInfo.IsPreview)
                sources.Add("https://bmclapi2.bangbang93.com/optifine/" + bmclapiInherit + "/HD_U_" +
                            downloadInfo.DisplayName.Replace(downloadInfo.Inherit + " ", "").Replace(" ", "/"));
            else
                sources.Add("https://bmclapi2.bangbang93.com/optifine/" + bmclapiInherit + "/HD_U/" +
                            downloadInfo.DisplayName.Replace(downloadInfo.Inherit + " ", ""));
            // 官方源
            string pageData;
            try
            {
                using (var resp = HttpRequest
                           .Create("https://optifine.net/adloadx?f=" + downloadInfo.NameFile)
                           .WithHeader("Accept", "text/html")
                           .WithHeader("Accept-Language", "en-US,en;q=0.5")
                           .WithHeader("X-Requested-With", "XMLHttpRequest")
                           .SendAsync()
                           .GetAwaiter()
                           .GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    pageData = resp.AsString();
                }
                task.Progress = 0.8d;
                sources.Add("https://optifine.net/" + pageData.RegexSearch(@"downloadx\?f=[^""']+")[0]);
                ModBase.Log("[Download] OptiFine " + downloadInfo.DisplayName + " 官方下载地址：" + sources.Last());
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "获取 OptiFine " + downloadInfo.DisplayName + " 官方下载地址失败");
            }

            // 构造文件请求
            task.output = new List<DownloadFile>
                { new(sources.ToArray(), target, new ModBase.FileChecker(300 * 1024)) };
        })
        {
            ProgressWeight = 8d
        });
        loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadOptiFineMainFile"), [])
            { ProgressWeight = 8d });
        loaders.Add(new ModLoader.LoaderTask<List<DownloadFile>, bool>(
            Lang.Text("Minecraft.Download.Stage.WaitVanillaDownload"), task =>
        {
            // 等待原版文件下载完成
            if (clientDownloadLoader is null)
                return;
            var targetLoaders = clientDownloadLoader.GetLoaderList()
                .Where(l => (l.name ?? "") == mcDownloadClientLibName || (l.name ?? "") == mcDownloadClientJsonName)
                .Where(l => l.State != ModBase.LoadState.Finished).ToList();
            if (targetLoaders.Any())
                ModBase.Log("[Download] OptiFine 安装正在等待原版文件下载完成");
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
                Directory.CreateDirectory(Path.Combine(mcFolder, "versions", downloadInfo.Inherit));
                if (!File.Exists(Path.Combine(mcFolder, "versions", downloadInfo.Inherit, downloadInfo.Inherit + ".json")))
                    ModBase.CopyFile($"{clientFolder}{clientName}.json",
                        $@"{mcFolder}versions\{downloadInfo.Inherit}\{downloadInfo.Inherit}.json");
                if (!File.Exists(Path.Combine(mcFolder, "versions", downloadInfo.Inherit, downloadInfo.Inherit + ".jar")))
                    ModBase.CopyFile($"{clientFolder}{clientName}.jar",
                        $@"{mcFolder}versions\{downloadInfo.Inherit}\{downloadInfo.Inherit}.jar");
            }
        })
        {
            ProgressWeight = 0.1d,
            show = false
        });

        // 安装（新旧方式均需要原版 Jar 和 Json）
        if (isNewVersion)
        {
            ModBase.Log("[Download] 检测为新版 OptiFine：" + downloadInfo.Inherit);
            loaders.Add(new ModLoader.LoaderTask<List<DownloadFile>, bool>(
                Lang.Text("Minecraft.Download.Stage.InstallOptiFine.MethodA"), task =>
            {
                var baseMcFolderHome = ModMain.RequestTaskTempFolder();
                var baseMcFolder = Path.Combine(baseMcFolderHome, ".minecraft");
                try
                {
                    // 准备安装环境
                    if (Directory.Exists(Path.Combine(baseMcFolder, "versions", downloadInfo.Inherit)))
                        ModBase.DeleteDirectory(Path.Combine(baseMcFolder, "versions", downloadInfo.Inherit));
                    Directory.CreateDirectory(Path.Combine(baseMcFolder, "versions", downloadInfo.Inherit));
                    ModFolder.McFolderLauncherProfilesJsonCreate(baseMcFolder);
                    ModBase.CopyFile(
                        Path.Combine(mcFolder, "versions", downloadInfo.Inherit, downloadInfo.Inherit + ".json"),
                        Path.Combine(baseMcFolder, "versions", downloadInfo.Inherit, downloadInfo.Inherit + ".json"));
                    ModBase.CopyFile(
                        Path.Combine(mcFolder, "versions", downloadInfo.Inherit, downloadInfo.Inherit + ".jar"),
                        Path.Combine(baseMcFolder, "versions", downloadInfo.Inherit, downloadInfo.Inherit + ".jar"));
                    task.Progress = 0.06d;
                    // 进行安装
                    var useJavaWrapper = ModBase.IsUtf8CodePage();
                    Retry: ;

                    try
                    {
                        McDownloadOptiFineInstall(baseMcFolderHome, target, task, useJavaWrapper, javaVersion);
                    }
                    catch (Exception ex)
                    {
                        if (!useJavaWrapper)
                        {
                            ModBase.Log(ex, "不使用 JavaWrapper 安装 OptiFine 失败，将使用 JavaWrapper 并重试");
                            useJavaWrapper = true;
                            goto Retry;
                        }

                        throw new Exception(Lang.Text("Minecraft.Download.Error.OptiFineInstallerRunFailed"), ex);
                    }

                    task.Progress = 0.96d;
                    // 复制文件
                    File.Delete(Path.Combine(baseMcFolder, "launcher_profiles.json"));
                    ModBase.CopyDirectory(baseMcFolder, mcFolder);
                    task.Progress = 0.98d;
                    // 清理文件
                    File.Delete(target);
                    ModBase.DeleteDirectory(baseMcFolderHome);
                }
                catch (Exception ex)
                {
                    throw new Exception(Lang.Text("Minecraft.Download.Error.OptiFineInstallFailed.MethodA"), ex);
                }
            })
            {
                ProgressWeight = 8d
            });
        }
        else
        {
            ModBase.Log("[Download] 检测为旧版 OptiFine：" + downloadInfo.Inherit);
            // 新建实例文件夹
            // 复制 Jar 文件
            // 建立 Json 文件
            loaders.Add(new ModLoader.LoaderTask<List<DownloadFile>, bool>(
                    Lang.Text("Minecraft.Download.Stage.InstallOptiFine.MethodB"), task =>
                {
                    try
                    {
                        Directory.CreateDirectory(versionFolder);
                        task.Progress = 0.1d;
                        if (File.Exists(Path.Combine(versionFolder, id + ".jar"))) File.Delete(Path.Combine(versionFolder, id + ".jar"));
                        ModBase.CopyFile(
                            Path.Combine(mcFolder, "versions", downloadInfo.Inherit, downloadInfo.Inherit + ".jar"),
                            Path.Combine(versionFolder, id + ".jar"));
                        task.Progress = 0.7d;
                        var inheritInstance =
                            new McInstance(Path.Combine(mcFolder, "versions", downloadInfo.Inherit));
                        var json = @"{
    ""id"": """ + id + @""",
    ""inheritsFrom"": """ + downloadInfo.Inherit + @""",
    ""time"": """ +
                                   (string.IsNullOrEmpty(downloadInfo.ReleaseTime)
                                       ? inheritInstance.releaseTime.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture)
                                       : downloadInfo.ReleaseTime.Replace("/", "-")) + @"T23:33:33+08:00"",
    ""releaseTime"": """ +
                                   (string.IsNullOrEmpty(downloadInfo.ReleaseTime)
                                       ? inheritInstance.releaseTime.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture)
                                       : downloadInfo.ReleaseTime.Replace("/", "-")) + @"T23:33:33+08:00"",
    ""type"": ""release"",
    ""libraries"": [
        {""name"": ""optifine:OptiFine:" +
                                   downloadInfo.NameFile.Replace("OptiFine_", "").Replace(".jar", "")
                                       .Replace("preview_", "") + // 输出旧版 Json 格式
                                   @"""},
        {""name"": ""net.minecraft:launchwrapper:1.12""}
    ],
    ""mainClass"": ""net.minecraft.launchwrapper.Launch"",";
                        task.Progress = 0.8d;
                        if (inheritInstance.IsOldJson)
                            json += @"
    ""minimumLauncherVersion"": 18,
    ""minecraftArguments"": """ + inheritInstance.JsonObject["minecraftArguments"] + // 输出新版 Json 格式
                                    @"  --tweakClass optifine.OptiFineTweaker""
}";
                        else
                            json += @"
    ""minimumLauncherVersion"": ""21"",
    ""arguments"": {
        ""game"": [
            ""--tweakClass"",
            ""optifine.OptiFineTweaker""
        ]
    }
}";
                        ModBase.WriteFile(Path.Combine(versionFolder, id + ".json"), json);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(Lang.Text("Minecraft.Download.Error.OptiFineInstallFailed.MethodB"), ex);
                    }
                })
                { ProgressWeight = 1d });
        }

        // 下载支持库
        if (fixLibrary)
        {
            loaders.Add(new ModLoader.LoaderTask<string, List<DownloadFile>>(
                    Lang.Text("Minecraft.Download.Stage.AnalyzeOptiFineLibraries"),
                    task => task.output =
                        ModLibrary.McLibNetFilesFromInstance(new McInstance(versionFolder)))
                { ProgressWeight = 1d, show = false });
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadOptiFineLibraries"),
                    new List<DownloadFile>())
                { ProgressWeight = 4d });
        }

        return loaders;
    }


    /// <summary>
    ///     获取保存某个 OptiFine 版本的加载器列表。
    /// </summary>
    private static List<ModLoader.LoaderBase> McDownloadOptiFineSaveLoader(ModDownload.DlOptiFineListEntry downloadInfo,
        string targetFolder)
    {
        var loaders = new List<ModLoader.LoaderBase>();
        // 获取下载地址
        loaders.Add(new ModLoader.LoaderTask<ModDownload.DlOptiFineListEntry, List<DownloadFile>>(
            Lang.Text("Minecraft.Download.Stage.ObtainOptiFineDownloadUrl"),
            task =>
            {
                var sources = new List<string>();
                // BMCLAPI 源
                var bmclapiInherit = downloadInfo.Inherit;
                if (bmclapiInherit == "1.8" || bmclapiInherit == "1.9")
                    bmclapiInherit += ".0"; // #4281
                if (downloadInfo.IsPreview)
                    sources.Add("https://bmclapi2.bangbang93.com/optifine/" + bmclapiInherit + "/HD_U_" +
                                downloadInfo.DisplayName.Replace(downloadInfo.Inherit + " ", "").Replace(" ", "/"));
                else
                    sources.Add("https://bmclapi2.bangbang93.com/optifine/" + bmclapiInherit + "/HD_U/" +
                                downloadInfo.DisplayName.Replace(downloadInfo.Inherit + " ", ""));
                // 官方源
                string pageData;
                try
                {
                    using (var resp = HttpRequest
                            .Create("https://optifine.net/adloadx?f=" + downloadInfo.NameFile)
                            .WithHeader("Accept", "text/html")
                            .WithHeader("Accept-Language", "en-US,en;q=0.5")
                            .WithHeader("X-Requested-With", "XMLHttpRequest")
                            .SendAsync().GetAwaiter().GetResult())
                    {
                        resp.EnsureSuccessStatusCode();
                        pageData = resp.AsString();
                    }
                    task.Progress = 0.8d;
                    sources.Add("https://optifine.net/" + pageData.RegexSearch(@"downloadx\?f=[^""']+")[0]);
                    ModBase.Log("[Download] OptiFine " + downloadInfo.DisplayName + " 官方下载地址：" + sources.Last());
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "获取 OptiFine " + downloadInfo.DisplayName + " 官方下载地址失败");
                }

                task.Progress = 0.9d;
                // 构造文件请求
                task.output = new List<DownloadFile>
                    { new(sources.ToArray(), targetFolder, new ModBase.FileChecker(64 * 1024)) };
            })
        {
            ProgressWeight = 6d
        });
        // 下载
        loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadOptiFineMainFile"),
                new List<DownloadFile>())
            { ProgressWeight = 10d, block = true });
        return loaders;
    }




    public static MyListItem OptiFineDownloadListItem(ModDownload.DlOptiFineListEntry entry,
        MyListItem.ClickEventHandler onClick, bool isSaveOnly)
    {
        var infoParts = new List<string>
        {
            entry.IsPreview
                ? Lang.Text("Download.Version.Type.Preview")
                : Lang.Text("Download.Version.Type.Release")
        };

        if (!string.IsNullOrEmpty(entry.ReleaseTime))
            infoParts.Add(Lang.Text("Download.Version.ReleaseDate", entry.ReleaseTime));

        if (entry.RequiredForgeVersion is null)
            infoParts.Add(Lang.Text("Download.Version.Optifine.IncompatibleForge"));
        else if (!string.IsNullOrEmpty(entry.RequiredForgeVersion))
            infoParts.Add(Lang.Text("Download.Version.Optifine.CompatibleForge", entry.RequiredForgeVersion));

        var newItem = new MyListItem
        {
            Title = entry.DisplayName,
            SnapsToDevicePixels = true,
            Height = 42d,
            Type = MyListItem.CheckType.Clickable,
            Tag = entry,
            Info = string.Join("  |  ", infoParts),
            Logo = ModBase.pathImage + "Blocks/GrassPath.png"
        };

        newItem.Click += onClick;
        // 建立菜单
        newItem.ContentHandler = isSaveOnly
            ? OptiFineSaveContMenuBuild
            : OptiFineContMenuBuild;
        // 结束
        return newItem;
    }


    private static void OptiFineSaveContMenuBuild(object sender, EventArgs e)
    {
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (sender, e) => OptiFineLog_Click(sender, (RoutedEventArgs)e);
        ((dynamic)sender).Buttons = new[] { btnInfo };
    }


    private static void OptiFineContMenuBuild(object sender, EventArgs e)
    {
        var btnSave = new MyIconButton { SvgIcon = "lucide/save", ToolTip = Lang.Text("Download.Version.SaveAs") };
        ToolTipService.SetPlacement(btnSave, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnSave, 30d);
        ToolTipService.SetHorizontalOffset(btnSave, 2d);
        //btnSave.Click += () ModDownloadLib.OptiFineSave_Click;
        var btnInfo = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/info", ToolTip = Lang.Text("Download.Version.Changelog") };
        ToolTipService.SetPlacement(btnInfo, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnInfo, 30d);
        ToolTipService.SetHorizontalOffset(btnInfo, 2d);
        btnInfo.Click += (sender, e) => OptiFineLog_Click(sender, (RoutedEventArgs)e);
        ((dynamic)sender).Buttons = new[] { btnSave, btnInfo };
    }


    private static void OptiFineLog_Click(object sender, RoutedEventArgs e)
    {
        ModDownload.DlOptiFineListEntry version;
        if (((dynamic)sender).Tag is not null)
            version = (ModDownload.DlOptiFineListEntry)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            version = (ModDownload.DlOptiFineListEntry)((dynamic)sender).Parent.Tag;
        else
            version = (ModDownload.DlOptiFineListEntry)((dynamic)sender).Parent.Parent.Tag;
        ModBase.OpenWebsite("https://optifine.net/changelog?f=" + version.NameFile);
    }


    public static void OptiFineSave_Click(object sender, RoutedEventArgs e)
    {
        ModDownload.DlOptiFineListEntry version;
        if (((dynamic)sender).Tag is not null)
            version = (ModDownload.DlOptiFineListEntry)((dynamic)sender).Tag;
        else if (((dynamic)sender).Parent.Tag is not null)
            version = (ModDownload.DlOptiFineListEntry)((dynamic)sender).Parent.Tag;
        else
            version = (ModDownload.DlOptiFineListEntry)((dynamic)sender).Parent.Parent.Tag;
        McDownloadOptiFineSave(version);
    }

}
