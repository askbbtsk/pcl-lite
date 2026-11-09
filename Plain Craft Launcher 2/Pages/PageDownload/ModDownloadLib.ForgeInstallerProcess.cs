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



    public static void McDownloadForgelikeSave(ModDownload.DlForgelikeEntry info)
    {
        try
        {
            var target = SystemDialogs.SelectSaveFile(Lang.Text("Download.Version.SelectSaveLocation"),
                $"{info.LoaderName}-{info.Inherit}-{info.VersionName}.{info.FileExtension}",
                Lang.Text("Minecraft.Download.Stage.ForgelikeInstallerFilter", info.LoaderName, info.FileExtension));
            var displayName = $"{info.LoaderName} {info.Inherit} - {info.VersionName}";
            if (!target.Contains(@"\"))
                return;

            // 重复任务检查
            foreach (var OngoingLoader in ModLoader.loaderTaskbar.ToList())
            {
                if ((OngoingLoader.name ?? "") !=
                    (Lang.Text("Minecraft.Download.Stage.ForgelikeDownload", displayName) ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Minecraft.Download.Error.InstanceDownloading"), HintType.Error);
                return;
            }

            // 获取下载地址
            var files = new List<DownloadFile>();
            if (info.forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.NeoForge)
            {
                // NeoForge
                var neo = (ModDownload.DlNeoForgeListEntry)info;
                var url = neo.UrlBase + "-installer.jar";
                files.Add(new DownloadFile(
                    new[] { url.Replace("maven.neoforged.net/releases", "bmclapi2.bangbang93.com/maven"), url }, target,
                    new ModBase.FileChecker(64 * 1024)));
            }
            else if (info.forgeType == ModDownload.DlForgelikeEntry.ForgelikeType.Cleanroom)
            {
                // Cleanroom
                var clr = (ModDownload.DlCleanroomListEntry)info;
                var url = clr.UrlBase + "-installer.jar";
                files.Add(new DownloadFile(new[] { url }, target, new ModBase.FileChecker(64 * 1024)));
            }
            else
            {
                // Forge
                var forge = (ModDownload.DlForgeVersionEntry)info;
                files.Add(new DownloadFile(
                    new[]
                    {
                        $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{forge.Inherit}-{forge.FileVersion}/forge-{forge.Inherit}-{forge.FileVersion}-{forge.Category}.{forge.FileExtension}",
                        $"https://files.minecraftforge.net/maven/net/minecraftforge/forge/{forge.Inherit}-{forge.FileVersion}/forge-{forge.Inherit}-{forge.FileVersion}-{forge.Category}.{forge.FileExtension}"
                    }, target, new ModBase.FileChecker(64 * 1024, hash: forge.Hash)));
            }

            // 构造加载器
            var loaders = new List<ModLoader.LoaderBase>();
            loaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Stage.DownloadMainFile"), files)
                { ProgressWeight = 6d });

            // 启动
            var loader =
                new ModLoader.LoaderCombo<ModDownload.DlForgelikeEntry>(
                        Lang.Text("Minecraft.Download.Stage.ForgelikeDownload", displayName), loaders)
                { OnStateChanged = LoaderStateChangedHintOnly };
            loader.Start(info);
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"开始 {info.LoaderName} 安装器下载失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Download.Error.OperationFailed"));
        }
    }


    private static void ForgelikeInjector(string target, ModLoader.LoaderTask<bool, bool> task, string mcFolder,
        bool useJavaWrapper, ModDownload.DlForgelikeEntry.ForgelikeType forgeType)
    {
        // 选择 Java
        JavaEntry java;
        lock (ModJava.javaLock)
        {
            java = ModJava.JavaSelect(Lang.Text("Minecraft.Download.Error.InstallationCanceled"),
                new Version(1, 8, 0, 60));
            if (java is null)
            {
                if (!ModJava.JavaDownloadConfirm(Lang.Text("Minecraft.Download.Error.JavaVersionRequired")))
                    throw new Exception(Lang.Text("Minecraft.Download.Error.JavaNotFoundInstallCanceled"));
                // 开始自动下载
                var javaLoader = ModJava.GetJavaDownloadLoader();
                try
                {
                    javaLoader.Start(17, true);
                    while (javaLoader.State == ModBase.LoadState.Loading && !task.IsAborted)
                        Thread.Sleep(10);
                }
                finally
                {
                    javaLoader.Abort(); // 确保取消时中止 Java 下载
                }

                // 检查下载结果
                java = ModJava.JavaSelect(Lang.Text("Minecraft.Download.Error.InstallationCanceled"),
                    new Version(1, 8, 0, 60));
                if (task.IsAborted)
                    return;
                if (java is null)
                    throw new Exception(Lang.Text("Minecraft.Download.Error.JavaNotFoundInstallCanceled"));
            }
        }

        // 添加 Java Wrapper 作为主 Jar
        string arguments;
        if (useJavaWrapper && !Config.Launch.DisableJlw)
            arguments =
                $@"-Doolloo.jlw.tmpdir=""{ModBase.pathPure.TrimEnd('\\')}"" -cp ""{ModBase.pathTemp}Cache\forge_installer.jar;{target}"" -jar ""{ModLaunch.ExtractJavaWrapper()}"" com.bangbang93.ForgeInstaller ""{mcFolder}";
        else
            arguments =
                $@"-cp ""{ModBase.pathTemp}Cache\forge_installer.jar;{target}"" com.bangbang93.ForgeInstaller ""{mcFolder}";
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
                RedirectStandardOutput = true
            };
            string loaderName = ModBase.GetStringFromEnum(forgeType);
            ModBase.Log($"[Download] 开始安装 {loaderName}：" + arguments);
            var process = new Process { StartInfo = info };
            var lastResults = new Queue<string>();
            using (var outputWaitHandle = new AutoResetEvent(false))
            {
                using (var errorWaitHandle = new AutoResetEvent(false))
                {
                    process.OutputDataReceived += (sender, e) =>
                    {
                        try
                        {
                            if (e.Data is null)
                            {
                                outputWaitHandle.Set();
                            }
                            else
                            {
                                lastResults.Enqueue(e.Data);
                                if (lastResults.Count > 100)
                                    lastResults.Dequeue();
                                ForgelikeInjectorLine(e.Data, task);
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(ex, $"读取 {loaderName} 安装器信息失败");
                        }

                        try
                        {
                            if (task.State == ModBase.LoadState.Aborted && !process.HasExited)
                            {
                                ModBase.Log($"[Installer] 由于任务取消，已中止 {loaderName} 安装");
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
                                lastResults.Enqueue(e.Data);
                                if (lastResults.Count > 100)
                                    lastResults.Dequeue();
                                ForgelikeInjectorLine(e.Data, task);
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(ex, $"读取 {loaderName} 安装器错误信息失败");
                        }

                        try
                        {
                            if (task.State == ModBase.LoadState.Aborted && !process.HasExited)
                            {
                                ModBase.Log($"[Installer] 由于任务取消，已中止 {loaderName} 安装");
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
                    // 检查是否安装成功：最后 5 行中是否有 true（true 可能在倒数数行，见 #832）
                    if (lastResults.Reverse().Take(5).Any(l => l == "true"))
                        return;
                    ModBase.Log(lastResults.Join("\r\n"));
                    var lastLines = "";
                    for (int i = Math.Max(0, lastResults.Count - 5), loopTo = lastResults.Count - 1;
                         i <= loopTo;
                         i++) // 最后 5 行
                        lastLines += "\r\n" + lastResults.ElementAtOrDefault(i);
                    throw new Exception(Lang.Text("Minecraft.Download.Error.LoaderInstallerFailedLastLine", loaderName,
                        lastLines));
                }
            }
        }
    }


    private static void ForgelikeInjectorLine(string content, ModLoader.LoaderTask<bool, bool> task)
    {
        switch (content ?? "")
        {
            case "Extracting json":
            {
                ModBase.Log("[Installer] " + content);
                task.Progress = 0.07d;
                break;
            }
            case "Downloading libraries":
            {
                ModBase.Log("[Installer] " + content);
                task.Progress = 0.08d;
                break;
            }
            case "  File exists: Checksum validated.":
            {
                if (ModBase.modeDebug)
                    ModBase.Log("[Installer] " + content);
                task.Progress += 0.003d;
                break;
            }
            case "Building Processors":
            {
                task.Progress = 0.18d;
                break;
            }
            case "Task: DOWNLOAD_MOJMAPS": // B
            {
                task.Progress = 0.2d;
                break;
            }
            case "Task: MERGE_MAPPING": // B
            {
                task.Progress = 0.3d;
                break;
            }
            case "Splitting: ":
            {
                task.Progress = 0.35d;
                break;
            }
            case "Parameter Annotations": // B
            {
                task.Progress = 0.4d;
                break;
            }
            case "Processing Complete": // B
            {
                task.Progress = 0.5d;
                break;
            }
            case "log: null": // new
            {
                task.Progress = 0.5d;
                break;
            }
            case "Sorting": // new
            {
                task.Progress = 0.65d;
                break;
            }
            case "Remapping final jar": // A
            {
                task.Progress = 0.72d;
                break;
            }
            case "Remapping jar... 50%": // A
            {
                task.Progress = 0.76d;
                break;
            }
            case "Remapping jar... 100%": // A
            {
                task.Progress = 0.81d;
                break;
            }
            case "Injecting profile":
            {
                task.Progress = 0.91d;
                break;
            }

            default:
            {
                if (ModBase.modeDebug)
                    ModBase.Log("[Installer] " + content);
                return;
            }
        }

        ModBase.Log("[Installer] " + content);
    }

}
