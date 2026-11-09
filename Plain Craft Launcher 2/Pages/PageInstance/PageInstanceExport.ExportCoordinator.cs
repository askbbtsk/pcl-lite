using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using DotNet.Globbing;
using PCL.Core.App;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.Java.UserPreference;
using PCL.Core.UI;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceExport
{

    /// <summary>
    ///     开始导出。
    /// </summary>
    private void StartExport(object sender, MouseButtonEventArgs e)
    {
        var packName = string.IsNullOrEmpty(TextExportName.Text) ? TextExportName.HintText : TextExportName.Text;
        var packVersion = string.IsNullOrEmpty(TextExportVersion.Text) ? "1.0.0" : TextExportVersion.Text;

        // 重复任务检查
        var loaderName = Lang.Text("Instance.Export.ExportTask.Prefix") + packName;
        foreach (var OngoingLoader in ModLoader.loaderTaskbar)
        {
            if ((OngoingLoader.name ?? "") != (loaderName ?? ""))
                continue;
            ModMain.frmMain.PageChange(FormMain.PageType.TaskManager);
            return;
        }

        // 确认导出位置
        string packPath = null;
        if (!string.IsNullOrWhiteSpace(configPackPath) && !configPackPath.EndsWithF(@"\") &&
            !configPackPath.EndsWithF("/"))
            try
            {
                Directory.CreateDirectory(ModBase.GetPathFromFullPath(configPackPath));
                packPath = configPackPath;
                ModBase.Log($"[Export] 使用配置文件中指定的导出路径：{configPackPath}");
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"无法使用配置文件中指定的导出路径（{configPackPath}）");
                if (ModMain.MyMsgBox(
                        Lang.Text("Instance.Export.PackPathInvalid.WithDetail", configPackPath, ex.ToString()),
                        Lang.Text("Instance.Export.PackPathInvalid.Title"), Lang.Text("Common.Action.Confirm"),
                        Lang.Text("Common.Action.Cancel")) == 2)
                    return;
            }

        if (packPath is null)
        {
            var extensions = new List<string>();
            if (CheckAdvancedModrinth.Checked == false)
                extensions.Add(Lang.Text("Instance.Export.ZipFilter"));
            if (SelectedLauncher == BundledLauncher.None)
                extensions.Add(Lang.Text("Instance.Export.MrpackFilter"));
            packPath = SystemDialogs.SelectSaveFile(Lang.Text("Instance.Export.SelectSaveLocation"),
                packName + (string.IsNullOrEmpty(TextExportVersion.Text) ? "" : " " + TextExportVersion.Text),
                extensions.Join("|"));
            ModBase.Log($"[Export] 手动指定的导出路径：{packPath}");
        }

        if (string.IsNullOrEmpty(packPath))
            return;

        // 缓存所需参数
        var cacheFolder = ModMain.RequestTaskTempFolder();
        var overridesFolder = Path.Combine(cacheFolder, "modpack", "overrides");
        var mcInstance = PageInstanceLeft.McInstance;
        var pathIndie = mcInstance.PathIndie;
        var checkHostedAssets = (bool)!CheckAdvancedInclude.Checked;
        var modrinthUploadMode = (bool)CheckAdvancedModrinth.Checked;
        var launcherType = SelectedLauncher;
        var includePCL = launcherType != BundledLauncher.None;
        var lightArchitecture = ComboLightArchitecture.SelectedIndex == 1 ? "ARM64" : "x64";
        var includeJava = CheckIncludeVersionJava.Checked == true;
        var instanceJava = _instanceJava;
        var isolated = ComboExportIsolation.SelectedIndex == 0;
        if (launcherType == BundledLauncher.Light && !isolated)
        {
            HintService.Hint(Lang.Text("Instance.Export.Bundle.LightIsolation"), HintType.Error);
            return;
        }
        if (includeJava && instanceJava is null)
        {
            HintService.Hint(Lang.Text("Instance.Export.Java.Missing"), HintType.Error);
            return;
        }
        if (includeJava && instanceJava is not null && launcherType == BundledLauncher.Light
            && instanceJava.Installation.Architecture != (lightArchitecture == "ARM64" ? MachineType.ARM64 : MachineType.AMD64))
        {
            HintService.Hint(Lang.Text("Instance.Export.Java.Incompatible"), HintType.Error);
            return;
        }
        var includePCLCustom = launcherType == BundledLauncher.Lite && CheckOptionsPclCustom.Checked == true;
        var allRules = StandardizeLines(GetAllRules(), true).ToList();
        var allExtraFiles = StandardizeLines(GetExtraFileLines(), false).ToList();
        ModBase.Log($"[Export] 准备导出整合包，共有 {allRules.Count} 条规则，{allExtraFiles.Count} 条追加内容行");

        // 构造步骤加载器
        var loaders = new List<ModLoader.LoaderBase>();


        if (launcherType == BundledLauncher.Lite)
            loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Instance.Export.Task.PackagePclLite"),
                _ => LauncherBundleResolver.CopyCurrentExecutable(Basics.ExecutablePath, cacheFolder))
            {
                ProgressWeight = 0.5d,
                block = false
            });

        if (launcherType == BundledLauncher.Light)
            loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Instance.Export.Bundle.DownloadLight"),
                task => ModLightBundle.Download(task, lightArchitecture, cacheFolder)) { ProgressWeight = 2d });



        loaders.Add(new ModLoader.LoaderTask<int, List<ModLocalComp.LocalCompFile>>(
            Lang.Text("Instance.Export.Task.CopyContent"), loader =>
            {
                loader.output = [];
                // 复制实例文件
                var progress = 0;
            Action<DirectoryInfo> searchFolder = null;
            searchFolder = folder =>
            {
                // 文件夹：进一步搜索
                foreach (var SubFolder in folder.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                {
                    loader.AbortedToken.ThrowIfCancellationRequested();
                    if (PackExportContent.IsInternalPath(Path.GetRelativePath(pathIndie, SubFolder.FullName))) continue;
                    if (File.Exists(Path.Combine(SubFolder.FullName, "bin", "java.exe"))) continue;
                    PackExportContent.CheckAncestors(pathIndie, SubFolder.FullName);
                    // 跳过部分又没用文件又多的文件夹，加快搜索
                    if ((folder.FullName ?? "") == (pathIndie ?? "") &&
                        new[] { "assets", "versions", "libraries" }.Contains(SubFolder.Name))
                        continue;
                    if (new[] { "structureCacheV1", ".fabric", ".git", "avatar-cache", "cosmetic-cache" }.Contains(
                            SubFolder.Name))
                        continue;
                    searchFolder(SubFolder);
                }

                // 文件：检查规则并复制
                foreach (var Entry in folder.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    loader.AbortedToken.ThrowIfCancellationRequested();
                    var relativePath = Path.GetRelativePath(pathIndie, Entry.FullName);
                    if (PackExportContent.IsInternalPath(relativePath)) continue;
                    PackExportContent.CheckAncestors(pathIndie, Entry.FullName);
                    // 检查规则
                    var shouldKeep = false;
                    foreach (var Rule in allRules)
                    {
                        var revert = Rule.StartsWith("!");
                        if (LikeString(relativePath, Rule.TrimStart('!')))
                            shouldKeep = !revert;
                    }

                    if (!shouldKeep)
                        continue;
                    var targetPath = Path.Combine(overridesFolder, relativePath);
                    ModBase.CopyFile(Entry.FullName, targetPath);
                    // 若为压缩包，考虑联网获取路径
                    if (checkHostedAssets &&
                        new[] { ".zip", ".rar", ".jar", ".disabled", ".old" }.Contains(Entry.Extension.ToLower()) &&
                        new[] { "mods", "packs", "openloader", "resource" }.Any(s => relativePath.Contains(s)))
                    {
                        var modFile = new ModLocalComp.LocalCompFile(targetPath);
                        var unused = modFile.ModrinthHash; // 提前计算 Hash
                        unused = modFile.CurseForgeHash.ToString();
                        loader.output.Add(modFile);
                    }

                    // 更新进度（进度并不准确，主要突出一个我还没似）
                    progress += 1;
                    if (progress == 25)
                    {
                        loader.Progress += (0.94d - loader.Progress) * 0.012d;
                        progress = 0;
                    }
                }
            };
            searchFolder(new DirectoryInfo(pathIndie));
            ModBase.Log($"[Export] 复制 overrides 文件完成，有 {loader.output.Count} 个文件需要联网检查");
            loader.Progress = 0.95d;
            // 复制追加内容到根目录
            var baseFolder = includePCL ? cacheFolder : Path.Combine(cacheFolder, "modpack");
            foreach (var Line in allExtraFiles)
                if (Line.EndsWithF(@"\") || Line.EndsWithF("/"))
                {
                    if (Directory.Exists(Line))
                        PackExportContent.CopyTree(Line, Path.Combine(baseFolder, ModBase.GetFolderNameFromPath(Line)), loader.AbortedToken);
                    else
                        HintService.Hint(Lang.Text("Instance.Export.Config.FolderNotFound", Line), HintType.Error);
                }
                else if (File.Exists(Line))
                {
                    if (PackExportContent.IsInternalPath(Line)) continue;
                    ModBase.CopyFile(Line, Path.Combine(baseFolder, ModBase.GetFileNameFromPath(Line)));
                }
                else
                {
                    HintService.Hint(Lang.Text("Instance.Export.Config.FileNotFound", Line), HintType.Error);
                }

            loader.Progress = 0.97d;
            // 复制 PCL 实例设置
            PackExportContent.CopyTree(Path.Combine(mcInstance.PathInstance, "PCL"), Path.Combine(overridesFolder, "PCL"), loader.AbortedToken);
            var exportedIni = Path.Combine(overridesFolder, "PCL", "Setup.ini");
            ModBase.WriteIni(exportedIni, "VersionArgumentIndie", isolated ? "1" : "2");
            ModBase.WriteIni(exportedIni, "VersionArgumentIndieV2", isolated.ToString());
            if (includeJava && instanceJava is not null)
            {
                var runtimeDestination = launcherType == BundledLauncher.Light
                    ? Path.Combine(cacheFolder, "PCL", "runtime", "java") : Path.Combine(overridesFolder, "runtime", "java");
                InstanceJavaBundler.Copy(instanceJava, mcInstance.PathInstance, runtimeDestination, loader.AbortedToken);
                JavaPreference preference = launcherType == BundledLauncher.Light
                    ? new UseRelativePath(@"PCL\runtime\java\bin\java.exe")
                    : new UseInstanceRelativePath(@"runtime\java\bin\java.exe");
                ModBase.WriteIni(exportedIni, "VersionArgumentJavaSelect", JsonSerializer.Serialize(preference, JsonCompat.SerializerOptions));
            }
            else
                ModBase.WriteIni(exportedIni, "VersionArgumentJavaSelect", JsonSerializer.Serialize<JavaPreference>(new AutoSelect(), JsonCompat.SerializerOptions));
            // 复制 PCL 个性化内容
            if (includePCLCustom)
            {
                if (Directory.Exists(Path.Combine(ModBase.exePath, "PCL", "Pictures")))
                    PackExportContent.CopyTree(Path.Combine(ModBase.exePath, "PCL", "Pictures"), Path.Combine(cacheFolder, "PCL", "Pictures"), loader.AbortedToken);
                if (Directory.Exists(Path.Combine(ModBase.exePath, "PCL", "Musics")))
                    PackExportContent.CopyTree(Path.Combine(ModBase.exePath, "PCL", "Musics"), Path.Combine(cacheFolder, "PCL", "Musics"), loader.AbortedToken);
                if (File.Exists(Path.Combine(ModBase.exePath, "PCL", "Custom.xaml")))
                    ModBase.CopyFile(Path.Combine(ModBase.exePath, "PCL", "Custom.xaml"), Path.Combine(cacheFolder, "PCL", "Custom.xaml"));
                // Global Setup.ini may contain local paths, commands or credentials; do not distribute it.
                if (File.Exists(Path.Combine(ModBase.exePath, "PCL", "hints.txt")))
                    ModBase.CopyFile(Path.Combine(ModBase.exePath, "PCL", "hints.txt"), Path.Combine(cacheFolder, "PCL", "hints.txt"));
                if (File.Exists(Path.Combine(ModBase.exePath, "PCL", "Logo.png")))
                    ModBase.CopyFile(Path.Combine(ModBase.exePath, "PCL", "Logo.png"), Path.Combine(cacheFolder, "PCL", "Logo.png"));
            }
        })
        {
            ProgressWeight = 5d
        });



        loaders.Add(
            new ModLoader.LoaderTask<List<ModLocalComp.LocalCompFile>,
                Dictionary<ModLocalComp.LocalCompFile, List<string>>>(Lang.Text("Instance.Export.Task.FetchFileInfo"),
                loader =>
                {
                    loader.output = new Dictionary<ModLocalComp.LocalCompFile, List<string>>();
                    if (!checkHostedAssets)
                    {
                        ModBase.Log("[Export] 要求跳过联网获取步骤");
                        return;
                    }

                    if (!loader.input.Any())
                    {
                        ModBase.Log("[Export] 没有需要联网检查的文件，跳过联网获取步骤");
                        return;
                    }

                    // 分平台获取下载地址
                    var endedThreadCount = 0;
                    var failedExceptions = new List<Exception>();
                    var lookupLock = new object();
                    var lookupToken = loader.AbortedToken;

                    // 从 Modrinth 获取信息
                    // 查找对应的文件
                    // 写入下载地址
                    ModBase.RunInNewThread(() =>
                    {
                        try
                        {
                            var modrinthHashes = loader.input.Select(m => m.ModrinthHash);
                            var modrinthRaw = (JsonObject)ModBase.GetJson(ModDownload.DlModRequest(
                                "https://api.modrinth.com/v2/version_files", "POST",
                                $"{{\"hashes\": [\"{modrinthHashes.Join("\",\"")}\"], \"algorithm\": \"sha1\"}}",
                                "application/json"));
                            foreach (var ModFile in loader.input)
                            {
                                if (!modrinthRaw.ContainsKey(ModFile.ModrinthHash)) continue;
                                if ((string)modrinthRaw[ModFile.ModrinthHash]?["files"]?[0]["hashes"]?["sha1"] !=
                                    ModFile.ModrinthHash) continue;
                                if (lookupToken.IsCancellationRequested) return;
                                lock (lookupLock) loader.output.AddToList(ModFile,
                                    (string)modrinthRaw[ModFile.ModrinthHash]["files"][0]["url"]);
                            }

                            ModBase.Log($"[Export] 从 Modrinth 获取到 {modrinthRaw.Count} 个本地资源项的对应信息");
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(ex, "从 Modrinth 获取本地 Mod 信息失败");
                            lock (lookupLock) failedExceptions.Add(ex);
                        }
                        finally
                        {
                            Interlocked.Increment(ref endedThreadCount);
                        }
                    }, "Modrinth - " + loaderName);

                    // 从 CurseForge 获取信息
                    // 查找对应的文件
                    // 写入下载地址
                    ModBase.RunInNewThread(() =>
                    {
                        try
                        {
                            if (modrinthUploadMode) return;
                            var curseForgeHashes = loader.input.Select(m => m.CurseForgeHash);
                            var curseForgeRaw = (JsonNode)((JsonObject)ModBase.GetJson(
                                ModDownload.DlModRequest("https://api.curseforge.com/v1/fingerprints/432/", "POST",
                                    $"{{\"fingerprints\": [{curseForgeHashes.Join(",")}]}}",
                                    "application/json")))["data"][
                                "exactMatches"];
                            foreach (JsonObject ResultJson in curseForgeRaw.AsArray())
                            {
                                if (!ResultJson.ContainsKey("file")) continue;
                                var file = (JsonObject)ResultJson["file"];
                                if (string.IsNullOrEmpty((string)file["downloadUrl"])) continue;
                                var modFile = loader.input.FirstOrDefault(m =>
                                    m.CurseForgeHash == file["fileFingerprint"].ToObject<uint>());
                                if (modFile is null) continue;
                                if (lookupToken.IsCancellationRequested) return;
                                lock (lookupLock) loader.output.AddToList(modFile,
                                    ModComp.CompFile.HandleCurseForgeDownloadUrls(file["downloadUrl"].ToString()));
                            }

                            ModBase.Log($"[Export] 从 CurseForge 获取到 {curseForgeRaw.AsArray().Count} 个本地资源项的对应信息");
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(ex, "从 CurseForge 获取本地 Mod 信息失败");
                            lock (lookupLock) failedExceptions.Add(ex);
                        }
                        finally
                        {
                            Interlocked.Increment(ref endedThreadCount);
                        }
                    }, "CurseForge - " + loaderName); // Modrinth 上传模式下，不能从 CurseForge 获取信息

                    // 等待线程结束
                    while (Volatile.Read(ref endedThreadCount) != 2)
                    {
                        lookupToken.ThrowIfCancellationRequested();
                        Thread.Sleep(10);
                    }

                    // 若失败，确认是否继续
                    if (failedExceptions.Count == 1)
                    {
                        if (ModMain.MyMsgBox(
                                Lang.Text("Instance.Export.NetCheckPartialFailed.Message"),
                                Lang.Text("Instance.Export.NetCheckPartialFailed.Title"),
                                Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel")) == 2)
                            throw failedExceptions.First();
                    }
                    else if (failedExceptions.Count > 1)
                    {
                        if (ModMain.MyMsgBox(
                                Lang.Text("Instance.Export.NetCheckAllFailed.Message"),
                                Lang.Text("Instance.Export.NetCheckAllFailed.Title"),
                                Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel")) == 2)
                            throw failedExceptions.First();
                    }
                })
            {
                show = checkHostedAssets,
                ProgressWeight = checkHostedAssets ? 2d : 0.01d
            });



        loaders.Add(new ModLoader.LoaderTask<Dictionary<ModLocalComp.LocalCompFile, List<string>>, int>(
            Lang.Text("Instance.Export.Task.CreateArchive"),
            loader =>
            {
                // 整理文件列表
                var files = new JsonArray();
                foreach (var Pair in loader.input)
                {
                    loader.AbortedToken.ThrowIfCancellationRequested();
                    var modFile = Pair.Key;
                    files.Add(new JsonObject
                    {
                        { "path", Path.GetRelativePath(overridesFolder, modFile.path).Replace(@"\", "/") },
                        {
                            "hashes",
                            new JsonObject
                            {
                                { "sha1", modFile.ModrinthHash }, { "sha512", ModBase.GetFileSHA512(modFile.path) }
                            }
                        },
                        { "downloads", new JsonArray(Pair.Value.OrderByDescending(u => u.Contains("modrinth.com")).Select(s => (JsonNode)s).ToArray()) },
                        { "fileSize", new FileInfo(modFile.path).Length }
                    });
                    File.Delete(modFile.path);
                }

                loader.Progress = 0.2d;
                // 导出最终 JSON 文件
                var dependencies = new JsonObject { { "minecraft", mcInstance.Info.VanillaName } };
                if (mcInstance.Info.HasForge)
                    dependencies.Add("forge", mcInstance.Info.Forge);
                if (mcInstance.Info.HasFabric)
                    dependencies.Add("fabric-loader", mcInstance.Info.Fabric);
                if (mcInstance.Info.HasNeoForge)
                    dependencies.Add("neoforge", mcInstance.Info.NeoForge);
                var resultJson = new JsonObject
                {
                    { "game", "minecraft" }, { "formatVersion", 1 }, { "versionId", packVersion }, { "name", packName },
                    { "summary", mcInstance.Desc }, { "files", files }, { "dependencies", dependencies }
                };
                File.WriteAllText(Path.Combine(cacheFolder, "modpack", "modrinth.index.json"),
                    resultJson.ToJsonString(new JsonSerializerOptions(JsonCompat.SerializerOptions) { WriteIndented = true }));
                // 打包
                Directory.CreateDirectory(ModBase.GetPathFromFullPath(packPath));
                if (includePCL)
                {
                    // 首次压缩整合包
                    PackExportArchive.Create(Path.Combine(cacheFolder, "modpack"), Path.Combine(cacheFolder, "modpack.mrpack"), loader.AbortedToken);
                    loader.Progress = 0.5d;
                    Directory.Delete(Path.Combine(cacheFolder, "modpack"), true);
                    loader.Progress = 0.6d;
                    // 二次压缩整合包
                    PackExportArchive.Create(cacheFolder, packPath, loader.AbortedToken);
                    loader.Progress = 0.9d;
                }
                else
                {
                    // 直接压缩整合包
                    PackExportArchive.Create(Path.Combine(cacheFolder, "modpack"), packPath, loader.AbortedToken);
                    loader.Progress = 0.8d;
                }

                try { Directory.Delete(cacheFolder, true); }
                catch (Exception ex) { ModBase.Log(ex, "导出成功，但临时目录清理失败", ModBase.LogLevel.Debug); }
                ModBase.OpenExplorer(packPath);
            })
        {
            ProgressWeight = 6d
        });


        // 启动
        var mainLoader = new ModLoader.LoaderCombo<string>(loaderName, loaders);
        mainLoader.OnStateChanged = task =>
        {
            ModDownloadLib.LoaderStateChangedHintOnly(task);
            // An aborted child may still be unwinding. Wait for every task before touching its staging files.
            if (task.State is ModBase.LoadState.Failed or ModBase.LoadState.Aborted)
                ModBase.Log("[Export] 导出未完成；保留任务临时目录供任务系统清理：" + cacheFolder, ModBase.LogLevel.Debug);
        };
        mainLoader.Start();
        ModLoader.LoaderTaskbarAdd(mainLoader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
        ModMain.frmMain.PageChange(FormMain.PageType.TaskManager);
    }



    private static bool LikeString(string input, string pattern)
    {
        pattern = pattern.Replace("#", "[0-9]");
        var options = new GlobOptions { Evaluation = { CaseInsensitive = true } };
        var glob = Glob.Parse(pattern, options);
        return glob.IsMatch(input);
    }

}
