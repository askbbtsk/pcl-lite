using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FluentValidation;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.Minecraft.Profile;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Instances;
using PCL.Core.UI;
using PCL.Core.Utils.Validate;
using FileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageInstanceOverall
{


    // 实例分类
    private void ComboDisplayType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!(isLoad && ModAnimation.AniControlEnabled == 0))
            return;
        if (ComboDisplayType.SelectedIndex != 1)
        {
            // 改为不隐藏
            try
            {
                // 若设置分类为可安装 Mod，则显示正常的 Mod 管理页面
                States.Instance.CardType[PageInstanceLeft.McInstance.PathInstance] = ComboDisplayType.SelectedIndex;
                PageInstanceLeft.McInstance.displayType = (McInstanceCardType)States.Instance.CardType[PageInstanceLeft.McInstance.PathInstance];
                ModMain.frmInstanceLeft.RefreshModDisabled();

                ModBase.WriteIni(ModFolder.mcFolderSelected + "PCL.ini", "InstanceCache", ""); // 要求刷新缓存
                ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                    ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    $"修改实例分类失败（{PageInstanceLeft.McInstance.Name}）",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
            }

            Reload(); // 更新 “打开 Mod 文件夹” 按钮
        }
        else
        {
            // 改为隐藏
            try
            {
                if (!States.Hint.HideGameInstance)
                {
                if (ModMain.MyMsgBox(
                        Lang.Text("Instance.Overall.Hide.ConfirmMessage"), Lang.Text("Instance.Overall.Hide.ConfirmTitle"), button2: Lang.Text("Common.Action.Cancel")) != 1)
                    {
                        ComboDisplayType.SelectedIndex = 0;
                        return;
                    }

                    States.Hint.HideGameInstance = true;
                }

                States.Instance.CardType[PageInstanceLeft.McInstance.PathInstance] =
                    (int)McInstanceCardType.Hidden;
                ModBase.WriteIni(ModFolder.mcFolderSelected + "PCL.ini", "InstanceCache", ""); // 要求刷新缓存
                ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                    ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    $"隐藏实例 {PageInstanceLeft.McInstance.Name} 失败",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
            }
        }
    }


    // 更改描述
    private void BtnDisplayDesc_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var oldInfo = States.Instance.CustomInfo[PageInstanceLeft.McInstance.PathInstance];
            var newInfo = ModMain.MyMsgBoxInput(Lang.Text("Instance.Overall.Description.EditTitle"), Lang.Text("Instance.Overall.Description.EditMessage"), oldInfo,
                [], Lang.Text("Instance.Overall.Description.Default"));
            if (newInfo is not null && (oldInfo ?? "") != (newInfo ?? ""))
                States.Instance.CustomInfo[PageInstanceLeft.McInstance.PathInstance] = newInfo;
            PageInstanceLeft.McInstance = new McInstance(PageInstanceLeft.McInstance.Name).Load();
            Reload();
            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"实例 {PageInstanceLeft.McInstance.Name} 描述更改失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }
    }


    // 重命名实例
    private void BtnDisplayRename_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // 确认输入的新名称
            var oldName = PageInstanceLeft.McInstance.Name;
            var oldPath = PageInstanceLeft.McInstance.PathInstance;
            var versionsPath = Directory.GetParent(Path.TrimEndingDirectorySeparator(oldPath))!.FullName;
            var minecraftPath = Directory.GetParent(versionsPath)!.FullName + Path.DirectorySeparatorChar;
            // 修改此部分的同时修改快速安装的实例名检测*
            var newName = ModMain.MyMsgBoxInput(Lang.Text("Instance.Overall.Name.EditTitle"), "", oldName,
                [new FolderNameValidator(versionsPath, ignoreCase: false)]);
            if (string.IsNullOrWhiteSpace(newName))
                return;
            var newPath = Path.Combine(versionsPath, newName);
            // 获取临时中间名，以防止仅修改大小写的重命名失败
            var tempName = ".pcl-rename-" + Guid.NewGuid().ToString("N");
            var tempPath = Path.Combine(versionsPath, tempName);
            var isCaseChangedOnly = string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase);
            // 重新加载实例 Json 信息，避免 HMCL 项被合并
            JsonObject jsonObject;
            try
            {
                jsonObject = (JsonObject)ModBase.GetJson(ModBase.ReadFile(PageInstanceLeft.McInstance.PathInstance +
                                                                       PageInstanceLeft.McInstance.Name + ".json"));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "重命名读取 Json 时失败");
                jsonObject = PageInstanceLeft.McInstance.JsonObject;
            }

            // 重命名主文件夹
            FileSystem.RenameDirectory(oldPath, tempName);
            FileSystem.RenameDirectory(tempPath, newName);
            // 清理 ini 缓存
            ModBase.IniClearCache(Path.Combine(PageInstanceLeft.McInstance.PathIndie, "options.txt"));
            // 重命名 Jar 文件与 natives 文件夹
            // 不能进行遍历重命名，否则在实例名很短的时候容易误伤其他文件（Meloong-Git/#6443）
            if (Directory.Exists(Path.Combine(newPath, $"{oldName}-natives")))
            {
                if (isCaseChangedOnly)
                {
                    var temporaryNatives = ".pcl-natives-" + Guid.NewGuid().ToString("N");
                    FileSystem.RenameDirectory(Path.Combine(newPath, $"{oldName}-natives"), temporaryNatives);
                    FileSystem.RenameDirectory(Path.Combine(newPath, temporaryNatives), $"{newName}-natives");
                }
                else
                {
                    ModBase.DeleteDirectory(Path.Combine(newPath, $"{newName}-natives"));
                    FileSystem.RenameDirectory(Path.Combine(newPath, $"{oldName}-natives"), $"{newName}-natives");
                }
            }

            if (File.Exists(Path.Combine(newPath, $"{oldName}.jar")))
            {
                if (isCaseChangedOnly)
                {
                    var temporaryJar = ".pcl-rename-" + Guid.NewGuid().ToString("N") + ".jar";
                    FileSystem.RenameFile(Path.Combine(newPath, $"{oldName}.jar"), temporaryJar);
                    FileSystem.RenameFile(Path.Combine(newPath, temporaryJar), $"{newName}.jar");
                }
                else
                {
                    File.Delete(Path.Combine(newPath, $"{newName}.jar"));
                    FileSystem.RenameFile(Path.Combine(newPath, $"{oldName}.jar"), $"{newName}.jar");
                }
            }

            // 替换实例设置文件中的路径
            if (File.Exists(Path.Combine(newPath, "PCL", "Setup.ini")))
                ModBase.WriteFile(Path.Combine(newPath, "PCL", "Setup.ini"),
                    ModBase.ReadFile(Path.Combine(newPath, "PCL", "Setup.ini")).Replace(oldPath,
                        newPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            ModBase.IniClearCache(Path.Combine(newPath, "PCL", "Setup.ini"));
            // 更改已选中的实例
            if ((ModBase.ReadIni(minecraftPath + "PCL.ini", "Version") ?? "") == (oldName ?? ""))
                ModBase.WriteIni(minecraftPath + "PCL.ini", "Version", newName);
            // 写入实例 Json，并删除旧的 Json
            try
            {
                jsonObject["id"] = newName;
                ModBase.WriteFile(Path.Combine(newPath, $"{newName}.json"), jsonObject.ToString());
                if (!isCaseChangedOnly)
                    File.Delete(Path.Combine(newPath, $"{oldName}.json"));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "重命名实例 Json 失败");
            }

            // 刷新与提示
            HintService.Hint(Lang.Text("Instance.Overall.Name.RenameSuccess"), HintType.Success);
            PageInstanceLeft.McInstance = new McInstance(newPath).Load();
            if (ModInstanceList.McMcInstanceSelected is not null &&
                ModInstanceList.McMcInstanceSelected.Equals(PageInstanceLeft.McInstance))
                ModBase.WriteIni(minecraftPath + "PCL.ini", "Version", newName);
            Reload();
            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, minecraftPath,
                ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "重命名实例失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }
    }


    // 实例图标
    private void ComboDisplayLogo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!(isLoad && ModAnimation.AniControlEnabled == 0))
            return;
        // 选择 自定义 时修改图片
        try
        {
            if (ReferenceEquals(ComboDisplayLogo.SelectedItem, ItemDisplayLogoCustom))
            {
                var fileName = SystemDialogs.SelectFile(Lang.Text("Instance.Overall.Icon.SelectFile.Filter"), Lang.Text("Instance.Overall.Icon.SelectFile.Title"));
                if (string.IsNullOrEmpty(fileName))
                {
                    Reload(); // 还原选项
                    return;
                }

                ModBase.CopyFile(fileName, PageInstanceLeft.McInstance.PathInstance + @"PCL\Logo.png");
            }
            else
            {
                File.Delete(PageInstanceLeft.McInstance.PathInstance + @"PCL\Logo.png");
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"更改自定义实例图标失败（{PageInstanceLeft.McInstance.Name}）",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }

        // 进行更改
        try
        {
            string newLogo = ((MyComboBoxItem)ComboDisplayLogo.SelectedItem).Tag?.ToString();
            States.Instance.LogoPath[PageInstanceLeft.McInstance.PathInstance] = newLogo;
            States.Instance.IsLogoCustom[PageInstanceLeft.McInstance.PathInstance] = !string.IsNullOrEmpty(newLogo);
            // 刷新显示
            ModBase.WriteIni(ModFolder.mcFolderSelected + "PCL.ini", "InstanceCache", ""); // 要求刷新缓存
            PageInstanceLeft.McInstance = new McInstance(PageInstanceLeft.McInstance.Name).Load();
            Reload();
            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"更改实例图标失败（{PageInstanceLeft.McInstance.Name}）",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }
    }


    // 收藏夹
    private void BtnDisplayStar_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            States.Instance.Starred[PageInstanceLeft.McInstance.PathInstance] = !PageInstanceLeft.McInstance.IsStar;
            PageInstanceLeft.McInstance = new McInstance(PageInstanceLeft.McInstance.Name).Load();
            Reload();
            ModInstanceList.mcInstanceListForceRefresh = true;
            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"实例 {PageInstanceLeft.McInstance.Name} 收藏状态更改失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }
    }




    // 实例文件夹
    private void BtnFolderVersion_Click(object sender, MouseButtonEventArgs mouseButtonEventArgs)
    {
        OpenVersionFolder(PageInstanceLeft.McInstance);
    }


    public static void OpenVersionFolder(McInstance version)
    {
        ModBase.OpenExplorer(version.PathInstance);
    }


    // 存档文件夹
    private void BtnFolderSaves_Click(object sender, MouseButtonEventArgs mouseButtonEventArgs)
    {
        var folderPath = PageInstanceLeft.McInstance.PathIndie + @"saves\";
        Directory.CreateDirectory(folderPath);
        ModBase.OpenExplorer(folderPath);
    }


    // Mod 文件夹
    private void BtnFolderMods_Click(object sender, MouseButtonEventArgs mouseButtonEventArgs)
    {
        var folderPath = PageInstanceLeft.McInstance.PathIndie + @"mods\";
        Directory.CreateDirectory(folderPath);
        ModBase.OpenExplorer(folderPath);
    }




    // 导出启动脚本
    private void BtnManageScript_Click(object sender, MouseButtonEventArgs mouseButtonEventArgs)
    {
        try
        {
            // 弹窗要求指定脚本的保存位置
            var savePath = SystemDialogs.SelectSaveFile(Lang.Text("Instance.Overall.Script.SelectSaveTitle"), "启动 " + PageInstanceLeft.McInstance.Name + ".bat",
                Lang.Text("Instance.Overall.Script.FileFilter"));
            if (string.IsNullOrEmpty(savePath))
                return;
            // 检查中断（等玩家选完弹窗指不定任务就结束了呢……）
            if (ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading)
            {
                HintService.Hint(Lang.Text("Instance.Overall.Script.WaitForLaunchTask"), HintType.Error);
                return;
            }

            // 生成脚本
            if (ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions
                    { SaveBatch = savePath, instance = PageInstanceLeft.McInstance }))
            {
                if (ProfileService.Current?.ProfileType == PCL.Core.Minecraft.Profile.Models.ProfileType.Offline)
                    HintService.Hint(Lang.Text("Instance.Overall.Script.Exporting"));
                else
                    HintService.Hint(Lang.Text("Instance.Overall.Script.ExportingWarning"));
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"导出启动脚本失败（{PageInstanceLeft.McInstance.Name}）",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }
    }


    // 补全文件
    private void BtnManageCheck_Click(object sender, MouseButtonEventArgs e)
    {
        if (!RestrictedFeaturePolicy.Check("minecraft-repair")) return;
        try
        {
            // 忽略文件检查提示
            if ((bool)ModLibrary.ShouldIgnoreFileCheck(PageInstanceLeft.McInstance))
            {
                HintService.Hint(Lang.Text("Instance.Overall.Repair.DisableVerificationHint"));
                return;
            }

            // 重复任务检查
            var taskName = PageInstanceLeft.McInstance.Name + " " + Lang.Text("Instance.Overall.Repair.TaskName");
            foreach (var OngoingLoader in ModLoader.loaderTaskbar)
            {
                if ((OngoingLoader.name ?? "") != (taskName ?? ""))
                    continue;
                HintService.Hint(Lang.Text("Instance.Overall.Repair.Processing"), HintType.Error);
                return;
            }

            // 启动
            var loader = new ModLoader.LoaderCombo<string>(taskName,
                ModDownload.DlClientFix(PageInstanceLeft.McInstance, true,
                    ModDownload.AssetsIndexExistsBehaviour.AlwaysDownload));
            loader.OnStateChanged = _ =>
            {
                switch (loader.State)
                {
                    case ModBase.LoadState.Finished:
                    {
                        HintService.Hint(
                            Lang.Text("Instance.Overall.Repair.Success.WithTaskName", taskName), HintType.Success);
                        break;
                    }
                    case ModBase.LoadState.Failed:
                    {
                        HintService.Hint(
                            Lang.Text("Instance.Overall.Repair.Failed.WithDetail", taskName, loader.Error.ToString()),
                            HintType.Error);
                        break;
                    }
                    case ModBase.LoadState.Aborted:
                    {
                        HintService.Hint(
                            Lang.Text("Instance.Overall.Repair.Cancelled.WithTaskName", taskName));
                        break;
                    }
                }
            };
            loader.Start(PageInstanceLeft.McInstance.Name);
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"尝试补全文件失败（{PageInstanceLeft.McInstance.Name}）",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }
    }


    // 重置
    private void BtnManageRestore_Click(object sender, MouseButtonEventArgs e)
    {
        if (!RestrictedFeaturePolicy.Check("minecraft-reset")) return;
        try
        {
            var currentVersion = PageInstanceLeft.McInstance.Info;
            if (!(currentVersion.Drop == 99) &&
                McVersionComparer.CompareVersion(currentVersion.VanillaName, "1.5.2") == -1 && currentVersion.HasForge)
            {
                HintService.Hint(Lang.Text("Instance.Overall.Reset.NotSupported"));
                return;
            }

            if (currentVersion.HasQuilt)
            {
                HintService.Hint(Lang.Text("Instance.Overall.Reset.QuiltUnsupported"));
                return;
            }

            // 确认操作
            if (ModMain.MyMsgBox(
                    Lang.Text("Instance.Overall.Reset.ConfirmMessage", PageInstanceLeft.McInstance.Name), Lang.Text("Instance.Overall.Reset.ConfirmTitle"), Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")) == 2)
                return;

            // 备份实例核心文件
            ModBase.CopyFile(PageInstanceLeft.McInstance.PathInstance + PageInstanceLeft.McInstance.Name + ".json",
                PageInstanceLeft.McInstance.PathInstance + @"PCLInstallBackups\" + PageInstanceLeft.McInstance.Name +
                ".json");
            ModBase.CopyFile(PageInstanceLeft.McInstance.PathInstance + PageInstanceLeft.McInstance.Name + ".jar",
                PageInstanceLeft.McInstance.PathInstance + @"PCLInstallBackups\" + PageInstanceLeft.McInstance.Name +
                ".jar");
            // 提交安装申请
            var request = new ModDownloadLib.McInstallRequest
            {
                targetInstanceName = PageInstanceLeft.McInstance.Name,
                targetInstanceFolder = $@"{ModFolder.mcFolderSelected}versions\{PageInstanceLeft.McInstance.Name}\",
                minecraftName = currentVersion.VanillaName,
                optiFineEntry = currentVersion.HasOptiFine
                    ? new ModDownload.DlOptiFineListEntry
                    {
                        Inherit = currentVersion.VanillaName,
                        DisplayName = currentVersion.VanillaName + " " + currentVersion.OptiFine
                    }
                    : null,
                forgeEntry = currentVersion.HasForge
                    ? new ModDownload.DlForgeVersionEntry(currentVersion.Forge, null, currentVersion.VanillaName)
                        { Category = "installer" }
                    : null,
                forgeVersion = currentVersion.HasForge ? currentVersion.Forge : null,
                neoForgeVersion = currentVersion.HasNeoForge ? currentVersion.NeoForge : null,
                cleanroomVersion = currentVersion.HasCleanroom ? currentVersion.Cleanroom : null,
                fabricVersion = currentVersion.HasFabric ? currentVersion.Fabric : null,
                liteLoaderEntry = currentVersion.HasLiteLoader
                    ? new ModDownload.DlLiteLoaderListEntry { Inherit = currentVersion.VanillaName }
                    : null,
                legacyFabricVersion = currentVersion.HasLegacyFabric ? currentVersion.LegacyFabric : null
            };
            // .MinecraftJson = CurrentVersion.McName,
            if (!ModDownloadLib.McInstall(request, Lang.Text("Common.Action.Reset")))
                return;
            ModMain.frmMain.PageChange(new FormMain.PageStackData { page = FormMain.PageType.Launch });
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"重置实例 {PageInstanceLeft.McInstance.Name} 失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }
    }


    // 测试游戏
    private void BtnManageTest_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions
                { instance = PageInstanceLeft.McInstance, IsTest = true });
            ModMain.frmMain.PageChange(FormMain.PageType.Launch);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "测试游戏失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }
    }


    private void BtnManageTestCopy_Click(object sender, MouseButtonEventArgs e)
    {
        var source = PageInstanceLeft.McInstance;
        if (source is null) return;
        var sourcePath = Path.GetFullPath(source.PathInstance).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gamePath = Path.GetFullPath(source.PathIndie).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(sourcePath, gamePath, StringComparison.OrdinalIgnoreCase))
        {
            HintService.Hint(Lang.Text("Instance.Overall.Manage.TestCopy.IsolationRequired"), HintType.Error);
            return;
        }
        var isRunning = ModWatcher.mcWatcherList.Any(watcher =>
            (watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running)
            && watcher.version is not null
            && string.Equals(Path.GetFullPath(watcher.version.PathInstance).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                sourcePath, StringComparison.OrdinalIgnoreCase));
        if (isRunning)
        {
            HintService.Hint(Lang.Text("Instance.PackUpdate.Error.GameRunning"), HintType.Error);
            return;
        }

        var newName = ModMain.MyMsgBoxInput(Lang.Text("Instance.Overall.Manage.TestCopy.Name.Title"),
            Lang.Text("Instance.Overall.Manage.TestCopy.Name.Message"), source.Name + " Test",
            [new FolderNameValidator(Path.Combine(ModFolder.mcFolderSelected, "versions"), ignoreCase: true)]);
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (ModMain.MyMsgBox(Lang.Text("Instance.Overall.Manage.TestCopy.Confirm", newName),
                Lang.Text("Instance.Overall.Manage.TestCopy.Name.Title"), Lang.Text("Common.Action.Confirm"),
                Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;

        var destination = Path.Combine(Path.GetDirectoryName(sourcePath)!, newName);
        var sourceName = source.Name;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                InstanceTestCopyService.Create(sourcePath, destination, sourceName, newName);
                var copied = new McInstance(newName);
                Config.Instance.IndieV2[copied.PathInstance] = true;
                ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                    ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Instance.Overall.Manage.TestCopy.Success", newName), HintType.Success));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "创建 Minecraft 测试副本失败", ModBase.LogLevel.Hint);
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Instance.Overall.Manage.TestCopy.Failed"), HintType.Error));
            }
        });
    }

    // 删除实例
    // 修改此代码时，同时修改 PageSelectRight 中的代码
    private void BtnManageDelete_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var isShiftPressed = !Config.Launch.DeleteToRecycleBin || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            var isIsolatedInstance =
                PageInstanceLeft.McInstance.state != McInstanceState.Error &&
                !string.Equals(
                    PageInstanceLeft.McInstance.PathIndie,
                    ModFolder.mcFolderSelected,
                    StringComparison.OrdinalIgnoreCase
                );

            var confirmMessageKey = (isIsolatedInstance, isShiftPressed) switch
            {
                (true, true) => "Instance.Overall.Delete.ConfirmMessageIsolatedPermanent",
                (true, false) => "Instance.Overall.Delete.ConfirmMessageIsolated",
                (false, true) => "Instance.Overall.Delete.ConfirmMessagePermanent",
                (false, false) => "Instance.Overall.Delete.ConfirmMessage"
            };

            var confirmResult = ModMain.MyMsgBox(
                Lang.Text(confirmMessageKey, PageInstanceLeft.McInstance.Name),
                Lang.Text("Instance.Overall.Delete.ConfirmTitle"),
                button2: Lang.Text("Common.Action.Cancel"),
                isWarn: isIsolatedInstance || isShiftPressed
            );

            switch (confirmResult)
            {
                case 1:
                {
                    var instancePath = PageInstanceLeft.McInstance.PathInstance;
                    var instanceName = PageInstanceLeft.McInstance.Name;
                    ModBase.IniClearCache(Path.Combine(PageInstanceLeft.McInstance.PathIndie, "options.txt"));
                    ((DynamicCacheConfigStorage)ConfigService.GetProvider(ConfigSource.GameInstance)).InvalidateCache(
                        instancePath);
                    if (isShiftPressed)
                    {
                        ModBase.DeleteDirectory(instancePath);
                        HintService.Hint(Lang.Text("Instance.Overall.Delete.PermanentSuccess", instanceName),
                            HintType.Success);
                    }
                    else
                    {
                        FileSystem.DeleteDirectory(instancePath, UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin);
                        HintService.Hint(Lang.Text("Instance.Overall.Delete.RecycleBinSuccess", instanceName),
                            HintType.Success);
                    }

                    break;
                }
                case 2:
                {
                    return;
                }
            }

            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\");
            ModMain.frmMain.PageBack();
        }
        catch (OperationCanceledException ex)
        {
            ModBase.Log(ex, "删除实例 " + PageInstanceLeft.McInstance.Name + " 被主动取消");
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"删除实例 {PageInstanceLeft.McInstance.Name} 失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Overall.Error.OperationFailed"));
        }
    }


    // 修补核心
    private void BtnManagePatch_Click(object sender, MouseButtonEventArgs e)
    {
        switch (ModMain.MyMsgBox(
                    Lang.Text("Instance.Overall.Patch.ConfirmMessage", PageInstanceLeft.McInstance.Name),
                    Lang.Text("Instance.Overall.Patch.ConfirmTitle"), button2: Lang.Text("Common.Action.Cancel")))
        {
            case 1:
            {
                var userInput = SystemDialogs.SelectFile(Lang.Text("Instance.Overall.Patch.SelectFile.Filter"), Lang.Text("Instance.Overall.Patch.SelectFile.Title"));
                if (userInput is null || string.IsNullOrWhiteSpace(userInput))
                    return;
                HintService.Hint(Lang.Text("Instance.Overall.Patch.Patching"));
                ModBase.RunInNewThread(() =>
                {
                    var core = new GameCore(PageInstanceLeft.McInstance.PathInstance + PageInstanceLeft.McInstance.Name +
                                            ".jar");
                    core.AddToCore(userInput);
                    HintService.Hint(Lang.Text("Instance.Overall.Patch.Success"), HintType.Success);
                    Config.Instance.DisableAssetVerifyV2[PageInstanceLeft.McInstance.PathInstance] = true;
                });
                break;
            }
            case 2:
            {
                return;
            }
        }
    }

}
