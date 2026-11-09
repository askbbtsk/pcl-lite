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
        string resourceId = null, bool isOnlineInstall = false, bool forceIsolated = false)
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
                        isOnlineInstall, forceIsolated);
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
                        isOnlineInstall, forceIsolated);
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
            var declared = ModBase.ReadIni(overridesIni, "VersionArgumentIndieV2", "");
            var legacy = ModBase.ReadIni(overridesIni, "VersionArgumentIndie", "");
            var isolated = PackExportContent.ReadIsolation(declared, legacy);
            ModBase.CopyFile(overridesIni, versionIni);
            ModBase.WriteIni(versionIni, "VersionArgumentIndie", isolated ? "1" : "2");
            ModBase.WriteIni(versionIni, "VersionArgumentIndieV2", isolated.ToString());
        }
        else
        {
            // A second, empty client-overrides pass must not erase an explicit choice from overrides.
            var isolated = PackExportContent.ReadIsolation(ModBase.ReadIni(versionIni, "VersionArgumentIndieV2", ""),
                ModBase.ReadIni(versionIni, "VersionArgumentIndie", ""));
            ModBase.WriteIni(versionIni, "VersionArgumentIndie", isolated ? "1" : "2");
            ModBase.WriteIni(versionIni, "VersionArgumentIndieV2", isolated.ToString());
        }

        ModBase.IniClearCache(versionIni); // 重置缓存，避免被安装过程中写入的 ini 覆盖
    }

}
