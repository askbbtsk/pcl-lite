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

}
