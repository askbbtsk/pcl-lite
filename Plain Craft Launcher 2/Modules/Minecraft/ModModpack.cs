using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.Backups;
using PCL.Core.UI;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.ModLoader;
using PCL.Core.Utils;

namespace PCL;

public static class ModModpack
{
    // 触发整合包安装的外部接口
    /// <summary>
    ///     弹窗要求选择一个整合包文件并进行安装。
    /// </summary>
    public static void ModpackInstall()
    {
        if (!RestrictedFeaturePolicy.Check("modpack-install"))
            return;
        var file = SystemDialogs.SelectFile(Lang.Text("Minecraft.Download.Modpack.FileDialog.Filter"),
            Lang.Text("Minecraft.Download.Modpack.FileDialog.Title")); // 选择整合包文件
        if (string.IsNullOrEmpty(file))
            return;
        ModBase.RunInThread(() =>
        {
            try
            {
                ModpackInstall(file);
            }
            catch (ModBase.CancelledException)
            {
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "手动安装整合包失败",
                    ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Minecraft.Download.Modpack.Error.OperationFailed"));
            }
        });
    }

    /// <summary>
    ///     构建并启动安装给定的整合包文件的加载器，并返回该加载器。若失败则抛出异常。
    ///     必须在工作线程执行。
    /// </summary>
    /// <exception cref="ModBase.CancelledException" />
    public static LoaderCombo<string> ModpackInstall(string file, string instanceName = null, string logo = null,
        string resourceId = null, bool isOnlineInstall = false)
    {
        RestrictedFeaturePolicy.ThrowIfRestricted("modpack-install");
        ModBase.Log("[ModPack] 整合包安装请求：" + (file ?? "null"));
        ZipArchive archive = null;
        var archiveBaseFolder = "";
        try
        {
            // 字符校验
            var targetFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\";
            if (targetFolder.Contains("!") || targetFolder.Contains(";"))
            {
                HintService.Hint(Lang.Text("Minecraft.Download.Modpack.InvalidGamePathChars", targetFolder),
                    HintType.Error);
                throw new ModBase.CancelledException();
            }

            // 获取整合包种类与关键 Json
            var packType = -1;
            do
            {
                try
                {
                    archive = new ZipArchive(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
                    if (archive.Entries.Any(e => e.IsEncrypted))
                        throw new Exception(Lang.Text("Minecraft.Download.Modpack.EncryptedArchiveUnsupported"));
                    // 从根目录判断整合包类型
                    if (archive.GetEntry("mcbbs.packmeta") is not null)
                    {
                        packType = 3;
                        break;
                    } // MCBBS 整合包（优先于 manifest.json 判断）

                    if (archive.GetEntry("mmc-pack.json") is not null)
                    {
                        packType = 2;
                        break;
                    } // MMC 整合包（优先于 manifest.json 判断，#4194）

                    if (archive.GetEntry("modrinth.index.json") is not null)
                    {
                        packType = 4;
                        break;
                    } // Modrinth 整合包

                    if (archive.GetEntry("manifest.json") is not null)
                    {
                        var json = (JsonObject)ModBase.GetJson(ModBase.ReadFile(archive.GetEntry("manifest.json").Open(),
                            Encoding.UTF8));
                        if (json["addons"] is null)
                        {
                            packType = 0;
                            break; // CurseForge 整合包
                        }

                        packType = 3;
                        break;
                        // MCBBS 整合包
                    }

                    if (archive.GetEntry("modpack.json") is not null)
                    {
                        packType = 1;
                        break;
                    } // HMCL 整合包

                    if (archive.GetEntry("modpack.zip") is not null || archive.GetEntry("modpack.mrpack") is not null)
                    {
                        packType = 9;
                        break;
                    } // 带启动器的压缩包

                    // 从一级目录判断整合包类型
                    var exitTry = false;
                    foreach (var Entry in archive.Entries)
                    {
                        var fullNames = Entry.FullName.Split("/");
                        archiveBaseFolder = fullNames[0] + "/";
                        // 确定为一级目录下
                        if (fullNames.Count() != 2)
                            continue;
                        // 判断是否为关键文件
                        if (fullNames[1] == "mcbbs.packmeta")
                        {
                            packType = 3;
                            exitTry = true;
                            break;
                        } // MCBBS 整合包（优先于 manifest.json 判断）

                        if (fullNames[1] == "mmc-pack.json")
                        {
                            packType = 2;
                            exitTry = true;
                            break;
                        } // MMC 整合包（优先于 manifest.json 判断，#4194）

                        if (fullNames[1] == "modrinth.index.json")
                        {
                            packType = 4;
                            exitTry = true;
                            break;
                        } // Modrinth 整合包

                        if (fullNames[1] == "manifest.json")
                        {
                            var json = (JsonObject)ModBase.GetJson(ModBase.ReadFile(Entry.Open(), Encoding.UTF8));
                            if (json["addons"] is null)
                            {
                                packType = 0;
                                exitTry = true;
                                break; // CurseForge 整合包
                            }

                            packType = 3;
                            archiveBaseFolder = "overrides/";
                            exitTry = true;
                            break;
                            // MCBBS 整合包
                        }

                        if (fullNames[1] == "modpack.json")
                        {
                            packType = 1;
                            exitTry = true;
                            break;
                        } // HMCL 整合包

                        if (fullNames[1] == "modpack.zip" || fullNames[1] == "modpack.mrpack")
                        {
                            packType = 9;
                            exitTry = true;
                            break;
                        } // 带启动器的压缩包
                    }

                    if (exitTry) break;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("Error.WinIOError"))
                        throw new Exception(Lang.Text("Minecraft.Download.Modpack.OpenFailed"), ex);
                    else if (file.EndsWithF(".rar", true))
                        throw new Exception(Lang.Text("Minecraft.Download.Modpack.RarUnsupported"), ex);
                    else
                        throw new Exception(Lang.Text("Minecraft.Download.Modpack.UnsupportedArchive"), ex);
                }
            } while (false);

            // 执行对应的安装方法
            switch (packType)
            {
                case 0:
                {
                    ModBase.Log("[ModPack] 整合包种类：CurseForge");
                    return InstallPackCurseForge(file, archive, archiveBaseFolder, instanceName, logo, resourceId,
                        isOnlineInstall);
                }
                case 1:
                {
                    ModBase.Log("[ModPack] 整合包种类：HMCL");
                    return InstallPackHMCL(file, archive, archiveBaseFolder);
                }
                case 2:
                {
                    ModBase.Log("[ModPack] 整合包种类：MMC");
                    return InstallPackMMC(file, archive, archiveBaseFolder);
                }
                case 3:
                {
                    ModBase.Log("[ModPack] 整合包种类：MCBBS");
                    return InstallPackMCBBS(file, archive, archiveBaseFolder, instanceName);
                }
                case 4:
                {
                    ModBase.Log("[ModPack] 整合包种类：Modrinth");
                    return InstallPackModrinth(file, archive, archiveBaseFolder, instanceName, logo, resourceId,
                        isOnlineInstall);
                }
                case 9:
                {
                    ModBase.Log("[ModPack] 整合包种类：带启动器的压缩包");
                    return InstallPackLauncherPack(file, archive, archiveBaseFolder);
                }

                default:
                {
                    ModBase.Log("[ModPack] 整合包种类：未能识别，假定为压缩包");
                    return InstallPackCompress(file, archive);
                }
            }
        }
        finally
        {
            if (archive is not null)
                archive.Dispose();
        }
    }

    private static void ExtractModpackFiles(string installTemp, string fileAddress, LoaderBase loader,
        double progressIncrement)
    {
        // 解压文件
        var retryCount = 1;
        var encode = Encoding.GetEncoding("GB18030");
        var initialProgress = loader.Progress;

        while (retryCount <= 5)
            try
            {
                loader.Progress = initialProgress;

                // 删除旧目录
                ModBase.DeleteDirectory(installTemp);

                // 解压文件，ProgressIncrementHandler 通过 Lambda 更新进度
                ModBase.ExtractFile(fileAddress, installTemp, encode,
                    delta => loader.Progress += delta * progressIncrement);

                // 解压成功，更新进度并退出循环
                loader.Progress = initialProgress + progressIncrement;
                return;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"第 {retryCount} 次解压尝试失败");

                if (ex is ArgumentException || ex is IOException)
                {
                    encode = Encoding.UTF8;
                    ModBase.Log("[ModPack] 已切换压缩包解压编码为 UTF8");
                }

                // 检查加载器状态，决定是否中止
                if (loader is not null && loader.LoadingState != MyLoading.MyLoadingState.Run)
                    return;

                // 增加重试次数
                retryCount++;

                if (retryCount <= 5)
                    // 等待一段时间再重试
                    Thread.Sleep((retryCount - 1) * 2000);
                else
                    throw new Exception("解压整合包文件失败", ex);
            }
    }

    /// <summary>
    ///     从整合包的 override 目录复制文件，同时设置 PCL 的配置文件与版本隔离。
    ///     对路径末尾是否为 \ 没有要求。
    /// </summary>
    private static void CopyOverrideDirectory(string overridesFolder, string versionFolder, LoaderBase loader,
        double progressIncrement)
    {
        if (!overridesFolder.EndsWithF(@"\"))
            overridesFolder += @"\";
        if (!versionFolder.EndsWithF(@"\"))
            versionFolder += @"\";
        // 复制文件
        if (Directory.Exists(overridesFolder))
        {
            ModBase.Log($"[ModPack] 处理整合包覆写文件夹：{overridesFolder} → {versionFolder}");
            ModBase.CopyDirectory(overridesFolder, versionFolder,
                delta => loader.Progress += delta * progressIncrement);
        }
        else
        {
            ModBase.Log($"[ModPack] 整合包中没有覆写文件夹：{overridesFolder}");
            loader.Progress += progressIncrement;
        }

        // 设置 ini
        var overridesIni = $@"{overridesFolder}PCL\Setup.ini";
        var versionIni = $@"{versionFolder}PCL\Setup.ini";
        if (File.Exists(overridesIni))
        {
            ModBase.WriteIni(overridesIni, "VersionArgumentIndie", 1.ToString()); // 开启版本隔离
            ModBase.WriteIni(overridesIni, "VersionArgumentIndieV2", true.ToString());
            ModBase.CopyFile(overridesIni, versionIni); // 覆写已有的 ini
        }
        else
        {
            ModBase.WriteIni(versionIni, "VersionArgumentIndie", 1.ToString()); // 开启版本隔离
            ModBase.WriteIni(versionIni, "VersionArgumentIndieV2", true.ToString());
        }

        ModBase.IniClearCache(versionIni); // 重置缓存，避免被安装过程中写入的 ini 覆盖
    }

    #region CurseForge

    private static LoaderCombo<string> InstallPackCurseForge(string fileAddress, ZipArchive archive,
        string archiveBaseFolder, string instanceName = null, string logo = null, string resourceId = null,
        bool isOnlineInstall = false)
    {
        // 读取 Json 文件
        JsonObject json;
        try
        {
            json = (JsonObject)ModBase.GetJson(
                ModBase.ReadFile(archive.GetEntry(archiveBaseFolder + "manifest.json").Open()));
        }
        catch (Exception ex)
        {
            throw new Exception("CurseForge 整合包安装信息存在问题", ex);
        }

        if (json["minecraft"] is null || json["minecraft"]["version"] is null)
            throw new Exception("CurseForge 整合包未提供 Minecraft 版本信息");

        // 获取实例名
        if (instanceName is null)
        {
            instanceName = (string)(json["name"] ?? "");
            var validate = new FolderNameValidator(Path.Combine(ModFolder.mcFolderSelected, "versions"));
            if (!validate.Validate(instanceName).IsValid)
                instanceName = "";
            if (string.IsNullOrEmpty(instanceName))
                instanceName = ModMain.MyMsgBoxInput(Lang.Text("Minecraft.Download.Modpack.InputInstanceName"), "", "",
                    [validate]);
            if (string.IsNullOrEmpty(instanceName))
                throw new ModBase.CancelledException();
        }

        // 获取 Mod API 版本信息
        string forgeVersion = null;
        string neoForgeVersion = null;
        string fabricVersion = null;
        var modLoader = ModComp.CompLoaderType.Any;
        foreach (var Entry in (dynamic)json["minecraft"]["modLoaders"] ?? Array.Empty<JsonNode>())
        {
            string id = (Entry["id"] ?? "").ToString().ToLower();
            if (id.StartsWithF("forge-"))
            {
                // Forge 指定
                if (id.Contains("recommended"))
                    throw new Exception(Lang.Text("Minecraft.Download.Modpack.TooOldUnsupported"));
                ModBase.Log("[ModPack] 整合包 Forge 版本：" + id);
                forgeVersion = id.Replace("forge-", "");
                modLoader = ModComp.CompLoaderType.Forge;
            }
            else if (id.StartsWithF("neoforge-"))
            {
                // NeoForge 指定
                ModBase.Log("[ModPack] 整合包 NeoForge 版本：" + id);
                neoForgeVersion = id.Replace("neoforge-", "");
                modLoader = ModComp.CompLoaderType.NeoForge;
            }
            else if (id.StartsWithF("fabric-"))
            {
                // Fabric 指定
                try
                {
                    ModBase.Log("[ModPack] 整合包 Fabric 版本：" + id);
                    fabricVersion = id.Replace("fabric-", "");
                    modLoader = ModComp.CompLoaderType.Fabric;
                    break;
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "读取整合包 Fabric 版本失败：" + id);
                }
            }
            else if (id.StartsWithF("quilt-"))
            {
                throw new Exception(Lang.Text("Minecraft.Download.Modpack.QuiltUnsupported"));
            }
        }

        // 解压
        var installTemp = ModMain.RequestTaskTempFolder();
        var installLoaders = new List<LoaderBase>();
        var authoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var authoredIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overrideHome = (string)(json["overrides"] ?? "");
        if (!string.IsNullOrEmpty(overrideHome))
        {
            var overridePrefix = CombineArchivePath(archiveBaseFolder, overrideHome is "." or "./" ? "" : overrideHome);
            CollectArchiveRelativePaths(archive, overridePrefix, authoredPaths);
        }
        if (!string.IsNullOrEmpty(overrideHome))
            installLoaders.Add(new LoaderTask<string, int>(Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"),
                task =>
            {
                ExtractModpackFiles(installTemp, fileAddress, task, 0.6d);
                CopyOverrideDirectory(
                    Path.Combine(installTemp, archiveBaseFolder, overrideHome == "." || overrideHome == "./" ? "" : overrideHome),
                    $@"{ModFolder.mcFolderSelected}versions\{instanceName}", task, 0.4d);
            })
            {
                ProgressWeight = new FileInfo(fileAddress).Length / 1024d / 1024d / 6d,
                block = false
            }); // 每 6M 需要 1s
        // 获取 Mod 列表
        var modList = new List<int>();
        var modOptionalList = new List<int>();
        foreach (var ModEntry in (dynamic)json["files"] ?? Array.Empty<JsonNode>())
        {
            if (ModEntry["projectID"] is null || ModEntry["fileID"] is null)
            {
                HintService.Hint(Lang.Text("Minecraft.Download.Modpack.ModMissingRequiredInfoSkipped", ModEntry));
                continue;
            }

            modList.Add((int)ModEntry["fileID"]);
            if (ModEntry["required"] is JsonNode requiredNode && !requiredNode.ToObject<bool>())
                modOptionalList.Add((int)ModEntry["fileID"]);
        }

        if (modList.Any())
        {
            var modDownloadLoaders = new List<LoaderBase>();
            // 获取 Mod 下载信息
            modDownloadLoaders.Add(new LoaderTask<int, JsonArray>(
                Lang.Text("Minecraft.Download.Modpack.Stage.PrepareModsDownloadInfo"), task =>
            {
                var allowMirror = true;
                JsonArray ret;
                var tryCount = 0;
                do
                {
                    tryCount += 1;
                    ret = (JsonArray)((JsonObject)ModBase.GetJson(ModDownload.DlModRequest(
                        "https://api.curseforge.com/v1/mods/files",
                        "POST", "{\"fileIds\": [" + modList.Join(",") + "]}", "application/json",
                        allowMirror)))["data"];
                    if (modList.Count <= ret.Count)
                    {
                        ModBase.Log("[Modpack] 已获取到的模组数量足够，开始进行下一步");
                        break;
                    }

                    allowMirror = false;
                    ModBase.Log($"[Modpack] 获取模组数量不达标，设置镜像源允许状态为: {allowMirror}");
                    if (tryCount > 3) throw new Exception(Lang.Text("Minecraft.Download.Modpack.SomeModsDeleted"));
                } while (true);

                task.output = ret;
            })
            {
                ProgressWeight = modList.Count / 10d
            }); // 每 10 Mod 需要 1s
            // 构造 NetFile
            modDownloadLoaders.Add(new LoaderTask<JsonArray, List<DownloadFile>>(
                Lang.Text("Minecraft.Download.Modpack.Stage.BuildModsDownloadInfo"), task =>
            {
                var fileList = new Dictionary<int, DownloadFile>();
                foreach (var ModJson in task.input)
                {
                    var id = ModJson["id"].ToObject<int>();
                    // 跳过重复的 Mod（疑似 CurseForge Bug）
                    if (fileList.ContainsKey(id))
                        continue;
                    // 可选 Mod 提示
                    if (modOptionalList.Contains(id))
                        if (ModMain.MyMsgBox(
                                Lang.Text("Minecraft.Download.Modpack.OptionalFile.Message", ModJson["displayName"]),
                                Lang.Text("Minecraft.Download.Modpack.OptionalFile.Title"),
                                Lang.Text("Minecraft.Download.Modpack.OptionalFile.Download"),
                                Lang.Text("Minecraft.Download.Modpack.OptionalFile.Skip")
                            ) == 2)
                            continue;

                    // 根据 modules 和文件名后缀判断资源类型
                    string targetFolder;
                    ModComp.CompType type;
                    if (ModJson["modules"].AsArray().Any()) // modules 可能返回 null（#1006）
                    {
                        var moduleNames = ((JsonArray)ModJson["modules"]).Select(l => l["name"].ToString()).ToList();
                        if (moduleNames.Contains("META-INF") || moduleNames.Contains("mcmod.info") ||
                            (ModJson?["FileName"]?.ToString()?.EndsWithF(".jar", true)).GetValueOrDefault())
                        {
                            targetFolder = "mods";
                            type = ModComp.CompType.Mod;
                        }
                        else if (moduleNames.Contains("pack.mcmeta"))
                        {
                            targetFolder = "resourcepacks";
                            type = ModComp.CompType.ResourcePack;
                        }
                        else if (moduleNames.Contains("level.dat"))
                        {
                            targetFolder = "saves";
                            type = ModComp.CompType.World;
                        }
                        else
                        {
                            targetFolder = "shaderpacks";
                            type = ModComp.CompType.Shader;
                        }
                    }
                    else
                    {
                        targetFolder = "mods";
                        type = ModComp.CompType.Mod;
                    }

                    // 建立 CompFile
                    var file = new ModComp.CompFile((JsonObject)ModJson, type);
                    if (!file.Available)
                        continue;
                    // 实际的添加
                    var download = file.ToNetFile($@"{ModFolder.mcFolderSelected}versions\{instanceName}\{targetFolder}\",
                        ModComp.DownloadReason.ModPack, json["minecraft"]!["version"]!.ToString(), modLoader);
                    fileList.Add(id, download);
                    var relativePath = Path.GetRelativePath($@"{ModFolder.mcFolderSelected}versions\{instanceName}", download.LocalPath).Replace('\\', '/');
                    authoredPaths.Add(relativePath);
                    if (ModJson["modId"] is JsonNode modId)
                        authoredIdentities[relativePath] = $"curseforge:{modId}:{id}";
                    task.Progress += 1d / (1 + modList.Count);
                }

                task.output = fileList.Values.ToList();
            })
            {
                ProgressWeight = modList.Count / 200d,
                show = false
            }); // 每 200 Mod 需要 1s
            // 下载 Mod 文件
            modDownloadLoaders.Add(new LoaderDownload(Lang.Text("Minecraft.Download.Modpack.Stage.DownloadMods"), [])
                { ProgressWeight = modList.Count * 1.5d }); // 每个 Mod 需要 1.5s
            // 构造加载器
            installLoaders.Add(
                new LoaderCombo<int>(Lang.Text("Minecraft.Download.Modpack.Stage.DownloadMods.MainLoader"),
                        modDownloadLoaders)
                { show = false, ProgressWeight = modDownloadLoaders.Sum(l => l.ProgressWeight) });
        }

        // 构造加载器
        var request = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\",
            minecraftName = json["minecraft"]["version"].ToString(),
            forgeVersion = forgeVersion,
            neoForgeVersion = neoForgeVersion,
            fabricVersion = fabricVersion,
        };
        var mergeLoaders = ModDownloadLib.McInstallLoader(request);
        // 构造总加载器
        var loaders = new List<LoaderBase>();
        loaders.Add(new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.ModpackInstall"),
                installLoaders)
            { show = false, block = false, ProgressWeight = installLoaders.Sum(l => l.ProgressWeight) });
        loaders.Add(new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.GameInstall"), mergeLoaders)
            { show = false, ProgressWeight = mergeLoaders.Sum(l => l.ProgressWeight) });
        loaders.Add(new LoaderTask<string, string>(Lang.Text("Minecraft.Download.Modpack.Stage.FinalizeFiles"), task =>
        {
            // 设置图标
            var versionFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\";
            if (logo is not null && File.Exists(logo))
            {
                File.Copy(logo, Path.Combine(versionFolder, "PCL", "Logo.png"), true);
                States.Instance.LogoPath[versionFolder] = @"PCL\Logo.png";
                States.Instance.IsLogoCustom[versionFolder] = true;
                ModBase.Log("[ModPack] 已设置整合包 Logo：" + logo);
            }

            // 删除原始整合包文件
            foreach (var Target in new[] { Path.Combine(versionFolder, "原始整合包.zip"), Path.Combine(versionFolder, "原始整合包.mrpack") })
                if (File.Exists(Target))
                {
                    ModBase.Log("[ModPack] 删除原始整合包文件：" + Target);
                    File.Delete(Target);
                }

            if (File.Exists(fileAddress) && ModBase.GetFileNameWithoutExtentionFromPath(fileAddress) == "modpack")
            {
                ModBase.Log("[ModPack] 删除安装整合包文件：" + fileAddress);
                File.Delete(fileAddress);
            }

            // 整合包版本
            if (json["version"] is not null) States.Instance.ModpackVersion[versionFolder] = json["version"].ToString();
            States.Instance.ModpackSource[versionFolder] = "CurseForge";
            States.Instance.ModpackId[versionFolder] = resourceId;
            SaveInstalledPackBaseline(versionFolder, authoredPaths, "CurseForge", resourceId, (string?)json["version"], authoredIdentities);
            do
            {
                try
                {
                    var projects = ModComp.CompRequest.GetCompProjectsByIds([resourceId]);
                    if (projects.Count == 0)
                        break;
                    States.Instance.CustomInfo[versionFolder] = projects.First().Description;
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "[ModPack] 获取整合包描述文本失败");
                }
            } while (false);
        })
        {
            ProgressWeight = 0.1d,
            show = false
        });

        // 重复任务检查
        var loaderName = Lang.Text("Minecraft.Download.Modpack.Task.CurseForgeInstall", instanceName);
        if (loaderTaskbar.Any(l => (l.name ?? "") == (loaderName ?? "")))
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.Installing"), HintType.Error);
            throw new ModBase.CancelledException();
        }

        // 启动
        var loader = new LoaderCombo<string>(loaderName, loaders) { OnStateChanged = ModDownloadLib.McInstallState };
        loader.Start(request.targetInstanceFolder);
        LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        if (!isOnlineInstall)
            ModBase.RunInUi(() => ModMain.frmMain.PageChange(FormMain.PageType.TaskManager));
        return loader;
    }

    private static string CombineArchivePath(string prefix, string relative)
    {
        var left = (prefix ?? "").Replace('\\', '/').Trim('/');
        var right = (relative ?? "").Replace('\\', '/').Trim('/');
        return left.Length == 0 ? right : right.Length == 0 ? left : left + "/" + right;
    }

    private static void CollectArchiveRelativePaths(ZipArchive archive, string prefix, ISet<string> output)
    {
        var normalizedPrefix = (prefix ?? "").Replace('\\', '/').Trim('/');
        if (normalizedPrefix.Length > 0) normalizedPrefix += "/";
        foreach (var entry in archive.Entries)
        {
            var fullName = entry.FullName.Replace('\\', '/');
            if (fullName.EndsWith('/') || !fullName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = normalizedPrefix.Length == 0 ? fullName : fullName[normalizedPrefix.Length..];
            if (string.IsNullOrWhiteSpace(relative)) continue;
            PackUpdatePlanner.ValidateRelativePath(relative);
            output.Add(relative);
        }
    }

    public static bool HasPackUpdateBaseline(string instancePath)
    {
        try { return new PackBaselineStore(Paths.SharedLocalData).Load(instancePath) is { Files.Count: > 0 }; }
        catch (Exception ex) { ModBase.Log(ex, "检查整合包来源记录失败"); return false; }
    }

    /// <summary>Downloads the latest compatible upstream pack archive, stages it as a temporary instance, and applies a three-way update to the selected pack.</summary>
    public static void UpdatePackFromOnline(McInstance instance) => StartPackUpdate(instance, null);

    public static void UpdatePackFromLocal(McInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var archivePath = SystemDialogs.SelectFile(Lang.Text("Minecraft.Download.Modpack.FileDialog.Filter"),
            Lang.Text("Minecraft.Download.Modpack.FileDialog.Title"));
        if (string.IsNullOrWhiteSpace(archivePath)) return;
        var confirm = ModMain.MyMsgBox(Lang.Text("Instance.PackUpdate.Local.UnverifiedMessage"),
            Lang.Text("Instance.PackUpdate.Local.Title"), Lang.Text("Common.Action.Confirm"),
            Lang.Text("Common.Action.Cancel"), isWarn: true);
        if (confirm != 1) return;
        StartPackUpdate(instance, archivePath);
    }

    private static void StartPackUpdate(McInstance instance, string? localArchivePath)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var task = new LoaderTask<int, int>(Lang.Text("Instance.PackUpdate.Task"), loader =>
            _UpdatePackFromOnline(instance, loader, localArchivePath)) { block = false };
        var root = new LoaderCombo<int>(Lang.Text("Instance.PackUpdate.Task"), [task]) { show = false };
        root.Start(0);
        ModLoader.LoaderTaskbarAdd(root);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
    }

    private static void _UpdatePackFromOnline(McInstance instance, LoaderTask<int, int> task, string? localArchivePath)
    {
        var store = new PackBaselineStore(Paths.SharedLocalData);
        var stageName = "PCLLiteUpdate" + Guid.NewGuid().ToString("N");
        var stageVersionPath = Path.Combine(ModFolder.mcFolderSelected, "versions", stageName);
        var stageRoot = Path.Combine(Path.GetDirectoryName(instance.PathInstance.TrimEnd(Path.DirectorySeparatorChar))!, $".pcllite-pack-update-{Guid.NewGuid():N}");
        try
        {
            var gameRoot = Path.GetFullPath(instance.PathIndie).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var instanceVersionPath = Path.GetFullPath(instance.PathInstance).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(gameRoot, instanceVersionPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.IsolationRequired"));
            if (ModWatcher.mcWatcherList.Any(static watcher => watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running))
                throw new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.GameRunning"));
            PackUpdateTransaction.RecoverInterrupted(gameRoot);

            var previous = store.Load(instance.PathInstance);
            if (previous is null || localArchivePath is null && string.IsNullOrWhiteSpace(previous.ProjectId))
                throw new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.NoBaseline"));
            Directory.CreateDirectory(stageRoot);
            string archivePath;
            if (localArchivePath is null)
            {
                var projects = ModComp.CompRequest.GetCompProjectsByIds([previous.ProjectId]);
                var project = projects.FirstOrDefault()
                    ?? throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.ProjectUnavailable"));
                var releases = ModComp.CompFilesGet(project.Id, project.FromCurseForge)
                    .Where(static file => file.Available && file.Type == ModComp.CompType.ModPack)
                    .OrderByDescending(static file => file.Status == ModComp.CompFileStatus.Release)
                    .ThenByDescending(static file => file.ReleaseDate)
                    .Take(50)
                    .ToList();
                if (releases.Count == 0) throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.NoReleases"));

                var selection = releases.Select(file => (IMyRadio)new MyRadioBox
                {
                    Text = $"{(string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName)}  ·  {file.ReleaseDate:yyyy-MM-dd}  ·  {file.Status}",
                }).ToList();
                var selected = ModBase.RunInUiWait(() => ModMain.MyMsgBoxSelect(selection,
                    Lang.Text("Instance.PackUpdate.SelectVersion.Title"), Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")));
                if (selected is null || selected.Value < 0 || selected.Value >= releases.Count) throw new ModBase.CancelledException();
                var release = releases[selected.Value];
                task.Progress = 0.05d;

                archivePath = Path.Combine(stageRoot, "modpack" + (Path.GetExtension(release.FileName) is { Length: > 1 } extension && extension.Length <= 10 ? extension : ".zip"));
                var packageDownload = release.ToNetFile(archivePath, ModComp.DownloadReason.ModPack,
                    instance.Info.VanillaName, _GetInstanceLoader(instance));
                var packageLoader = new LoaderDownload(Lang.Text("Instance.PackUpdate.Task.Download"), [packageDownload]);
                packageLoader.Start();
                while (packageLoader.State == ModBase.LoadState.Loading)
                {
                    if (task.IsAborted)
                    {
                        packageLoader.Abort();
                        throw new ModBase.CancelledException();
                    }
                    task.Progress = 0.05d + 0.1d * packageLoader.Progress;
                    Thread.Sleep(50);
                }
                if (packageLoader.State != ModBase.LoadState.Finished)
                    throw packageLoader.Error ?? new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.DownloadFailed"));
                task.Progress = 0.15d;
            }
            else
            {
                if (!File.Exists(localArchivePath)) throw new FileNotFoundException(Lang.Text("Instance.PackUpdate.Error.DownloadFailed"), localArchivePath);
                var extension = Path.GetExtension(localArchivePath);
                if (!new[] { ".zip", ".mrpack", ".rar" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException(Lang.Text("Minecraft.Download.Modpack.UnsupportedArchive"));
                archivePath = Path.Combine(stageRoot, "selected-local-package" + extension);
                File.Copy(localArchivePath, archivePath, overwrite: false);
                task.Progress = 0.05d;
            }

            var stagingInstall = ModpackInstall(archivePath, stageName, resourceId: previous.ProjectId, isOnlineInstall: true);
            while (stagingInstall.State == ModBase.LoadState.Loading)
            {
                if (task.IsAborted)
                {
                    stagingInstall.Abort();
                    throw new ModBase.CancelledException();
                }
                task.Progress = 0.15d + stagingInstall.Progress * 0.35d;
                Thread.Sleep(50);
            }
            if (stagingInstall.State != ModBase.LoadState.Finished)
                throw stagingInstall.Error ?? new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.StageFailed"));

            var target = store.Load(stageVersionPath)
                ?? throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.BaselineMissing"));
            if (localArchivePath is not null)
            {
                // The user explicitly supplied this package but its author/source
                // cannot be independently authenticated. Keep the existing project
                // association and derive provenance from the original installed baseline.
                target = target with { Source = previous.Source, ProjectId = previous.ProjectId };
                store.Save(stageVersionPath, target);
                var stagedInstance = new McInstance(stageVersionPath).Load();
                if (!string.Equals(stagedInstance.Info.VanillaName, instance.Info.VanillaName, StringComparison.OrdinalIgnoreCase)
                    || stagedInstance.Info.HasForge != instance.Info.HasForge
                    || stagedInstance.Info.HasNeoForge != instance.Info.HasNeoForge
                    || stagedInstance.Info.HasFabric != instance.Info.HasFabric
                    || stagedInstance.Info.HasLegacyFabric != instance.Info.HasLegacyFabric
                    || stagedInstance.Info.HasQuilt != instance.Info.HasQuilt
                    || stagedInstance.Info.HasCleanroom != instance.Info.HasCleanroom)
                    throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.SourceMismatch"));
            }
            else if (!string.Equals(target.Source, previous.Source, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(target.ProjectId, previous.ProjectId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.SourceMismatch"));
            }

            var payloadPaths = target.Files.Values.ToDictionary(
                static item => item.Path,
                item => PackUpdatePlanner.ResolveWithin(stageVersionPath, item.Path),
                StringComparer.OrdinalIgnoreCase);
            var plan = PackUpdatePlanner.Build(gameRoot, previous, target, payloadPaths);
            var authorCount = plan.Actions.Count(static item => item.Kind is PackUpdateActionKind.InstallAuthorFile or PackUpdateActionKind.RemoveAuthorFile);
            var personalCount = plan.Actions.Count(static item => item.Kind == PackUpdateActionKind.PreservePersonalFile && item.Ownership == PackFileOwnership.PersonalModified);
            if (authorCount == 0 && plan.Warnings.Count == 0)
            {
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Instance.PackUpdate.Hint.AlreadyCurrent")));
                return;
            }
            var previewActions = plan.Actions
                .Where(static item => item.Kind != PackUpdateActionKind.PreservePersonalFile
                    || item.Ownership is PackFileOwnership.PersonalModified or PackFileOwnership.PersonalAdded)
                .ToList();
            var previewLines = previewActions
                .Take(24)
                .Select(item => $"• {(item.Kind == PackUpdateActionKind.InstallAuthorFile ? "+" : item.Kind == PackUpdateActionKind.RemoveAuthorFile ? "−" : "=")} {item.DestinationPath}"
                    + (item.Kind == PackUpdateActionKind.PreservePersonalFile ? $" ({Lang.Text("Instance.PackUpdate.Preview.Preserve")})" : ""));
            var hiddenLineCount = Math.Max(0, previewActions.Count - 24);
            var preview = Lang.Text("Instance.PackUpdate.Preview", plan.Actions.Count(static item => item.Kind == PackUpdateActionKind.InstallAuthorFile),
                plan.Actions.Count(static item => item.Kind == PackUpdateActionKind.RemoveAuthorFile), personalCount)
                + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, previewLines)
                + (hiddenLineCount > 0 ? Environment.NewLine + $"… {hiddenLineCount}" : "")
                + (plan.Warnings.Count > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, plan.Warnings) : "");
            var decision = ModBase.RunInUiWait(() => ModMain.MyMsgBox(preview, Lang.Text("Instance.PackUpdate.Preview.Title"),
                Lang.Text("Instance.PackUpdate.Preview.Preserve"), Lang.Text("Instance.PackUpdate.Preview.UseAuthor"),
                Lang.Text("Common.Action.Cancel"), isWarn: true));
            if (decision == 3 || decision == 0) throw new ModBase.CancelledException();
            if (decision == 2)
            {
                var authorChoices = previous.Files.Keys.Concat(target.Files.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static path => path, static _ => PackFileChoice.UseAuthor, StringComparer.OrdinalIgnoreCase);
                plan = PackUpdatePlanner.Build(gameRoot, previous, target, payloadPaths, authorChoices);
            }

            var restoreRoot = Path.Combine(Paths.SharedLocalData, "Minecraft", "PackRestorePoints");
            EnvironmentRestorePointService.Create(gameRoot, restoreRoot, "before pack update " + (target.VersionId ?? ""));
            EnvironmentRestorePointService.ApplyRetention(restoreRoot, gameRoot, 10);
            task.Progress = 0.55d;
            using var transactionCancellation = new CancellationTokenSource();
            PackUpdateTransaction.Apply(plan, transactionCancellation.Token,
                (completed, total) =>
                {
                    if (task.IsAborted) transactionCancellation.Cancel();
                    task.Progress = total == 0 ? 0.95d : 0.55d + 0.4d * completed / total;
                },
                () => store.Save(instance.PathInstance, target));
            if (!string.IsNullOrWhiteSpace(target.VersionId)) States.Instance.ModpackVersion[instance.PathInstance] = target.VersionId;
            task.Progress = 1d;
            ModBase.RunInUi(() =>
            {
                ModMain.frmInstanceOverall?.RefreshPackDetails();
                HintService.Hint(Lang.Text("Instance.PackUpdate.Hint.Success", target.VersionId ?? ""), HintType.Success);
            });
        }
        finally
        {
            try
            {
                store.Delete(stageVersionPath);
                if (Directory.Exists(stageVersionPath)) Directory.Delete(stageVersionPath, recursive: true);
            }
            catch (Exception ex) { ModBase.Log(ex, "清理整合包更新暂存实例失败"); }
            try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); }
            catch (Exception ex) { ModBase.Log(ex, "清理整合包更新下载目录失败"); }
        }
    }

    private static ModComp.CompLoaderType _GetInstanceLoader(McInstance instance)
    {
        if (instance.Info.HasNeoForge) return ModComp.CompLoaderType.NeoForge;
        if (instance.Info.HasForge || instance.Info.HasCleanroom) return ModComp.CompLoaderType.Forge;
        if (instance.Info.HasFabric || instance.Info.HasLegacyFabric) return ModComp.CompLoaderType.Fabric;
        if (instance.Info.HasQuilt) return ModComp.CompLoaderType.Quilt;
        return ModComp.CompLoaderType.Any;
    }

    private static void SaveInstalledPackBaseline(string versionFolder, IEnumerable<string> authoredPaths,
        string source, string? projectId, string? versionId, IReadOnlyDictionary<string, string> identities)
    {
        try
        {
            var store = new PackBaselineStore(Paths.SharedLocalData);
            var baseline = store.Capture(versionFolder, authoredPaths, source, projectId, versionId,
                relative => identities.TryGetValue(relative, out var identity) ? identity : null);
            store.Save(versionFolder, baseline);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "记录整合包来源文件清单失败；当前安装仍可使用，但该实例之后无法进行来源差异更新");
        }
    }

    #endregion

    #region Modrinth

    private static LoaderCombo<string> InstallPackModrinth(string fileAddress, ZipArchive archive,
        string archiveBaseFolder, string instanceName = null, string logo = null, string resourceId = null,
        bool isOnlineInstall = false)
    {
        // 读取 Json 文件
        JsonObject json;
        try
        {
            json = (JsonObject)ModBase.GetJson(
                ModBase.ReadFile(archive.GetEntry(archiveBaseFolder + "modrinth.index.json").Open()));
        }
        catch (Exception ex)
        {
            throw new Exception("Modrinth 整合包安装信息存在问题", ex);
        }

        if (json["dependencies"] is null || json["dependencies"]["minecraft"] is null)
            throw new Exception("Modrinth 整合包未提供 Minecraft 版本信息");
        // 获取 Mod API 版本信息
        string minecraftVersion = null;
        string forgeVersion = null;
        string neoForgeVersion = null;
        string fabricVersion = null;
        var modLoader = ModComp.CompLoaderType.Any;
        foreach (var Entry in json["dependencies"]?.AsObject() ?? new JsonObject())
            switch (Entry.Key.ToLower() ?? "")
            {
                case "minecraft":
                {
                    minecraftVersion = Entry.Value?.ToObject<string>();
                    break;
                }
                case "forge": // eg. 14.23.5.2859 / 1.19-41.1.0
                {
                    forgeVersion = Entry.Value?.ToObject<string>();
                    modLoader = ModComp.CompLoaderType.Forge;
                    ModBase.Log("[ModPack] 整合包 Forge 版本：" + forgeVersion);
                    break;
                }
                case "neoforge":
                case "neo-forge": // eg. 20.6.98-beta
                {
                    neoForgeVersion = Entry.Value?.ToObject<string>();
                    modLoader = ModComp.CompLoaderType.NeoForge;
                    ModBase.Log("[ModPack] 整合包 NeoForge 版本：" + neoForgeVersion);
                    break;
                }
                case "fabric-loader": // eg. 0.14.14
                {
                    fabricVersion = Entry.Value?.ToObject<string>();
                    modLoader = ModComp.CompLoaderType.Fabric;
                    ModBase.Log("[ModPack] 整合包 Fabric 版本：" + fabricVersion);
                    break;
                }
                case "quilt-loader": // eg. 0.26.0
                {
                    throw new Exception(Lang.Text("Minecraft.Download.Modpack.QuiltUnsupported"));
                }

                default:
                {
                    HintService.Hint(Lang.Text("Minecraft.Download.Modpack.UnknownLoader", Entry.Key, Entry.Value),
                        HintType.Error);
                    break;
                }
            }

        // 获取实例名
        if (instanceName is null)
        {
            instanceName = (string)(json["name"] ?? "");
            var validate = new FolderNameValidator(Path.Combine(ModFolder.mcFolderSelected, "versions"));
            if (!validate.Validate(instanceName).IsValid)
                instanceName = "";
            if (string.IsNullOrEmpty(instanceName))
                instanceName = ModMain.MyMsgBoxInput(Lang.Text("Minecraft.Download.Modpack.InputInstanceName"), "", "",
                    [validate]);
            if (string.IsNullOrEmpty(instanceName))
                throw new ModBase.CancelledException();
        }

        // 解压
        var installTemp = ModMain.RequestTaskTempFolder();
        var installLoaders = new List<LoaderBase>();
        var authoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var authoredIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectArchiveRelativePaths(archive, CombineArchivePath(archiveBaseFolder, "overrides"), authoredPaths);
        CollectArchiveRelativePaths(archive, CombineArchivePath(archiveBaseFolder, "client-overrides"), authoredPaths);
        installLoaders.Add(new LoaderTask<string, int>(Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"),
            task =>
        {
            ExtractModpackFiles(installTemp, fileAddress, task, 0.5d);
            CopyOverrideDirectory(Path.Combine(installTemp, archiveBaseFolder, "overrides"),
                Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName), task, 0.4d);
            CopyOverrideDirectory(Path.Combine(installTemp, archiveBaseFolder, "client-overrides"),
                Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName), task, 0.1d);
        })
        {
            ProgressWeight = new FileInfo(fileAddress).Length / 1024d / 1024d / 6d,
            block = false
        }); // 每 6M 需要 1s
        // 获取下载文件列表
        var fileList = new List<DownloadFile>();
        foreach (var File in (dynamic)json["files"] ?? Array.Empty<JsonNode>())
        {
            // 检查是否需要该文件
            if (File["env"] is not null)
                switch (File["env"]["client"].ToString() ?? "")
                {
                    case "optional":
                    {
                        if (ModMain.MyMsgBox(
                                Lang.Text("Minecraft.Download.Modpack.OptionalFile.Message",
                                    ModBase.GetFileNameFromPath(File["path"].ToString())),
                                Lang.Text("Minecraft.Download.Modpack.OptionalFile.Title"),
                                Lang.Text("Minecraft.Download.Modpack.OptionalFile.Download"),
                                Lang.Text("Minecraft.Download.Modpack.OptionalFile.Skip")
                            ) == 2) continue;

                        break;
                    }
                    case "unsupported":
                    {
                        continue;
                    }
                }

            // 添加下载文件
            var urls = ((JsonArray)File["downloads"])
                .OfType<JsonNode>()
                .Select(x => ModComp.CompFile.HandleCurseForgeDownloadUrls(x.ToString()))
                .ToList();
            // 镜像源
            urls = urls.SelectMany(x => ModDownload.DlSourceModDownloadGet(x)).ToList();
            var relativePath = ((string)File["path"]).Replace('\\', '/');
            PackUpdatePlanner.ValidateRelativePath(relativePath);
            var versionRoot = Path.GetFullPath(Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName));
            var targetPath = PackUpdatePlanner.ResolveWithin(versionRoot, relativePath);
            if (!targetPath.StartsWith(versionRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                ModMain.MyMsgBox(Lang.Text("Minecraft.Download.Modpack.PathOutsideInstance.Message", targetPath),
                    Lang.Text("Minecraft.Download.Modpack.PathOutsideInstance.Title"), isWarn: true);
                throw new ModBase.CancelledException();
            }

            fileList.Add(new DownloadFile(
                ModComp.CompFile.HandleModrinthDownloadUrls(urls, ModComp.DownloadReason.ModPack, minecraftVersion,
                    modLoader), targetPath,
                new ModBase.FileChecker(actualSize: ((JsonNode)File["fileSize"]).ToObject<long>(),
                    hash: File["hashes"]["sha1"].ToString()), true));
            authoredPaths.Add(relativePath);
        }

        if (fileList.Any())
            installLoaders.Add(
                new LoaderDownload(Lang.Text("Minecraft.Download.Modpack.Stage.DownloadAdditions"), fileList)
                { ProgressWeight = fileList.Count * 1.5d }); // 每个 Mod 需要 1.5s

        // 构造加载器
        var request = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\",
            minecraftName = minecraftVersion,
            forgeVersion = forgeVersion,
            neoForgeVersion = neoForgeVersion,
            fabricVersion = fabricVersion,
        };
        var mergeLoaders = ModDownloadLib.McInstallLoader(request);
        // 构造总加载器
        var loaders = new List<LoaderBase>();
        loaders.Add(new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.ModpackInstall"),
                installLoaders)
            { show = false, block = false, ProgressWeight = installLoaders.Sum(l => l.ProgressWeight) });
        loaders.Add(new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.GameInstall"), mergeLoaders)
            { show = false, ProgressWeight = mergeLoaders.Sum(l => l.ProgressWeight) });
        loaders.Add(new LoaderTask<string, string>(Lang.Text("Minecraft.Download.Modpack.Stage.FinalizeFiles"), task =>
        {
            // 设置图标
            var versionFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\";
            if (logo is not null && File.Exists(logo))
            {
                File.Copy(logo, Path.Combine(versionFolder, "PCL", "Logo.png"), true);
                States.Instance.LogoPath[versionFolder] = @"PCL\Logo.png";
                States.Instance.IsLogoCustom[versionFolder] = true;
                ModBase.Log("[ModPack] 已设置整合包 Logo：" + logo);
            }

            // 删除原始整合包文件
            foreach (var Target in new[] { Path.Combine(versionFolder, "原始整合包.zip"), Path.Combine(versionFolder, "原始整合包.mrpack") })
                if (File.Exists(Target))
                {
                    ModBase.Log("[ModPack] 删除原始整合包文件：" + Target);
                    File.Delete(Target);
                }

            if (File.Exists(fileAddress) && ModBase.GetFileNameWithoutExtentionFromPath(fileAddress) == "modpack")
            {
                ModBase.Log("[ModPack] 删除安装整合包文件：" + fileAddress);
                File.Delete(fileAddress);
            }

            // 整合包版本
            if (json["versionId"] is not null)
                States.Instance.ModpackVersion[versionFolder] = json["versionId"].ToString();
            States.Instance.ModpackSource[versionFolder] = "Modrinth";
            States.Instance.ModpackId[versionFolder] = resourceId;
            SaveInstalledPackBaseline(versionFolder, authoredPaths, "Modrinth", resourceId, (string?)json["versionId"], authoredIdentities);
            do
            {
                try
                {
                    var projects = ModComp.CompRequest.GetCompProjectsByIds([resourceId]);
                    if (projects.Count == 0)
                        break;
                    States.Instance.CustomInfo[versionFolder] = projects.First().Description;
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "[ModPack] 获取整合包描述文本失败");
                }
            } while (false);
        })
        {
            ProgressWeight = 0.1d,
            show = false
        });

        // 重复任务检查
        var loaderName = Lang.Text("Minecraft.Download.Modpack.Task.ModrinthInstall", instanceName);
        if (loaderTaskbar.Any(l => (l.name ?? "") == (loaderName ?? "")))
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.Installing"), HintType.Error);
            throw new ModBase.CancelledException();
        }

        // 启动
        var loader = new LoaderCombo<string>(loaderName, loaders) { OnStateChanged = ModDownloadLib.McInstallState };
        loader.Start(request.targetInstanceFolder);
        LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        if (!isOnlineInstall)
            ModBase.RunInUi(() => ModMain.frmMain.PageChange(FormMain.PageType.TaskManager));
        return loader;
    }

    #endregion

    #region HMCL

    private static LoaderCombo<string> InstallPackHMCL(string fileAddress, ZipArchive archive, string archiveBaseFolder)
    {
        // 读取 Json 文件
        JsonObject json;
        try
        {
            json = (JsonObject)ModBase.GetJson(
                ModBase.ReadFile(archive.GetEntry(archiveBaseFolder + "modpack.json").Open(), Encoding.UTF8));
        }
        catch (Exception ex)
        {
            throw new Exception("HMCL 整合包安装信息存在问题", ex);
        }

        // 获取实例名
        var instanceName = (string)(json["name"] ?? "");
        var validate = new FolderNameValidator(Path.Combine(ModFolder.mcFolderSelected, "versions"));
        if (!validate.Validate(instanceName).IsValid)
            instanceName = "";
        if (string.IsNullOrEmpty(instanceName))
            instanceName = ModMain.MyMsgBoxInput(Lang.Text("Minecraft.Download.Modpack.InputInstanceName"), "", "",
                [validate]);
        if (string.IsNullOrEmpty(instanceName))
            throw new ModBase.CancelledException();
        // 解压
        var installTemp = ModMain.RequestTaskTempFolder();
        var installLoaders = new List<LoaderBase>();
        installLoaders.Add(new LoaderTask<string, int>(Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"),
            task =>
        {
            ExtractModpackFiles(installTemp, fileAddress, task, 0.6d);
            CopyOverrideDirectory(Path.Combine(installTemp, archiveBaseFolder, "minecraft"),
                Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName), task, 0.4d);
        })
        {
            ProgressWeight = new FileInfo(fileAddress).Length / 1024d / 1024d / 6d,
            block = false
        }); // 每 6M 需要 1s
        // 构造游戏本体安装加载器
        if (json["gameVersion"] is null)
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.MissingGameVersion.Hmcl"));
        var request = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\",
            minecraftName = json["gameVersion"].ToString()
        };
        var mergeLoaders = ModDownloadLib.McInstallLoader(request);
        // 构造总加载器
        var loaders = new List<LoaderBase>
        {
            new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.ModpackInstall"), installLoaders)
                { show = false, block = false, ProgressWeight = installLoaders.Sum(l => l.ProgressWeight) },
            new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.GameInstall"), mergeLoaders)
                { show = false, ProgressWeight = mergeLoaders.Sum(l => l.ProgressWeight) }
        };
        // 重复任务检查
        var loaderName = Lang.Text("Minecraft.Download.Modpack.Task.HmclInstall", instanceName);
        if (loaderTaskbar.Any(l => (l.name ?? "") == (loaderName ?? "")))
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.Installing"), HintType.Error);
            throw new ModBase.CancelledException();
        }

        // 启动
        var loader = new LoaderCombo<string>(loaderName, loaders) { OnStateChanged = ModDownloadLib.McInstallState };
        loader.Start(request.targetInstanceFolder);
        LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModBase.RunInUi(() => ModMain.frmMain.PageChange(FormMain.PageType.TaskManager));
        return loader;
    }

    #endregion

    #region MCBBS

    private static string HandleLaunchArguments(JsonNode? argumentNode)
    {
        IEnumerable<string> arguments = argumentNode switch
        {
            JsonArray array => array.OfType<JsonValue>()
                .Where(argument => argument.GetValueKind() == JsonValueKind.String)
                .Select(argument => argument.GetValue<string>()),
            JsonValue argument when argument.GetValueKind() == JsonValueKind.String =>
                [argument.GetValue<string>()],
            _ => []
        };
        return string.Join(" ", arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)));
    }

    private static LoaderCombo<string> InstallPackMCBBS(string fileAddress, ZipArchive archive,
        string archiveBaseFolder, string instanceName = null)
    {
        // 读取 Json 文件
        JsonObject json;
        try
        {
            // VB 的 If(a, b) 在 C# 中如果是 null 合并则用 ??，如果是三元运算则用 ?:
            var entry = archive.GetEntry(archiveBaseFolder + "mcbbs.packmeta") ??
                        archive.GetEntry(archiveBaseFolder + "manifest.json");
            using (var stream = entry.Open())
            {
                json = (JsonObject)ModBase.GetJson(ModBase.ReadFile(stream, Encoding.UTF8));
            }
        }
        catch (Exception ex)
        {
            throw new Exception("MCBBS 整合包安装信息存在问题", ex);
        }

        // 获取实例名
        if (instanceName is null)
        {
            instanceName = json["name"]?.ToString() ?? "";
            var validate = new FolderNameValidator(Path.Combine(ModFolder.mcFolderSelected, "versions"));

            if (!validate.Validate(instanceName).IsValid) instanceName = "";

            if (string.IsNullOrEmpty(instanceName))
                instanceName = ModMain.MyMsgBoxInput(Lang.Text("Minecraft.Download.Modpack.InputInstanceName"), "", "",
                    [validate]);

            if (string.IsNullOrEmpty(instanceName)) throw new ModBase.CancelledException();
        }

        // 解压与路径准备
        var installTemp = ModMain.RequestTaskTempFolder();
        var versionFolder = $"{ModFolder.mcFolderSelected}versions\\{instanceName}";
        var installLoaders = new List<LoaderBase>();

        // 解压整合包文件任务
        var unzipTask = new LoaderTask<string, int>(Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"),
            task =>
        {
            ExtractModpackFiles(installTemp, fileAddress, task, 0.6);
            CopyOverrideDirectory(
                Path.Combine(installTemp, archiveBaseFolder, "overrides"),
                Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName),
                task, 0.4);

            // JVM 参数处理
            if (json["launchInfo"] is not null)
            {
                var launchInfo = (JsonObject)json["launchInfo"];
                Config.Instance.JvmArgs[versionFolder] = HandleLaunchArguments(launchInfo["javaArgument"]);
                Config.Instance.GameArgs[versionFolder] = HandleLaunchArguments(launchInfo["launchArgument"]);
            }

            // 整合包版本
            if (json["version"] is not null) States.Instance.ModpackVersion[versionFolder] = json["version"].ToString();
        });

        unzipTask.ProgressWeight = new FileInfo(fileAddress).Length / 1024.0 / 1024.0 / 6.0; // 每 6M 需要 1s
        unzipTask.block = false;
        installLoaders.Add(unzipTask);

        // 构造加载器
        if (json["addons"] is null)
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.MissingGameVersion.McbbsAddons"));

        var addons = new Dictionary<string, string>();
        foreach (var EntryNode in json["addons"].AsArray()) { var entry = EntryNode.AsObject(); addons.Add(entry["id"].ToString(), entry["version"].ToString()); }

        if (!addons.ContainsKey("game"))
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.MissingGameVersion.Generic"), HintType.Error);
            return null;
        }

        if (addons.ContainsKey("quilt"))
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.QuiltUnsupported"));

        // 构造安装请求
        var request = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = $"{ModFolder.mcFolderSelected}versions\\{instanceName}\\",
            minecraftName = addons["game"],
            optiFineVersion = addons.ContainsKey("optifine") ? addons["optifine"] : null,
            forgeVersion = addons.ContainsKey("forge") ? addons["forge"] : null,
            neoForgeVersion = addons.ContainsKey("neoforge") ? addons["neoforge"] : null,
            fabricVersion = addons.ContainsKey("fabric") ? addons["fabric"] : null,
        };

        var mergeLoaders = ModDownloadLib.McInstallLoader(request);

        // 构造总加载器
        var loaders = new List<LoaderBase>();
        loaders.Add(new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.ModpackInstall"),
            installLoaders)
        {
            show = false,
            block = false,
            ProgressWeight = installLoaders.Sum(l => l.ProgressWeight)
        });
        loaders.Add(new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.GameInstall"), mergeLoaders)
        {
            show = false,
            ProgressWeight = mergeLoaders.Sum(l => l.ProgressWeight)
        });

        // 重复任务检查
        var loaderName = Lang.Text("Minecraft.Download.Modpack.Task.McbbsInstall", instanceName);
        if (loaderTaskbar.Any(l => l.name == loaderName))
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.Installing"), HintType.Error);
            throw new ModBase.CancelledException();
        }

        // 启动任务
        var loader = new LoaderCombo<string>(loaderName, loaders);
        loader.OnStateChanged = ModDownloadLib.McInstallState;

        loader.Start(request.targetInstanceFolder);
        LoaderTaskbarAdd(loader);

        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModBase.RunInUi(() => ModMain.frmMain.PageChange(FormMain.PageType.TaskManager));

        return loader;
    }

    #endregion

    #region 带启动器的压缩包

    private static LoaderCombo<string> InstallPackLauncherPack(string fileAddress, ZipArchive archive,
        string archiveBaseFolder)
    {
        // 获取解压路径
        ModMain.MyMsgBox(Lang.Text("Minecraft.Download.Modpack.SelectEmptyFolder.Message"),
            Lang.Text("Common.Action.Install"), Lang.Text("Common.Action.Continue"), forceWait: true);
        var targetFolder = SystemDialogs.SelectFolder(Lang.Text("Minecraft.Download.Modpack.SelectTargetFolder.Title"));
        if (string.IsNullOrEmpty(targetFolder))
            throw new ModBase.CancelledException();
        if (Directory.GetFileSystemEntries(targetFolder).Length > 0)
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.TargetFolderMustBeEmpty"), HintType.Error);
            throw new ModBase.CancelledException();
        }

        // 解压
        var loader = new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.ExtractArchive"), new[]
        {
            new LoaderTask<string, int>(Lang.Text("Minecraft.Download.Modpack.Stage.ExtractArchive"), task =>
            {
                ExtractModpackFiles(targetFolder, fileAddress, task, 0.9d);
                Thread.Sleep(400); // 避免文件争用
                // PCL Lite should win over a legacy upstream PCL or any other bundled launcher.
                var launcherCandidates = new List<LauncherBundleCandidate>();
                foreach (var exeFile in Directory.GetFiles(targetFolder, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var productName = FileVersionInfo.GetVersionInfo(exeFile).ProductName ?? string.Empty;
                    ModBase.Log($"[Modpack] 文件 {exeFile} 的产品名标识为 {productName}");
                    launcherCandidates.Add(new LauncherBundleCandidate(exeFile, productName));
                }
                var launcher = LauncherBundleResolver.SelectBundledLauncher(launcherCandidates);
                if (launcher is not null)
                {
                    var selectedProduct = FileVersionInfo.GetVersionInfo(launcher).ProductName;
                    ModBase.Log(selectedProduct == LauncherBundleResolver.LiteProductName
                        ? $"[Modpack] 优先选择整合包内的 PCL Lite：{launcher}"
                        : $"[Modpack] 选择整合包内的启动器：{launcher}");
                }

                task.Progress = 0.95d;
                // 尝试使用附带的启动器打开
                if (launcher is not null)
                {
                    ModBase.Log("[Modpack] 找到压缩包中附带的启动器：" + launcher);
                    if (ModMain.MyMsgBox(Lang.Text("Minecraft.Download.Modpack.BundledLauncher.Message", launcher),
                            Lang.Text("Minecraft.Download.Modpack.BundledLauncher.Title"),
                            Lang.Text("Minecraft.Download.Modpack.BundledLauncher.UseBundled"),
                            Lang.Text("Minecraft.Download.Modpack.BundledLauncher.DoNotUse")
                        ) == 1)
                    {
                        ModBase.OpenExplorer(targetFolder);
                        ModBase.ShellOnly(launcher, "--wait"); // 要求等待已有的 PCL 退出
                        ModBase.Log("[Modpack] 为换用整合包中的启动器启动，强制结束程序");
                        ModMain.frmMain.EndProgram(false);
                        return;
                    }
                }
                else
                {
                    ModBase.Log("[Modpack] 未找到压缩包中附带的启动器");
                }

                ModBase.OpenExplorer(targetFolder);
                // 加入文件夹列表
                var instanceName = ModBase.GetFolderNameFromPath(targetFolder);
                Directory.CreateDirectory(Path.Combine(targetFolder, ".minecraft"));
                PageSelectLeft.AddFolder(
                    Path.Combine(targetFolder, ".minecraft", archiveBaseFolder.Replace("/", @"\").TrimStart('\\')), instanceName,
                    false); // 格式例如：包裹文件夹\.minecraft\（最短为空字符串）
                // 调用 modpack 文件进行安装
                var modpackFile = Directory.GetFiles(targetFolder, "modpack.*", SearchOption.AllDirectories).First();
                ModBase.Log("[Modpack] 调用 modpack 文件继续安装：" + modpackFile);
                ModpackInstall(modpackFile);
            })
        });
        loader.Start(targetFolder);
        LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
        return loader;
    }

    #endregion

    #region 普通压缩包

    private static LoaderCombo<string> InstallPackCompress(string fileAddress, ZipArchive archive)
    {
        // 尝试定位 .minecraft 文件夹：寻找形如 “/versions/XXX/XXX.json” 的路径
        Match match = null;
        var regex = new Regex(@"^.*\/(?=versions\/(?<ver>[^\/]+)\/(\k<ver>)\.json$)", RegexOptions.IgnoreCase);
        foreach (var Entry in archive.Entries)
        {
            var entryMatch = regex.Match("/" + Entry.FullName);
            if (entryMatch.Success)
            {
                match = entryMatch;
                break;
            }
        }

        if (match is null)
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.UnknownArchiveStructure")); // 没有匹配
        var archiveBaseFolder = match.Value.Replace("/", @"\").TrimStart('\\'); // 格式例如：包裹文件夹\.minecraft\（最短为空字符串）
        var instanceName = match.Groups[1].Value;
        ModBase.Log("[ModPack] 检测到压缩包的 .minecraft 根目录：" + archiveBaseFolder + "，命中的实例名：" + instanceName);
        // 获取解压路径
        ModMain.MyMsgBox(Lang.Text("Minecraft.Download.Modpack.SelectEmptyFolder.Message"),
            Lang.Text("Common.Action.Install"), Lang.Text("Common.Action.Continue"), forceWait: true);
        var targetFolder = SystemDialogs.SelectFolder(Lang.Text("Minecraft.Download.Modpack.SelectTargetFolder.Title"));
        if (string.IsNullOrEmpty(targetFolder))
            throw new ModBase.CancelledException();
        if (targetFolder.Contains("!") || targetFolder.Contains(";"))
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.InvalidGamePathChars", targetFolder),
                HintType.Error);
            throw new ModBase.CancelledException();
        }

        if (Directory.GetFileSystemEntries(targetFolder).Length > 0)
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.TargetFolderMustBeEmpty"), HintType.Error);
            throw new ModBase.CancelledException();
        }

        // 解压
        var loader = new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.ExtractArchive"), new[]
        {
            new LoaderTask<string, int>(Lang.Text("Minecraft.Download.Modpack.Stage.ExtractArchive"), task =>
            {
                ExtractModpackFiles(targetFolder, fileAddress, task, 0.95d);
                // 加入文件夹列表
                PageSelectLeft.AddFolder(Path.Combine(targetFolder, archiveBaseFolder), ModBase.GetFolderNameFromPath(targetFolder),
                    false);
                Thread.Sleep(400); // 避免文件争用
                ModBase.RunInUi(() => ModMain.frmMain.PageChange(FormMain.PageType.InstanceSelect));
            })
        })
        {
            OnStateChanged = ModDownloadLib.McInstallState
        };
        loader.Start(targetFolder);
        LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
        return loader;
    }

    #endregion

    #region MultiMC

    public class MMCPackInfo
    {
        public JsonObject additionalJson = new();
        public bool isCleanroomOverrided;
        public bool isFabricOverrided;
        public bool isForgeOverrided;
        public bool isMcArgsEdited;
        public bool isMinecraftOverrided;
        public bool isNeoForgeOverrided;
        public JsonArray jvmArgs = new();
        public JsonArray libraries = new();
        public JsonObject overridedJson = new();
        public string tweakers = null;
    }

    private static LoaderCombo<string> InstallPackMMC(string fileAddress, ZipArchive archive, string archiveBaseFolder)
    {
        // 读取 Json 文件
        JsonObject packJson;
        string packInstance;
        MMCPackInfo packInfo = null;
        try
        {
            packJson = (JsonObject)ModBase.GetJson(
                ModBase.ReadFile(archive.GetEntry(archiveBaseFolder + "mmc-pack.json").Open(), Encoding.UTF8));
            // 部分整合包不含 instance.cfg，此时会在后续流程中要求用户输入实例名
            var instanceCfgEntry = archive.GetEntry(archiveBaseFolder + "instance.cfg");
            packInstance = instanceCfgEntry is null
                ? ""
                : ModBase.ReadFile(instanceCfgEntry.Open(), Encoding.UTF8);

            #region JSON Patches

            // 参考 https://github.com/MultiMC/Launcher/wiki/JSON-Patches
            do
            {
                try
                {
                    // 部分压缩包不包含目录条目，因此按文件前缀匹配补丁，而不是依赖 "patches/" 目录项
                    if (!archive.Entries.Any(e =>
                            !e.FullName.EndsWith("/") &&
                            e.FullName.StartsWith(archiveBaseFolder + "patches/", StringComparison.OrdinalIgnoreCase)))
                        break;
                    ModBase.Log("[ModPack] 安装的 MultiMC 整合包存在 JSON Patches");
                    // 排序预处理
                    var patches = new List<KeyValuePair<JsonObject, int>>();
                    foreach (var entry in archive.Entries)
                        if (!entry.FullName.EndsWith("/") && entry.FullName.StartsWith(archiveBaseFolder + "patches/",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            var patch = (JsonObject)ModBase.GetJson(ModBase.ReadFile(
                                archive.GetEntry(entry.FullName).Open(), Encoding.UTF8));
                            patches.Add(new KeyValuePair<JsonObject, int>(patch,
                                (int)(patch["order"] is not null ? patch["order"] : 0)));
                        }

                    var components = (JsonArray)packJson["components"];
                    var componentUids = components
                        .Select(c => c["uid"]?.ToString())
                        .ToHashSet();
                    
                    patches = patches
                        .Where(p => componentUids.Contains(p.Key["uid"]?.ToString()))
                        .OrderBy(p => p.Value)
                        .ToList();
                    // 应用 Patches
                    packInfo = new MMCPackInfo();

                    string tweakers = null;
                    JsonObject assetIndex = null;
                    string mainClass = null;
                    List<int> javaMajorsSet = null;
                    JsonArray javaMajorsJson = null;
                    var gameArguments = new JsonArray();
                    var jvmArguments = new JsonArray();
                    var libJson = new JsonArray();
                    var addLibJson = new JsonArray();
                    foreach (var Patch in patches)
                    {
                        var patchJson = Patch.Key;
                        if ((string)patchJson["uid"] == "net.minecraft")
                        {
                            packInfo.isMinecraftOverrided = true;
                        }
                        else if ((string)patchJson["uid"] == "net.minecraftforge")
                        {
                            if (patchJson["version"].ToString().StartsWithF("0."))
                                packInfo.isCleanroomOverrided = true;
                            else
                                packInfo.isForgeOverrided = true;
                        }
                        else if ((string)patchJson["uid"] == "net.neoforged")
                        {
                            packInfo.isNeoForgeOverrided = true;
                        }
                        else if ((string)patchJson["uid"] == "net.fabricmc.fabric-loader")
                        {
                            packInfo.isFabricOverrided = true;
                        }
                        else if ((string)patchJson["uid"] == "org.quiltmc.quilt-loader")
                        {
                            throw new Exception(Lang.Text("Minecraft.Download.Modpack.QuiltUnsupported"));
                        }

                        // JVM 参数
                        if (patchJson["+jvmArgs"] is not null)
                        {
                            jvmArguments.Merge(patchJson["+jvmArgs"]);
                            ModBase.Log($"[ModPack] 已应用 JSON-Patch {patchJson["uid"]} 的 JVM 参数");
                        }

                        // Libraries
                        if (patchJson["libraries"] is not null || patchJson["+libraries"] is not null)
                        {
                            var libs = new JsonArray();
                            if (patchJson["libraries"] is not null)
                                foreach (var Library in patchJson["libraries"].AsArray())
                                {
                                    if (Library is not JsonObject LibraryObj) continue;
                                    var libJobj = LibraryObj.DeepClone().AsObject();
                                    if (libJobj["MMC-hint"] is not null)
                                    {
                                        libJobj.Add("hint", libJobj["MMC-hint"]?.DeepClone());
                                        libJobj.Remove("MMC-hint");
                                    }

                                    libs.Add(libJobj);
                                }

                            if (patchJson["+libraries"] is not null)
                                foreach (var Library in patchJson["+libraries"].AsArray()) // TODO: 此处处理不严谨，但也能用吧
                                {
                                    if (Library is not JsonObject LibraryObj) continue;
                                    var libJobj = LibraryObj.DeepClone().AsObject();
                                    if (libJobj["MMC-hint"] is not null)
                                    {
                                        libJobj.Add("hint", libJobj["MMC-hint"]?.DeepClone());
                                        libJobj.Remove("MMC-hint");
                                    }

                                    libs.Add(libJobj);
                                }

                            libJson.Merge(libs);
                            ModBase.Log($"[ModPack] 已应用 JSON-Patch {patchJson["uid"]} 的 Libraries");
                        }

                        // Tweakers
                        if (patchJson["+tweakers"] is not null)
                        {
                            tweakers = (string)patchJson["+tweakers"][0];
                            ModBase.Log($"[ModPack] 已应用 JSON-Patch {patchJson["uid"]} 的 Tweakers");
                        }

                        // AssetIndex
                        if (patchJson["assetIndex"] is not null)
                        {
                            assetIndex = patchJson["assetIndex"]?.DeepClone().AsObject();
                            ModBase.Log($"[ModPack] 已应用 JSON-Patch {patchJson["uid"]} 的 AssetIndex");
                        }

                        // minecraftArguments -> arguments.game
                        if (patchJson["minecraftArguments"] is not null)
                        {
                            foreach (var Arg in patchJson["minecraftArguments"].ToString().Split(" "))
                                gameArguments.Add(Arg);
                            packInfo.isMcArgsEdited = true;
                            ModBase.Log(
                                $"[ModPack] 已应用 JSON-Patch {patchJson["uid"]} 的 minecraftArguments 至 arguments.game");
                        }

                        // mainClass
                        if (patchJson["mainClass"] is not null)
                        {
                            mainClass = (string)patchJson["mainClass"];
                            ModBase.Log($"[ModPack] 已应用 JSON-Patch {patchJson["uid"]} 的 mainClass");
                        }

                        // Java 版本要求
                        // compatibleJavaMajors 表示该组件可以运行的所有 Java 主版本，安装后应能自动选用其中的版本
                        if (patchJson["compatibleJavaMajors"] is JsonArray javaMajors && javaMajors.Count > 0)
                        {
                            var majors = javaMajors
                                // Val 无法解析 JsonNode（会返回 0），必须先转为字符串
                                .Select(Java => (int)Math.Round(ModBase.Val(Java?.ToString())))
                                .Where(major => major > 0)
                                .Distinct()
                                .OrderBy(major => major)
                                .ToList();
                            if (majors.Count > 0)
                            {
                                if (javaMajorsSet is not null)
                                {
                                    // 多个组件同时声明时取集合交集；若无交集，则以当前补丁的要求为准
                                    var intersect = majors.Where(major => javaMajorsSet.Contains(major)).ToList();
                                    if (intersect.Count == 0)
                                    {
                                        ModBase.Log($"[ModPack] JSON-Patch {patchJson["uid"]} 要求的 Java 主版本（{string.Join(", ", majors)}）" +
                                                    $"与此前的要求（{string.Join(", ", javaMajorsSet)}）无交集，已使用当前补丁的要求");
                                        javaMajorsSet = majors;
                                    }
                                    else
                                    {
                                        javaMajorsSet = intersect;
                                    }
                                }
                                else
                                {
                                    javaMajorsSet = majors;
                                }

                                ModBase.Log($"[ModPack] JSON-Patch {patchJson["uid"]} 兼容的 Java 主版本：" +
                                            string.Join(", ", majors));
                            }
                        }
                    }

                    // Java 版本要求：以兼容集合的最低主版本作为最低要求，确保已安装的兼容 Java 能被自动选用；
                    // compatibleJavaMajors 按集合存储（含多补丁交集），启动时按成员资格挑选 Java
                    JsonObject javaVerJson = null;
                    if (javaMajorsSet is not null && javaMajorsSet.Count > 0)
                    {
                        var javaMajorMin = javaMajorsSet.Min();
                        string? javaComponent = javaMajorMin switch
                        {
                            8 => "jre-legacy",
                            17 => "java-runtime-gamma",
                            21 => "java-runtime-delta",
                            25 => "java-runtime-epsilon",
                            _ => null
                        };
                        javaVerJson = new JsonObject { { "majorVersion", javaMajorMin } };
                        if (javaComponent is not null) javaVerJson.Add("component", javaComponent);
                        javaMajorsJson = new JsonArray();
                        foreach (var major in javaMajorsSet) javaMajorsJson.Add(major);
                        ModBase.Log($"[ModPack] 整合包要求的 Java 主版本集合：{string.Join(", ", javaMajorsSet)}");
                    }

                    JsonObject jsonArguments = null;
                    if (!string.IsNullOrWhiteSpace(tweakers))
                    {
                        gameArguments.Add("--tweakClass");
                        gameArguments.Add(tweakers);
                    }

                    if (gameArguments is not null || jvmArguments is not null)
                    {
                        jvmArguments.Insert(0, "-Djava.library.path=${natives_directory}");
                        jvmArguments.Insert(1, "-Dminecraft.launcher.brand=${launcher_name}");
                        jvmArguments.Insert(2, "-Dminecraft.launcher.version=${launcher_version}");
                        jvmArguments.Insert(3, "-cp");
                        jvmArguments.Insert(4, "${classpath}");
                        jsonArguments = new JsonObject { { "game", gameArguments }, { "jvm", jvmArguments } };
                    }

                    packInfo.overridedJson = new JsonObject();
                    if (jsonArguments is not null)
                        packInfo.overridedJson.Add("arguments", jsonArguments);
                    if (mainClass is not null)
                        packInfo.overridedJson.Add("mainClass", mainClass);
                    if (assetIndex is not null)
                        packInfo.overridedJson.Add("assetIndex", assetIndex);
                    if (javaVerJson is not null)
                        packInfo.overridedJson.Add("javaVersion", javaVerJson);
                    if (javaMajorsJson is not null)
                        packInfo.overridedJson.Add("compatibleJavaMajors", javaMajorsJson);
                    if (libJson is not null)
                        packInfo.overridedJson.Add("libraries", libJson);
                }
                catch (Exception ex)
                {
                    // JSON Patches 应用失败导致实例不完整，终止安装并报错
                    throw new Exception("应用 MMC JSON-Patches 失败", ex);
                }
            } while (false);
        }

        #endregion

        catch (Exception ex)
        {
            throw new Exception("MMC 整合包安装信息存在问题", ex);
        }

        // 获取实例名
        var instanceName = packInstance.RegexSeek(@"(?<=\nname\=)[^\n]+") ?? "";
        var validate = new FolderNameValidator(Path.Combine(ModFolder.mcFolderSelected, "versions"));
        if (!validate.Validate(instanceName).IsValid)
            instanceName = "";
        if (string.IsNullOrEmpty(instanceName))
            instanceName = ModMain.MyMsgBoxInput(Lang.Text("Minecraft.Download.Modpack.InputInstanceName"), "", "",
                [validate]);
        if (string.IsNullOrEmpty(instanceName))
            throw new ModBase.CancelledException();
        // 解压
        var installTemp = ModMain.RequestTaskTempFolder();
        // 与 McInstance.PathInstance 及其余整合包流程保持一致（含末尾反斜杠），
        // 否则此处写入的个性化设置（图标、JVM 参数、启动命令等）会与读取端使用不同的配置键，导致设置不生效
        var versionFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\";
        var installLoaders = new List<LoaderBase>();
        installLoaders.Add(new LoaderTask<string, int>(Lang.Text("Minecraft.Download.Modpack.Stage.ExtractModpack"),
            task =>
        {
            ExtractModpackFiles(installTemp, fileAddress, task, 0.55d);
            CopyOverrideDirectory(Path.Combine(installTemp, archiveBaseFolder, "libraries"),
                Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName, "libraries"), task, 0.2d);
            CopyOverrideDirectory(Path.Combine(installTemp, archiveBaseFolder, ".minecraft"),
                Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName), task, 0.2d);

            #region instance.cfg

            // 读取 MMC 设置文件（#2655）
            try
            {
                var mMCSetupFile = Path.Combine(installTemp, archiveBaseFolder, "instance.cfg");
                // 将其中的等号替换为冒号，以符合 ini 文件格式
                if (File.Exists(mMCSetupFile))
                {
                    List<string> lines = [];
                    foreach (var Line in ModBase.ReadFile(mMCSetupFile).Split(new[] { "\r", "\n" },
                                 StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!Line.Contains("="))
                            continue;
                        lines.Add(Line.BeforeFirst("=") + ":" + Line.AfterFirst("="));
                    }

                    ModBase.WriteFile(mMCSetupFile, lines.Join("\r\n"));
                    // 读取文件
                    if (Convert.ToBoolean(ModBase.ReadIni(mMCSetupFile, "OverrideCommands",
                            false.ToString())))
                    {
                        var preLaunchCommand = ModBase.ReadIni(mMCSetupFile, "PreLaunchCommand");
                        if (!string.IsNullOrEmpty(preLaunchCommand))
                        {
                            preLaunchCommand = preLaunchCommand.Replace(@"\""", "\"")
                                .Replace("$INST_JAVA", "{java}java.exe").Replace(@"$INST_MC_DIR\", "{minecraft}")
                                .Replace("$INST_MC_DIR", "{minecraft}").Replace(@"$INST_DIR\", "{verpath}")
                                .Replace("$INST_DIR", "{verpath}").Replace("$INST_ID", "{name}")
                                .Replace("$INST_NAME", "{name}");
                            Config.Instance.PreLaunchCommand[versionFolder] = preLaunchCommand;
                            ModBase.Log("[ModPack] 迁移 MultiMC 实例独立设置：启动前执行命令：" + preLaunchCommand);
                        }
                    }

                    if (Convert.ToBoolean(ModBase.ReadIni(mMCSetupFile, "JoinServerOnLaunch",
                            false.ToString())))
                    {
                        var serverAddress = ModBase.ReadIni(mMCSetupFile, "JoinServerOnLaunchAddress")
                            .Replace(@"\""", "\"");
                        Config.Instance.ServerToEnter[versionFolder] = serverAddress;
                        ModBase.Log("[ModPack] 迁移 MultiMC 实例独立设置：自动进入服务器：" + serverAddress);
                    }

                    if (Convert.ToBoolean(ModBase.ReadIni(mMCSetupFile, "IgnoreJavaCompatibility",
                            false.ToString())))
                    {
                        Config.Instance.IgnoreJavaCompatibility[versionFolder] = true;
                        ModBase.Log("[ModPack] 迁移 MultiMC 实例独立设置：忽略 Java 兼容性警告");
                    }

                    var logo = Path.GetFileName(ModBase.ReadIni(mMCSetupFile, "iconKey"));
                    if (!string.IsNullOrEmpty(logo) && File.Exists($"{installTemp}{archiveBaseFolder}{logo}.png"))
                    {
                        States.Instance.IsLogoCustom[versionFolder] = true;
                        States.Instance.LogoPath[versionFolder] = @"PCL\Logo.png";
                        ModBase.CopyFile($"{installTemp}{archiveBaseFolder}{logo}.png",
                            $@"{ModFolder.mcFolderSelected}versions\{instanceName}\PCL\Logo.png");
                        ModBase.Log($"[ModPack] 迁移 MultiMC 实例独立设置：实例图标（{logo}.png）");
                    }

                    // JVM 参数
                    var jvmArgs = ModBase.ReadIni(mMCSetupFile, "JvmArgs");
                    if (!string.IsNullOrEmpty(jvmArgs))
                    {
                        if (Convert.ToBoolean(ModBase.ReadIni(mMCSetupFile, "OverrideJavaArgs",
                                false.ToString())))
                        {
                            Config.Instance.JvmArgs[versionFolder] = jvmArgs;
                            ModBase.Log("[ModPack] 迁移 MultiMC 实例独立设置：JVM 参数（覆盖）：" + jvmArgs);
                        }
                        else
                        {
                            jvmArgs = jvmArgs +
                                                           " " +
                                                               Config.Launch.JvmArgs;
                            Config.Instance.JvmArgs[versionFolder] = jvmArgs;
                            ModBase.Log("[ModPack] 迁移 MultiMC 实例独立设置：JVM 参数（追加）：" + jvmArgs);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"读取 MMC 配置文件失败（{installTemp}{archiveBaseFolder}instance.cfg）");
            }

            #endregion

            // 立即刷新该实例的配置存储：将队列中的个性化设置（图标、JVM 参数等）同步落盘，
            // 并让后续读取端基于最新文件内容重建缓存，避免写入延迟或进程退出时机导致设置丢失
            ((PCL.Core.App.Configuration.Storage.DynamicCacheConfigStorage)PCL.Core.App.Configuration.ConfigService
                .GetProvider(PCL.Core.App.Configuration.ConfigSource.GameInstance)).InvalidateCache(versionFolder);
        })
        {
            ProgressWeight = new FileInfo(fileAddress).Length / 1024d / 1024d / 6d,
            block = false
        }); // 每 6M 需要 1s
        // 构造实例安装请求
        if (packJson["components"] is null)
            throw new Exception(Lang.Text("Minecraft.Download.Modpack.MissingGameVersion.Generic"));
        var request = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\"
        };
        foreach (var Component in packJson["components"].AsArray())
            switch ((Component["uid"] ?? "").ToString() ?? "")
            {
                case "org.lwjgl":
                {
                    ModBase.Log("[ModPack] 已跳过 LWJGL 项");
                    break;
                }
                case "net.minecraft":
                {
                    request.minecraftName = (string)Component["version"];
                    break;
                }
                case "net.minecraftforge":
                {
                    if (Component["version"].ToString().StartsWithF("0."))
                        request.cleanroomVersion = (string)Component["version"];
                    else
                        request.forgeVersion = (string)Component["version"];

                    break;
                }
                case "net.neoforged":
                {
                    request.neoForgeVersion = (string)Component["version"];
                    break;
                }
                case "net.fabricmc.fabric-loader":
                {
                    request.fabricVersion = (string)Component["version"];
                    break;
                }
                case "org.quiltmc.quilt-loader":
                {
                    throw new Exception(Lang.Text("Minecraft.Download.Modpack.QuiltUnsupported"));
                }
            }

        if (packInfo is not null)
            request.mmcPackInfo = packInfo;
        // 构造加载器
        var mergeLoaders = ModDownloadLib.McInstallLoader(request);
        // 构造总加载器
        var loaders = new List<LoaderBase>();
        loaders.Add(new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.ModpackInstall"),
                installLoaders)
            { show = false, block = false, ProgressWeight = installLoaders.Sum(l => l.ProgressWeight) });
        loaders.Add(new LoaderCombo<string>(Lang.Text("Minecraft.Download.Modpack.Stage.GameInstall"), mergeLoaders)
            { show = false, ProgressWeight = mergeLoaders.Sum(l => l.ProgressWeight) });

        // 重复任务检查
        var loaderName = Lang.Text("Minecraft.Download.Modpack.Task.MmcInstall", instanceName);
        if (loaderTaskbar.Any(l => (l.name ?? "") == (loaderName ?? "")))
        {
            HintService.Hint(Lang.Text("Minecraft.Download.Modpack.Installing"), HintType.Error);
            throw new ModBase.CancelledException();
        }

        // 启动
        var loader = new LoaderCombo<string>(loaderName, loaders) { OnStateChanged = ModDownloadLib.McInstallState };
        loader.Start(request.targetInstanceFolder);
        LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModBase.RunInUi(() => ModMain.frmMain.PageChange(FormMain.PageType.TaskManager));
        return loader;
    }

    #endregion
}
