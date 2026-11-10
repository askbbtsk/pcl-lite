using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using fNbt;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;
using static PCL.ModComp;
using static PCL.ModLoader;

namespace PCL;

public static partial class ModLocalComp
{
    private static readonly LatestBatchWorkScheduler JijWorkers = new(ModIndexService.WorkerCount);

    /// <summary>
    ///     获取文件夹描述信息。
    /// </summary>
    private static string GetFolderDescription(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
                return "空文件夹";
            return "文件夹";
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"获取文件夹描述失败：{folderPath}");
            return "文件夹";
        }
    }


    public class CompLocalLoaderData
    {
        public string compPath;
        public CompType compType;

        public KeyValuePair<List<LocalCompFile>, JsonObject> detailInfo;
        public PageInstanceCompResource frm;
        public McInstance gameVersion;
        public List<CompLoaderType> loaders;
    }


    internal static string? GetPreferredLoader(McInstance? instance)
    {
        if (instance?.Info is null) return null;
        if (instance.Info.HasNeoForge) return "NeoForge";
        if (instance.Info.HasForge || instance.Info.HasCleanroom) return "Forge";
        if (instance.Info.HasFabric) return "Fabric";
        if (instance.Info.HasQuilt) return "Quilt";
        return null;
    }


    // Phase 2：列表出现后在后台线程补解析被延后的内嵌树，完成后清理+落盘缓存
    private static void _ResolveJijPendingBackground(List<LocalCompFile> pending, List<string> allModPaths,
        string instancePath, int loaderTaskId, int generation)
    {
        ModJarInJarCache.UseInstance(instancePath); // [ThreadStatic]：后台线程须重新注册目标实例缓存
        bool IsObsolete() => generation != Volatile.Read(ref _jijGeneration)
            || compResourceListLoader.IsAbortedWithThread(loaderTaskId);
        var allResolved = false;
        ModJarInJar.ScanContext? sharedScan = null;
        var scanGate = new object();
        try
        {
            if (IsObsolete()) return;
            JijWorkers.RunAsync(pending, (mod, token) =>
            {
                if (IsObsolete()) return;
                // ThreadStatic state must be bound on each worker, never inherited from the coordinator.
                ModJarInJarCache.UseInstance(instancePath);
                lock (scanGate)
                    JijScanContext = sharedScan ??= new ModJarInJar.ScanContext(() => token.IsCancellationRequested || IsObsolete());
                try { ModIndexService.RunWorker(() => mod.ResolveJijNow(), token); }
                finally { JijScanContext = null; ModJarInJarCache.UseInstance(null); }
            }, canStart: () => !IsObsolete()).GetAwaiter().GetResult();

            if (IsObsolete()) return;
            // 个别文件解析失败时保留其 pending 状态与旧缓存条目，等待下一次强制刷新重试。
            allResolved = pending.All(m => !m.JijPending);
            if (allResolved) ModJarInJarCache.Prune(allModPaths);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ModBase.Log(ex, "Jar-in-Jar 后台补解析失败", ModBase.LogLevel.Developer);
        }
        finally
        {
            // 中断/失败时也保存此前已完整解析的条目；不执行 Prune，避免删掉尚未扫描的旧缓存。
            ModJarInJarCache.FlushCurrent();
            JijScanContext = null;
            ModJarInJarCache.UseInstance(null);
            if (generation == Volatile.Read(ref _jijGeneration))
            {
                CompJijResolved = allResolved;
                // 内嵌解析状态变化后，若关系页正显示则刷新；失败时仍保留“未完成”保护。
                ModBase.RunInUi(() =>
                {
                    if (generation == Volatile.Read(ref _jijGeneration)) ModMain.frmInstanceModJarInJar?.OnJijResolved();
                });
            }
        }
    }


    private static void CompResourceListLoad(LoaderTask<CompLocalLoaderData, List<LocalCompFile>> loader)
    {
        try
        {
            ModBase.RunInUiWait(() =>
            {
                if (loader.input.frm is not null) loader.input.frm.Load.ShowProgress = false;
            });

            // 等待 Mod 更新完成
            if (PageInstanceCompResource.updatingVersions.Contains(loader.input.compPath))
            {
                ModBase.Log("[Mod] 等待资源更新完成后才能继续加载资源列表：" + loader.input.compPath);
                try
                {
                    ModBase.RunInUiWait(() =>
                    {
                        if (loader.input.frm is not null)
                            loader.input.frm.Load.Text = Lang.Text("Instance.Resource.Update.WaitingForUpdate");
                    });
                    while (PageInstanceCompResource.updatingVersions.Contains(loader.input.compPath))
                    {
                        if (loader.IsAborted)
                            return;
                        Thread.Sleep(100);
                    }
                }
                finally
                {
                    ModBase.RunInUiWait(() =>
                    {
                        if (loader.input.frm is not null)
                            loader.input.frm.Load.Text = Lang.Text("Instance.Resource.List.Loading");
                    });
                }

                loader.input.frm.LoaderRun(LoaderFolderRunType.UpdateOnly);
            }

            // 获取 Mod 文件夹下的可用文件列表
            var modList = new List<LocalCompFile>();
            if (Directory.Exists(loader.input.compPath))
            {
                var rawName = loader.input.compPath.ToLower();

                if (loader.input.compType == CompType.Schematic)
                {
                    var currentFolderPath = "";
                    if (loader.input.frm is not null) currentFolderPath = loader.input.frm.CurrentFolderPath;

                    var searchPath = string.IsNullOrEmpty(currentFolderPath)
                        ? loader.input.compPath
                        : currentFolderPath;

                    try
                    {
                        var dirInfo = new DirectoryInfo(searchPath);
                        foreach (var Dir in dirInfo.EnumerateDirectories("*", SearchOption.AllDirectories))
                            modList.Add(new LocalCompFile(Path.Combine(Dir.FullName, "__FOLDER__")));
                        foreach (var File in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                            try
                            {
                                if (LocalCompFile.IsCompFile(File.FullName, loader.input.compType))
                                    modList.Add(new LocalCompFile(File.FullName));
                            }
                            catch (Exception ex)
                            {
                                ModBase.Log(ex, $"处理文件失败：{File.FullName}");
                            }
                    }
                    catch (Exception ex)
                    {
                        ModBase.Log(ex, $"枚举文件失败：{searchPath}");
                    }
                }
                else
                {
                    try
                    {
                        foreach (var File in ModBase.EnumerateFiles(loader.input.compPath))
                            try
                            {
                                if ((File.DirectoryName.ToLower() ?? "") != (rawName.TrimEnd('\\') ?? ""))
                                    if (!(PageInstanceLeft.McInstance is not null &&
                                          PageInstanceLeft.McInstance.Info.HasForge &&
                                          PageInstanceLeft.McInstance.Info.Drop < 130 && (File.Directory.Name ?? "") ==
                                          (PageInstanceLeft.McInstance.Info.VanillaName ?? "")))
                                        continue;

                                if (LocalCompFile.IsCompFile(File.FullName, loader.input.compType))
                                    modList.Add(new LocalCompFile(File.FullName));
                            }
                            catch (Exception ex)
                            {
                                ModBase.Log(ex, $"处理文件失败：{File.FullName}");
                            }
                    }
                    catch (Exception ex)
                    {
                        ModBase.Log(ex, $"枚举文件夹失败：{loader.input.compPath}");
                    }
                }
            }

            // 确定是否显示进度
            loader.Progress = 0.05d;
            if (modList.Count > 50)
                ModBase.RunInUi(() =>
                {
                    if (loader.input.frm is not null) loader.input.frm.Load.ShowProgress = true;
                });

            // 获取本地文件缓存
            var cachePath = ModBase.pathTemp + @"Cache\LocalComp.json";
            var cache = new JsonObject();
            try
            {
                var cacheContent = ModBase.ReadFile(cachePath);
                if (!string.IsNullOrWhiteSpace(cacheContent))
                {
                    cache = (JsonObject)ModBase.GetJson(cacheContent);
                    if (!cache.ContainsKey("version") || cache["version"].ToObject<int>() != localModCacheVersion)
                    {
                        ModBase.Log("[Mod] 本地 Mod 信息缓存版本已过期，将弃用这些缓存信息", ModBase.LogLevel.Debug);
                        cache = new JsonObject();
                    }
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "读取本地 Mod 信息缓存失败，已重置");
                cache = new JsonObject();
            }

            cache["version"] = localModCacheVersion;

            // 加载 Mod 列表 - 优化：对于原理图文件，延迟加载NBT数据
            var modUpdateList = new List<LocalCompFile>();
            // 仅 Mod 类型启用 Jar-in-Jar 缓存；资源包/光影/投影共用同一 loader，不能用其路径污染/剪裁 mod 缓存
            var jijEnabled = loader.input.compType == CompType.Mod;
            var jijInstancePath = jijEnabled ? loader.input.gameVersion.PathInstance : null;
            var preferredLoader = jijEnabled ? GetPreferredLoader(loader.input.gameVersion) : null;
            var jijGeneration = 0;
            Action? startJijResolve = null;
            ModJarInJarCache.UseInstance(jijInstancePath);
            // Phase 1：缓存未命中的内嵌解析延后到后台，让列表尽快出现（完成前禁用/删除级联会提示不完整）
            if (jijEnabled)
            {
                ModIndexService.Register(loader.input.gameVersion);
                jijGeneration = Interlocked.Increment(ref _jijGeneration);
                JijWorkers.CancelObsolete();
                CompJijResolved = false;
                DeferJijOnMiss = true;
            }

            // Bind ThreadStatic context on every worker. Browsing reads metadata and cached trees,
            // while expensive nested archive decoding remains an explicit operation.
            if (jijEnabled)
                Parallel.ForEach(modList.Where(mod => !mod.IsFolder),
                    new ParallelOptions { MaxDegreeOfParallelism = ModIndexService.WorkerCount }, mod =>
                    {
                        if (loader.IsAborted) return;
                        ModJarInJarCache.UseInstance(jijInstancePath);
                        DeferJijOnMiss = true;
                        try
                        {
                            mod.PreferredLoader = preferredLoader;
                            mod.Load();
                            if (mod.State != LocalCompFile.LocalFileStatus.Unavailable)
                                _ = mod.ModrinthHash;
                        }
                        finally { DeferJijOnMiss = false; ModJarInJarCache.UseInstance(null); }
                    });

            foreach (var ModEntry in modList)
            {
                loader.Progress += 0.94d / modList.Count;
                if (loader.IsAborted)
                    return;
                if (ModEntry.IsFolder)
                    continue;

                if (jijEnabled) ModEntry.PreferredLoader = preferredLoader;
                // 优化：对于原理图文件，只进行基础加载，不解析NBT数据
                if (loader.input.compType == CompType.Schematic)
                    ModEntry.LoadBasicInfo();
                else
                    // 加载 McMod 对象
                    ModEntry.Load();
                
                // 读取 Comp 缓存
                if (ModEntry.State == LocalCompFile.LocalFileStatus.Unavailable)
                    continue;
                var cacheKey = ModEntry.ModrinthHash + loader.input.gameVersion.Info.VanillaName +
                               loader.input.loaders.Join("");
                if (cache.ContainsKey(cacheKey))
                {
                    ModEntry.FromJson((JsonObject)cache[cacheKey]);
                    // 如果缓存中的信息在 6 小时以内更新过，则无需重新获取
                    if (ModEntry.compLoaded &&
                        DateTime.Now - cache[cacheKey]["Comp"]["CacheTime"].ToObject<DateTime>() <
                        new TimeSpan(6, 0, 0))
                        continue;
                }

                modUpdateList.Add(ModEntry);
            }

            if (jijEnabled)
            {
                DeferJijOnMiss = false; // Phase 1 结束，后续（如 UI 惰性）Load 恢复即时解析
                var allModPaths = modList.Where(m => !m.IsFolder).Select(m => m.path).ToList();
                var pending = modList.Where(m => !m.IsFolder && m.JijPending).ToList();
                if (pending.Count == 0)
                {
                    // 全部命中缓存：无需后台补解析，直接清理+落盘
                    ModJarInJarCache.Prune(allModPaths);
                    ModJarInJarCache.FlushCurrent();
                    if (jijGeneration == Volatile.Read(ref _jijGeneration)) CompJijResolved = true;
                }
                else if (ModIndexService.Policy.ConsumeDownloadRequest(loader.input.compPath))
                {
                    var loaderTaskId = Task.CurrentId ?? -1;
                    startJijResolve = () =>
                        _ResolveJijPendingBackground(pending, allModPaths, jijInstancePath, loaderTaskId,
                            jijGeneration);
                }
            }

            loader.Progress = 0.99d;
            ModBase.Log(
                $"[Mod] 共有 {modList.Count} 个 Mod，其中 {modUpdateList.Where(m => m.Comp is null).Count()} 个需要联网获取信息，{modUpdateList.Where(m => m.Comp is not null).Count()} 个需要更新信息");

            // 排序
            modList.Sort((left, right) =>
            {
                if (left.State == LocalCompFile.LocalFileStatus.Unavailable !=
                    (right.State == LocalCompFile.LocalFileStatus.Unavailable))
                    return left.State == LocalCompFile.LocalFileStatus.Unavailable ? 1 : -1;

                return right.FileName.CompareTo(left.FileName);
            });

            // 回设
            if (loader.IsAborted)
                return;
            loader.output = modList;
            if (startJijResolve is not null)
                ModBase.RunInNewThread(startJijResolve, "Jar-in-Jar 后台解析");

            // 开始联网加载
            if (modUpdateList.Any())
            {
                // TODO: 添加信息获取中提示
                loader.input.detailInfo = new KeyValuePair<List<LocalCompFile>, JsonObject>(modUpdateList, cache);
                compUpdateDetailLoader.Start(loader.input, true);
            }
        }

        catch (Exception ex)
        {
            ModBase.Log(ex, "Mod 列表加载失败");
            throw;
        }
        finally
        {
            // 复位线程的"当前实例"：本 run 在线程池线程上执行，不复位会让后续复用此线程的
            // 其它工作项（下载依赖检查等触发 Load 的路径）把别实例的条目写进本实例缓存
            ModJarInJarCache.UseInstance(null);
            // 中途 IsAborted/异常 return 时也复位延后标记，避免残留污染线程池复用线程上的后续 Load
            DeferJijOnMiss = false;
        }
    }


    private static void CompUpdateDetailLoad(LoaderTask<CompLocalLoaderData, int> loader)
    {
        var mods = loader.input.detailInfo.Key;
        var cache = loader.input.detailInfo.Value;
        // 获取作为检查目标的加载器和版本
        var modLoaders = loader.input.loaders;
        var compType = loader.input.compType;
        var mcInstance = loader.input.gameVersion.Info.VanillaName;

        // 开始网络获取
        ModBase.Log($"[Mod] 目标加载器：{string.Join("/", modLoaders)}，版本：{mcInstance}");
        var endedThreadCount = 0;
        var isFailed = false;
        var currentTaskId = Task.CurrentId ?? -1;

        // 从 Modrinth 获取信息
        ModBase.RunInNewThread(() =>
        {
            try
            {
                // 步骤 1：获取 Hash 与对应的工程 ID
                var modrinthHashes = mods.Select(m => m.ModrinthHash).ToList();
                var modrinthVersion = (JsonObject)ModBase.GetJson(ModDownload.DlModRequest(
                    "https://api.modrinth.com/v2/version_files", "POST",
                    $"{{\"hashes\": [\"{string.Join("\",\"", modrinthHashes)}\"], \"algorithm\": \"sha1\"}}",
                    "application/json"));
                ModBase.Log($"[Mod] 从 Modrinth 获取到 {modrinthVersion.Count} 个本地 Mod 的对应信息");

                // 步骤 2：尝试读取工程信息缓存，构建其他 Mod 的对应关系
                if (modrinthVersion.Count == 0) return;
                var modrinthMapping = new Dictionary<string, List<LocalCompFile>>();
                foreach (var Entry in mods)
                {
                    if (modrinthVersion[Entry.ModrinthHash] is null) continue;
                    if (modrinthVersion[Entry.ModrinthHash]["files"][0]["hashes"]["sha1"].ToString() !=
                        Entry.ModrinthHash) continue;

                    var projectId = modrinthVersion[Entry.ModrinthHash]["project_id"].ToString();
                    // 读取已加载的缓存，加快结果出现速度
                    if (compProjectCache.ContainsKey(projectId) && Entry.Comp is null)
                        Entry.Comp = compProjectCache[projectId];

                    if (!modrinthMapping.ContainsKey(projectId)) modrinthMapping[projectId] = new List<LocalCompFile>();
                    modrinthMapping[projectId].Add(Entry);

                    // 记录对应的 CompFile
                    var fileInfo = new CompFile((JsonObject)modrinthVersion[Entry.ModrinthHash], CompType.Mod);
                    if (Entry.compFile is null || Entry.compFile.ReleaseDate < fileInfo.ReleaseDate)
                        Entry.compFile = fileInfo;
                }

                if (loader.IsAbortedWithThread(currentTaskId)) return;
                ModBase.Log($"[Mod] 需要从 Modrinth 获取 {modrinthMapping.Count} 个本地 Mod 的工程信息");

                // 步骤 3：获取工程信息
                if (!modrinthMapping.Any()) return;
                var modrinthProject = (JsonArray)ModBase.GetJson(ModDownload.DlModRequest(
                    $"https://api.modrinth.com/v2/projects?ids=[\"{string.Join("\",\"", modrinthMapping.Keys)}\"]",
                    "GET", "", "application/json"));

                foreach (var ProjectJson in modrinthProject)
                {
                    var project = new CompProject((JsonObject)ProjectJson);
                    foreach (var Entry in modrinthMapping[project.Id]) Entry.Comp = project;
                }

                ModBase.Log("[Mod] 已从 Modrinth 获取本地 Mod 信息，继续获取更新信息");

                // 步骤 4：获取更新信息
                var targetLoaders = compType == CompType.DataPack
                    ? "datapack"
                    : string.Join("\",\"", modLoaders).ToLower();
                var modrinthUpdate = (JsonObject)ModBase.GetJson(ModDownload.DlModRequest(
                    "https://api.modrinth.com/v2/version_files/update", "POST",
                    $"{{\"hashes\": [\"{string.Join("\",\"", modrinthMapping.SelectMany(l => l.Value.Select(m => m.ModrinthHash)))}\"], \"algorithm\": \"sha1\", " +
                    $"\"loaders\": [\"{targetLoaders}\"],\"game_versions\": [\"{mcInstance}\"]}}", "application/json"));

                foreach (var Entry in mods)
                {
                    if (modrinthUpdate[Entry.ModrinthHash] is null || Entry.compFile is null) continue;
                    var updateFile = new CompFile((JsonObject)modrinthUpdate[Entry.ModrinthHash], CompType.Mod);
                    if (!updateFile.Available) continue;

                    if (ModBase.modeDebug)
                        ModBase.Log($"[Mod] 本地文件 {Entry.compFile.FileName} 在 Modrinth 上的最新版为 {updateFile.FileName}");
                    if (Entry.compFile.ReleaseDate >= updateFile.ReleaseDate ||
                        Entry.compFile.Hash == updateFile.Hash) continue;

                    // 设置更新日志与更新文件
                    if (Entry.UpdateFile is not null && updateFile.Hash == Entry.UpdateFile.Hash)
                    {
                        Entry.changelogUrls.Add(
                            $"https://modrinth.com/mod/{modrinthUpdate[Entry.ModrinthHash]["project_id"]}/changelog?g={mcInstance}");
                        Entry.UpdateFile.DownloadUrls.AddRange(updateFile.DownloadUrls);
                        Entry.UpdateFile = updateFile;
                    }
                    else if (Entry.UpdateFile is null || updateFile.ReleaseDate >= Entry.UpdateFile.ReleaseDate)
                    {
                        Entry.changelogUrls = new List<string>
                        {
                            $"https://modrinth.com/mod/{modrinthUpdate[Entry.ModrinthHash]["project_id"]}/changelog?g={mcInstance}"
                        };
                        Entry.UpdateFile = updateFile;
                    }
                }

                ModBase.Log("[Mod] 从 Modrinth 获取本地 Mod 信息结束");
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "从 Modrinth 获取本地 Mod 信息失败");
                isFailed = true;
            }
            finally
            {
                Interlocked.Increment(ref endedThreadCount);
            }
        }, "Mod List Detail Loader Modrinth");

        // 从 CurseForge 获取信息（工程 ID 与文件 ID 均为数字）
        ModBase.RunInNewThread(() =>
        {
            try
            {
                // 步骤 1：获取 Hash 与对应的工程信息
                var curseForgeHashes = new List<uint>();
                foreach (var entry in mods)
                {
                    curseForgeHashes.Add(entry.CurseForgeHash);
                    if (loader.IsAbortedWithThread(currentTaskId)) return;
                }

                var curseForgeResponse = (JsonObject)ModBase.GetJson(ModDownload.DlModRequest(
                    "https://api.curseforge.com/v1/fingerprints/432", "POST",
                    $"{{\"fingerprints\": [{string.Join(",", curseForgeHashes)}]}}", "application/json"));
                var curseForgeRaw = (JsonArray)curseForgeResponse["data"]["exactMatches"];
                ModBase.Log($"[Mod] 从 CurseForge 获取到 {curseForgeRaw.Count} 个本地 Mod 的对应信息");

                // 步骤 2：尝试读取工程信息缓存，构建本地文件与工程 ID 的对应关系
                if (!curseForgeRaw.Any()) return;
                var curseForgeMapping = new Dictionary<string, List<LocalCompFile>>();
                foreach (var project in curseForgeRaw)
                {
                    var projectId = project["id"].ToString();
                    var hash = project["file"]["fileFingerprint"].ToObject<uint>();
                    foreach (var Entry in mods)
                    {
                        if (Entry.CurseForgeHash != hash) continue;
                        // 读取已加载的缓存，加快结果出现速度
                        if (compProjectCache.ContainsKey(projectId) && Entry.Comp is null)
                            Entry.Comp = compProjectCache[projectId];

                        if (!curseForgeMapping.ContainsKey(projectId))
                            curseForgeMapping[projectId] = new List<LocalCompFile>();
                        curseForgeMapping[projectId].Add(Entry);

                        // 记录对应的 CompFile
                        var fileInfo = new CompFile((JsonObject)project["file"], CompType.Mod);
                        if (Entry.compFile is null || Entry.compFile.ReleaseDate < fileInfo.ReleaseDate)
                            Entry.compFile = fileInfo;
                    }
                }

                if (loader.IsAbortedWithThread(currentTaskId)) return;
                ModBase.Log($"[Mod] 需要从 CurseForge 获取 {curseForgeMapping.Count} 个本地 Mod 的工程信息");

                // 步骤 3：获取工程信息，同时归纳需要检查更新的文件 ID
                if (!curseForgeMapping.Any()) return;
                var curseForgeProject = (JsonArray)((JsonObject)ModBase.GetJson(ModDownload.DlModRequest(
                    "https://api.curseforge.com/v1/mods", "POST",
                    $"{{\"modIds\": [{string.Join(",", curseForgeMapping.Keys)}]}}", "application/json")))["data"];

                var updateFileIds = new Dictionary<int, List<LocalCompFile>>(); // FileId -> 本地 Mod 文件列表
                var fileIdToProjectSlug = new Dictionary<int, string>();
                foreach (var projectJson in curseForgeProject)
                {
                    if (projectJson["isAvailable"] is not null && !projectJson["isAvailable"].ToObject<bool>())
                        continue;

                    // 设置 Entry 中的工程信息
                    var project = new CompProject((JsonObject)projectJson);
                    if (!curseForgeMapping.ContainsKey(project.Id)) continue;
                    foreach (var Entry in curseForgeMapping[project.Id])
                    {
                        // 若已从 Modrinth 获取到工程信息，则保留它，仅再次触发修改事件以刷新 UI
                        if (Entry.Comp is not null && !Entry.Comp.FromCurseForge)
                        {
                            var existing = Entry.Comp;
                            Entry.Comp = existing;
                            continue;
                        }

                        Entry.Comp = project;
                    }

                    // 仅在加载器唯一时查找可能有更新的文件
                    if (modLoaders.Count != 1) continue;
                    string newestVersion = null;
                    var newestFileIds = new List<int>();
                    foreach (var indexEntry in (JsonArray)projectJson["latestFilesIndexes"])
                    {
                        // 加载器唯一且匹配
                        if (compType != CompType.DataPack &&
                            (indexEntry["modLoader"] is null ||
                             (int)modLoaders.Single() != indexEntry["modLoader"].ToObject<int>()))
                            continue;

                        var indexVersion = indexEntry["gameVersion"].ToString();
                        if (indexVersion != mcInstance) continue; // MC 版本匹配
                        // latestFilesIndexes 按时间从新到老排序，只保留最新的 MC 版本
                        if (newestVersion is not null &&
                            McVersionComparer.CompareVersion(newestVersion, indexVersion) > -1)
                            continue;

                        if (newestVersion != indexVersion)
                        {
                            newestVersion = indexVersion;
                            newestFileIds.Clear();
                        }

                        newestFileIds.Add(indexEntry["fileId"].ToObject<int>());
                    }

                    foreach (var fileId in newestFileIds)
                    {
                        if (!updateFileIds.ContainsKey(fileId)) updateFileIds[fileId] = new List<LocalCompFile>();
                        updateFileIds[fileId].AddRange(curseForgeMapping[project.Id]);
                        fileIdToProjectSlug[fileId] = project.Slug;
                    }
                }

                if (loader.IsAbortedWithThread(currentTaskId)) return;
                ModBase.Log(
                    $"[Mod] 已从 CurseForge 获取本地 Mod 信息，需要获取 {updateFileIds.Count} 个用于检查更新的文件信息");

                // 步骤 4：获取更新文件信息
                if (!updateFileIds.Any()) return;
                var curseForgeFiles = (JsonArray)((JsonObject)ModBase.GetJson(ModDownload.DlModRequest(
                    "https://api.curseforge.com/v1/mods/files", "POST",
                    $"{{\"fileIds\": [{string.Join(",", updateFileIds.Keys)}]}}", "application/json")))["data"];

                var updateFiles = new Dictionary<LocalCompFile, CompFile>();
                foreach (var fileJson in curseForgeFiles)
                {
                    var updateFile = new CompFile((JsonObject)fileJson, CompType.Mod);
                    if (!updateFile.Available) continue;
                    if (!int.TryParse(updateFile.Id, out var fileId) || !updateFileIds.ContainsKey(fileId))
                        continue;
                    foreach (var Entry in updateFileIds[fileId])
                    {
                        if (updateFiles.ContainsKey(Entry) && updateFiles[Entry].ReleaseDate >= updateFile.ReleaseDate)
                            continue;
                        updateFiles[Entry] = updateFile;
                    }
                }

                foreach (var pair in updateFiles)
                {
                    var Entry = pair.Key;
                    var updateFile = pair.Value;
                    if (Entry.compFile is null) continue;
                    if (ModBase.modeDebug)
                        ModBase.Log(
                            $"[Mod] 本地文件 {Entry.compFile.FileName} 在 CurseForge 上的最新版为 {updateFile.FileName}");
                    if (Entry.compFile.ReleaseDate >= updateFile.ReleaseDate ||
                        Entry.compFile.Hash == updateFile.Hash) continue;

                    var changelogUrl =
                        $"https://www.curseforge.com/minecraft/mc-mods/{fileIdToProjectSlug[int.Parse(updateFile.Id)]}/files/{updateFile.Id}";

                    // 设置更新日志与更新文件
                    if (Entry.UpdateFile is not null && updateFile.Hash == Entry.UpdateFile.Hash)
                    {
                        // 合并下载源
                        Entry.changelogUrls.Add(changelogUrl);
                        Entry.UpdateFile.DownloadUrls.AddRange(updateFile.DownloadUrls);
                    }
                    else if (Entry.UpdateFile is null || updateFile.ReleaseDate > Entry.UpdateFile.ReleaseDate)
                    {
                        // 替换
                        Entry.changelogUrls = new List<string> { changelogUrl };
                        Entry.UpdateFile = updateFile;
                    }
                }

                ModBase.Log("[Mod] 从 CurseForge 获取 Mod 更新信息结束");
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "从 CurseForge 获取本地 Mod 信息失败");
                isFailed = true;
            }
            finally
            {
                Interlocked.Increment(ref endedThreadCount);
            }
        }, "Mod List Detail Loader CurseForge");

        // 等待线程结束
        while (endedThreadCount < 2)
        {
            if (loader.IsAborted) return;
            Thread.Sleep(10);
        }

        // 保存缓存
        var cachedMods = mods.Where(m => m.Comp is not null).ToList();
        ModBase.Log($"[Mod] 联网获取本地 Mod 信息完成，为 {cachedMods.Count} 个 Mod 更新缓存");
        if (!cachedMods.Any()) return;

        foreach (var Entry in cachedMods)
        {
            Entry.compLoaded = !isFailed;
            cache[Entry.ModrinthHash + mcInstance + string.Join("", modLoaders)] = Entry.ToJson();
        }

        ModBase.WriteFile(Path.Combine(ModBase.pathTemp, "Cache", "LocalComp.json"),
            cache.ToJsonString(ModBase.modeDebug ? new JsonSerializerOptions(JsonCompat.SerializerOptions) { WriteIndented = true } : null));

        // 刷新 UI
        ModBase.RunInUi(() =>
        {
            if (ModMain.frmInstanceMod?.Filter == PageInstanceCompResource.FilterType.CanUpdate)
                ModMain.frmInstanceMod?.RefreshUI();
            else
                ModMain.frmInstanceMod?.RefreshBars();
        });
    }


    public static List<CompLoaderType> GetCurrentVersionModLoader()
    {
        var modLoaders = new List<CompLoaderType>();
        if (PageInstanceLeft.McInstance.Info.HasForge)
            modLoaders.Add(CompLoaderType.Forge);
        if (PageInstanceLeft.McInstance.Info.HasNeoForge)
            modLoaders.Add(CompLoaderType.NeoForge);
        if (PageInstanceLeft.McInstance.Info.HasFabric)
            modLoaders.Add(CompLoaderType.Fabric);
        if (PageInstanceLeft.McInstance.Info.HasQuilt)
            modLoaders.AddRange(new[] { CompLoaderType.Fabric, CompLoaderType.Quilt });
        if (PageInstanceLeft.McInstance.Info.HasLiteLoader)
            modLoaders.Add(CompLoaderType.LiteLoader);
        if (!modLoaders.Any())
            modLoaders.AddRange(new[]
            {
                CompLoaderType.Forge, CompLoaderType.NeoForge, CompLoaderType.Fabric, CompLoaderType.LiteLoader,
                CompLoaderType.Quilt
            });
        return modLoaders;
    }

}
