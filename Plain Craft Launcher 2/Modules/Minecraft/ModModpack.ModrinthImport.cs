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



    private static LoaderCombo<string> InstallPackModrinth(string fileAddress, ZipArchive archive,
        string archiveBaseFolder, string instanceName = null, string logo = null, string resourceId = null,
        bool isOnlineInstall = false, bool forceIsolated = false)
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

}
