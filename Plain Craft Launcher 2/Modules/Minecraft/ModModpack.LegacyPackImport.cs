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

}
