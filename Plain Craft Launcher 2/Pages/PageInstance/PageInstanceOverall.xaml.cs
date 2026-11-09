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
    private ModLoader.LoaderCombo<int> instanceInfoLoader;

    private bool isLoad;

    public MyListItem itemVersion;
    private MyCompItem modpackCompItem;

    public PageInstanceOverall()
    {
        InitializeComponent();
        Loaded += PageSetupLaunch_Loaded;
        LabInfoLoading.Text = Lang.Text("Instance.Overall.Info.Loading");
        // Handles
        ComboDisplayType.SelectionChanged += ComboDisplayType_SelectionChanged;
        BtnDisplayDesc.Click += BtnDisplayDesc_Click;
        BtnDisplayRename.Click += BtnDisplayRename_Click;
        ComboDisplayLogo.SelectionChanged += ComboDisplayLogo_SelectionChanged;
        BtnDisplayStar.Click += BtnDisplayStar_Click;
        BtnFolderVersion.Click += BtnFolderVersion_Click;
        BtnFolderSaves.Click += BtnFolderSaves_Click;
        BtnFolderMods.Click += BtnFolderMods_Click;
        BtnManageScript.Click += BtnManageScript_Click;
        BtnManageCheck.Click += BtnManageCheck_Click;
        BtnManageRestore.Click += BtnManageRestore_Click;
        BtnManageTest.Click += BtnManageTest_Click;
        BtnManageTestCopy.Click += BtnManageTestCopy_Click;
        BtnManageDelete.Click += BtnManageDelete_Click;
        BtnManagePatch.Click += BtnManagePatch_Click;
        BtnPackUpdate.Click += BtnPackUpdate_Click;
        BtnPackUpdateLocal.Click += BtnPackUpdateLocal_Click;
        BtnCreateRestorePoint.Click += BtnCreateRestorePoint_Click;
    }

    private void PageSetupLaunch_Loaded(object sender, RoutedEventArgs e)
    {
        // 重复加载部分
        PanBack.ScrollToHome();

        // 更新设置
        ItemDisplayLogoCustom.Tag = @"PCL\Logo.png";
        Reload();

        // 非重复加载部分
        if (isLoad)
            return;
        isLoad = true;
        PanDisplay.TriggerForceResize();
    }

    /// <summary>
    ///     确保当前页面上的信息已正确显示。
    /// </summary>
    private void Reload()
    {
        ModAnimation.AniControlEnabled += 1;

        var instance = PageInstanceLeft.McInstance;
        // 刷新设置项目
        ComboDisplayType.SelectedIndex = States.Instance.CardType[instance.PathInstance];
        BtnDisplayStar.Text = instance.IsStar ? Lang.Text("Instance.Overall.Unfavorite") : Lang.Text("Instance.Overall.Favorite");
        BtnFolderMods.Visibility = instance.Modable ? Visibility.Visible : Visibility.Collapsed;
        BtnManageTestCopy.Visibility = string.Equals(Path.GetFullPath(instance.PathIndie).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(instance.PathInstance).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        PanPackUpdate.Visibility = ModModpack.HasPackUpdateBaseline(instance.PathInstance)
            ? Visibility.Visible : Visibility.Collapsed;
        var isolatedPath = string.Equals(
            Path.GetFullPath(instance.PathIndie).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(instance.PathInstance).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        PanRestorePoints.Visibility = isolatedPath ? Visibility.Visible : Visibility.Collapsed;
        if (isolatedPath) RefreshRestorePoints(instance);
        // 刷新实例显示
        PanDisplayItem.Children.Clear();
        itemVersion = PageSelectRight.McVersionListItem(instance);
        itemVersion.IsHitTestVisible = false;
        PanDisplayItem.Children.Add(itemVersion);
        ModMain.frmMain.PageNameRefresh();
        // 刷新实例信息
        GetInstanceInfo();
        // 刷新实例图标
        ComboDisplayLogo.SelectedIndex = 0;
        var logo = States.Instance.LogoPath[instance.PathInstance];
        var logoCustom = States.Instance.IsLogoCustom[instance.PathInstance];
        if (logoCustom)
            foreach (MyComboBoxItem Selection in ComboDisplayLogo.Items)
                if (Equals(Selection.Tag, logo) ||
                    (Equals(Selection.Tag, @"PCL\Logo.png") &&
                     logo.EndsWith(@"PCL\Logo.png")))
                {
                    ComboDisplayLogo.SelectedItem = Selection;
                    break;
                }

        ModAnimation.AniControlEnabled -= 1;
    }

    public void RefreshPackDetails() => Reload();

    private void BtnPackUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null || !ModModpack.HasPackUpdateBaseline(instance.PathInstance))
        {
            HintService.Hint(Lang.Text("Instance.PackUpdate.Error.NoBaseline"), HintType.Error);
            return;
        }
        ModModpack.UpdatePackFromOnline(instance);
    }

    private void BtnPackUpdateLocal_Click(object sender, MouseButtonEventArgs e)
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null || !ModModpack.HasPackUpdateBaseline(instance.PathInstance))
        {
            HintService.Hint(Lang.Text("Instance.PackUpdate.Error.NoBaseline"), HintType.Error);
            return;
        }
        ModModpack.UpdatePackFromLocal(instance);
    }

    private void BtnCreateRestorePoint_Click(object sender, MouseButtonEventArgs e)
    {
        var instance = PageInstanceLeft.McInstance;
        if (instance is null) return;
        var instanceDirectory = instance.PathIndie;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                ModWorldBackups.CreateRestorePoint(instanceDirectory, "Manual restore point");
                ModBase.RunInUi(() =>
                {
                    HintService.Hint(Lang.Text("Instance.RestorePoint.Created"), HintType.Success);
                    RefreshRestorePoints(PageInstanceLeft.McInstance);
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "创建实例还原点失败", ModBase.LogLevel.Hint);
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Instance.RestorePoint.Failed"), HintType.Error));
            }
        });
    }

    private void RefreshRestorePoints(McInstance instance)
    {
        PanRestorePointList.Children.Clear();
        IReadOnlyList<PCL.Core.Minecraft.Backups.BackupSnapshot> snapshots;
        try { snapshots = ModWorldBackups.ListRestorePoints(instance.PathIndie); }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取实例还原点列表失败", ModBase.LogLevel.Hint);
            PanRestorePointList.Children.Add(new TextBlock { Text = Lang.Text("Instance.RestorePoint.Failed"), Margin = new Thickness(0, 4, 0, 0) });
            return;
        }

        if (snapshots.Count == 0)
            PanRestorePointList.Children.Add(new TextBlock { Text = Lang.Text("Instance.RestorePoint.Empty"), Margin = new Thickness(0, 4, 0, 0) });

        foreach (var snapshot in snapshots.OrderByDescending(static item => item.CreatedUtc))
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new TextBlock
            {
                Text = Lang.Text("Instance.RestorePoint.Item", Lang.Date(snapshot.CreatedUtc.LocalDateTime, "g"), snapshot.Files.Count, ModWorldBackups.DescribeReason(snapshot)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            row.Children.Add(info);
            var restore = new MyButton
            {
                Text = Lang.Text("Instance.RestorePoint.Restore"),
                MinWidth = 120,
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0)
            };
            Grid.SetColumn(restore, 1);
            restore.Click += (_, _) => RestorePoint(instance, snapshot);
            var compare = new MyIconButton
            {
                SvgIcon = "lucide/git-compare",
                ToolTip = Lang.Text("Instance.RestorePoint.Compare"),
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(compare, 2);
            compare.Click += (_, _) => CompareRestorePoint(instance, snapshot);            var delete = new MyIconButton
            {
                SvgIcon = "lucide/trash-2",
                ToolTip = Lang.Text("Instance.RestorePoint.Delete"),
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(delete, 3);
            delete.Click += (_, _) => DeleteRestorePoint(instance, snapshot);
            row.Children.Add(delete);
            row.Children.Add(compare);
            row.Children.Add(restore);
            PanRestorePointList.Children.Add(row);
        }

    }

    private void CompareRestorePoint(McInstance instance, PCL.Core.Minecraft.Backups.BackupSnapshot snapshot)
    {
        var instanceDirectory = instance.PathIndie;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var differences = ModWorldBackups.CompareRestorePoint(instanceDirectory, snapshot.Id);
                var added = differences.Count(item => item.Kind == PCL.Core.Minecraft.Backups.BackupDifferenceKind.Added);
                var modified = differences.Count(item => item.Kind == PCL.Core.Minecraft.Backups.BackupDifferenceKind.Modified);
                var removed = differences.Count(item => item.Kind == PCL.Core.Minecraft.Backups.BackupDifferenceKind.Removed);
                var details = differences.Take(20).Select(item => Lang.Text("Instance.RestorePoint.Compare.Entry",
                    item.Kind switch
                    {
                        PCL.Core.Minecraft.Backups.BackupDifferenceKind.Added => Lang.Text("Instance.RestorePoint.Compare.Added"),
                        PCL.Core.Minecraft.Backups.BackupDifferenceKind.Modified => Lang.Text("Instance.RestorePoint.Compare.Modified"),
                        _ => Lang.Text("Instance.RestorePoint.Compare.Removed")
                    }, item.Path));
                var message = differences.Count == 0
                    ? Lang.Text("Instance.RestorePoint.Compare.Empty")
                    : Lang.Text("Instance.RestorePoint.Compare.Summary", added, modified, removed)
                      + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, details)
                      + (differences.Count > 20 ? Environment.NewLine + Lang.Text("Instance.RestorePoint.Compare.More", differences.Count - 20) : "");
                ModBase.RunInUiWait(() => ModMain.MyMsgBox(message, Lang.Text("Instance.RestorePoint.Compare.Title"), Lang.Text("Common.Action.Confirm")));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "对比实例还原点失败", ModBase.LogLevel.Hint);
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Instance.RestorePoint.Failed"), HintType.Error));
            }
        });
    }
    private void DeleteRestorePoint(McInstance instance, PCL.Core.Minecraft.Backups.BackupSnapshot snapshot)
    {
        if (ModMain.MyMsgBox(Lang.Text("Instance.RestorePoint.Delete.Confirm", snapshot.CreatedUtc.LocalDateTime),
                Lang.Text("Instance.RestorePoint.Delete"), Lang.Text("Instance.RestorePoint.Delete"),
                Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
        var instanceDirectory = instance.PathIndie;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                ModWorldBackups.DeleteRestorePoint(instanceDirectory, snapshot.Id);
                ModBase.RunInUi(() =>
                {
                    HintService.Hint(Lang.Text("Instance.RestorePoint.Deleted"), HintType.Success);
                    RefreshRestorePoints(PageInstanceLeft.McInstance);
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "删除实例还原点失败", ModBase.LogLevel.Hint);
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Instance.RestorePoint.Failed"), HintType.Error));
            }
        });
    }
    private void RestorePoint(McInstance instance, PCL.Core.Minecraft.Backups.BackupSnapshot snapshot)
    {
        if (ModMain.MyMsgBox(Lang.Text("Instance.RestorePoint.Confirm", snapshot.CreatedUtc.LocalDateTime),
                Lang.Text("Instance.RestorePoint.Restore"), Lang.Text("Instance.RestorePoint.Restore"),
                Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
        var instanceDirectory = instance.PathIndie;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                ModWorldBackups.RestoreRestorePoint(instanceDirectory, snapshot.Id);
                ModBase.RunInUi(() =>
                {
                    HintService.Hint(Lang.Text("Instance.RestorePoint.Restored"), HintType.Success);
                    RefreshRestorePoints(PageInstanceLeft.McInstance);
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "恢复实例还原点失败", ModBase.LogLevel.Hint);
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Instance.RestorePoint.Failed"), HintType.Error));
            }
        });
    }
    private void GetInstanceInfo()
    {
        modpackCompItem = null;
        ModBase.RunInUi(() =>
        {
            PanInfo.Children.Clear();
            PanInfo.Children.Add(new MyLoading { Text = Lang.Text("Instance.Overall.Info.Loading"), Margin = new Thickness(0d, 0d, 0d, 10d) });
        });
        var loaders = new List<ModLoader.LoaderBase>();
        loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Instance.Overall.Info.LoadModpackInfoTask"), _ =>
        {
            var modpackId = States.Instance.ModpackId[PageInstanceLeft.McInstance.PathInstance];
            if (!string.IsNullOrWhiteSpace(modpackId))
            {
                var compProjects = ModComp.CompRequest.GetCompProjectsByIds(new List<string> { modpackId });
                if (compProjects.Count > 0)
                    ModBase.RunInUi(() =>
                    {
                        modpackCompItem = compProjects.First().ToCompItem(false, false);
                        modpackCompItem.Tag = compProjects.First();
                    });
            }
        })
        {
            block = true
        });
        loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Instance.Overall.Info.LoadInstanceInfoTask"), _ => ModBase.RunInUi(() =>
        {
            var instance = PageInstanceLeft.McInstance;
            var instanceInfo = instance.Info;
            List<MyListItem> items = [];
            var launchCount = States.Instance.LaunchCount[instance.PathInstance];
            if (launchCount == 0)
                items.Add(new MyListItem
                {
                    Title = Lang.Text("Instance.Overall.Info.LaunchCount.Title"), Info = Lang.Text("Instance.Overall.Info.LaunchCount.Never"), Logo = "pack://application:,,,/images/Blocks/RedstoneLampOff.png"
                });
            else
                items.Add(new MyListItem
                {
                    Title = Lang.Text("Instance.Overall.Info.LaunchCount.Title"),
                    Info = Lang.Text("Instance.Overall.Info.LaunchCount.Count", States.Instance.LaunchCount[instance.PathInstance]),
                    Logo = "pack://application:,,,/images/Blocks/RedstoneLampOn.png"
                });
            if (!string.IsNullOrWhiteSpace(States.Instance.ModpackVersion[instance.PathInstance]))
                items.Add(new MyListItem
                {
                    Title = Lang.Text("Instance.Overall.Info.ModpackVersion"), Info = States.Instance.ModpackVersion[instance.PathInstance],
                    Logo = "pack://application:,,,/images/Blocks/CommandBlock.png"
                });
            items.Add(new MyListItem
            {
                Title = "Minecraft", Info = instanceInfo.VanillaName,
                Logo = "pack://application:,,,/images/Blocks/Grass.png"
            });
            if (instanceInfo.HasForge)
                items.Add(new MyListItem
                {
                    Title = "Forge", Info = instanceInfo.Forge, Logo = "pack://application:,,,/images/Blocks/Anvil.png"
                });
            if (instanceInfo.HasNeoForge)
                items.Add(new MyListItem
                {
                    Title = "NeoForge", Info = instanceInfo.NeoForge,
                    Logo = "pack://application:,,,/images/Blocks/NeoForge.png"
                });
            if (instanceInfo.HasCleanroom)
                items.Add(new MyListItem
                {
                    Title = "Cleanroom", Info = instanceInfo.Cleanroom,
                    Logo = "pack://application:,,,/images/Blocks/Cleanroom.png"
                });
            if (instanceInfo.HasFabric)
                items.Add(new MyListItem
                {
                    Title = "Fabric", Info = instanceInfo.Fabric,
                    Logo = "pack://application:,,,/images/Blocks/Fabric.png"
                });
            if (instanceInfo.HasQuilt)
                items.Add(new MyListItem
                {
                    Title = "Quilt", Info = instanceInfo.Quilt, Logo = "pack://application:,,,/images/Blocks/Quilt.png"
                });
            if (instanceInfo.HasOptiFine)
                items.Add(new MyListItem
                {
                    Title = "OptiFine", Info = instanceInfo.OptiFine,
                    Logo = "pack://application:,,,/images/Blocks/GrassPath.png"
                });
            if (instanceInfo.HasLiteLoader)
                items.Add(new MyListItem
                    { Title = "LiteLoader", Info = Lang.Text("Instance.Overall.Info.Installed"), Logo = "pack://application:,,,/images/Blocks/Egg.png" });
            if (instanceInfo.HasLegacyFabric)
                items.Add(new MyListItem
                {
                    Title = "Legacy Fabric", Info = instanceInfo.LegacyFabric,
                    Logo = "pack://application:,,,/images/Blocks/Fabric.png"
                });
            if (instanceInfo.HasLabyMod)
                items.Add(new MyListItem
                {
                    Title = "LabyMod", Info = instanceInfo.LabyMod,
                    Logo = "pack://application:,,,/images/Blocks/LabyMod.png"
                });
            var wrapPanel = new WrapPanel { Margin = new Thickness(0, -5, -20, 7) };
            foreach (var item in items)
            {
                wrapPanel.Children.Add(item);
                wrapPanel.Children.Add(new TextBlock { Width = 2d });
            }

            PanInfo.Children.Clear();
            if (modpackCompItem is not null)
            {
                PanInfo.Children.Add(modpackCompItem);
                PanInfo.Children.Add(new TextBlock());
            }

            PanInfo.Children.Add(wrapPanel);
        })));
        instanceInfoLoader = new ModLoader.LoaderCombo<int>("Instance Info Loader", loaders) { show = false };
        instanceInfoLoader.Start();
    }

    #region 卡片：个性化

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
            // 修改此部分的同时修改快速安装的实例名检测*
            var newName = ModMain.MyMsgBoxInput(Lang.Text("Instance.Overall.Name.EditTitle"), "", oldName,
                [new FolderNameValidator(ModFolder.mcFolderSelected + "versions", ignoreCase: false)]);
            if (string.IsNullOrWhiteSpace(newName))
                return;
            var newPath = Path.Combine(ModFolder.mcFolderSelected, "versions", newName);
            // 获取临时中间名，以防止仅修改大小写的重命名失败
            var tempName = newName + "_temp";
            var tempPath = Path.Combine(ModFolder.mcFolderSelected, "versions", tempName);
            var isCaseChangedOnly = (newName.ToLower() ?? "") == (oldName.ToLower() ?? "");
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
                    FileSystem.RenameDirectory(Path.Combine(newPath, $"{oldName}-natives"), $"{oldName}natives_temp");
                    FileSystem.RenameDirectory(Path.Combine(newPath, $"{oldName}-natives_temp"), $"{newName}-natives");
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
                    FileSystem.RenameFile(Path.Combine(newPath, $"{oldName}.jar"), $"{oldName}_temp.jar");
                    FileSystem.RenameFile(Path.Combine(newPath, $"{oldName}_temp.jar"), $"{newName}.jar");
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
                    ModBase.ReadFile(Path.Combine(newPath, "PCL", "Setup.ini")).Replace(oldPath, newPath));
            // 更改已选中的实例
            if ((ModBase.ReadIni(ModFolder.mcFolderSelected + "PCL.ini", "Version") ?? "") == (oldName ?? ""))
                ModBase.WriteIni(ModFolder.mcFolderSelected + "PCL.ini", "Version", newName);
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
            PageInstanceLeft.McInstance = new McInstance(newName).Load();
            if (ModInstanceList.McMcInstanceSelected is not null &&
                ModInstanceList.McMcInstanceSelected.Equals(PageInstanceLeft.McInstance))
                ModBase.WriteIni(ModFolder.mcFolderSelected + "PCL.ini", "Version", newName);
            Reload();
            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
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

    #endregion

    #region 卡片：快捷方式

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

    #endregion

    #region 卡片：管理

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

    #endregion
}
