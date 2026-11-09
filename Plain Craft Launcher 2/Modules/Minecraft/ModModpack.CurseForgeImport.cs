using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Packs;
using PCL.Core.UI;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.ModLoader;
using PCL.Core.Utils;

namespace PCL;

public static partial class ModModpack
{


    private static LoaderCombo<string> InstallPackCurseForge(string fileAddress, ZipArchive archive,
        string archiveBaseFolder, string instanceName = null, string logo = null, string resourceId = null,
        bool isOnlineInstall = false, bool forceIsolated = false)
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
            ApplyPackIsolationLayout(versionFolder, authoredPaths, forceIsolated);
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

}
