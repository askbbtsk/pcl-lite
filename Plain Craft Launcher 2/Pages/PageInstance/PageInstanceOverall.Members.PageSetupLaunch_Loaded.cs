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

}
