using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.UI;
using PCL.Core.UI.Theme;
using PCL.Network;
using PCL.Network.Loaders;
using FileSystem = Microsoft.VisualBasic.FileSystem;
using SearchOption = System.IO.SearchOption;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceCompResource
{

    /// <summary>
    ///     将加载器结果的 Mod 列表加载为 UI。
    /// </summary>
    private void LoadUIFromLoaderOutput()
    {
        try
        {
            // 判断应该显示哪一个页面
            if (ModLocalComp.compResourceListLoader.output.Any())
            {
                PanBack.Visibility = Visibility.Visible;
                PanEmpty.Visibility = Visibility.Collapsed;
                PanSchematicEmpty.Visibility = Visibility.Collapsed;
            }
            else
            {
                // 检查是否为投影文件类型且schematics文件夹不存在
                if (currentCompType == ModComp.CompType.Schematic)
                {
                    var schematicsPath = PageInstanceLeft.McInstance.PathIndie + @"schematics\";
                    if (!Directory.Exists(schematicsPath))
                    {
                        PanSchematicEmpty.Visibility = Visibility.Visible;
                        PanEmpty.Visibility = Visibility.Collapsed;
                        PanBack.Visibility = Visibility.Collapsed;
                        return;
                    }
                }

                // 根据组件类型设置PanEmpty的文本内容
                if (currentCompType == ModComp.CompType.Schematic)
                {
                    // 检查是否在子文件夹中
                    if (!string.IsNullOrEmpty(CurrentFolderPath))
                    {
                        // 子文件夹为空的提示
                        TxtEmptyTitle.Text = Lang.Text("Instance.Resource.EmptyFolder.Title");
                        TxtEmptyDescription.Text = Lang.Text("Instance.Resource.EmptyFolder.Description");
                    }
                    else
                    {
                        // 根目录为空的提示
                        TxtEmptyTitle.Text = Lang.Text("Instance.Resource.Empty.Title");
                        TxtEmptyDescription.Text = Lang.Text("Instance.Resource.Empty.Description");
                    }
                }
                else
                {
                    TxtEmptyTitle.Text = Lang.Text("Instance.Resource.Empty.Title");
                    TxtEmptyDescription.Text = Lang.Text("Instance.Resource.Empty.DescriptionWithDownload");
                }

                // 如果当前在子文件夹中，显示返回上一级按钮
                if (!string.IsNullOrEmpty(CurrentFolderPath))
                    BtnHintBack.Visibility = Visibility.Visible;
                else
                    BtnHintBack.Visibility = Visibility.Collapsed;

                PanEmpty.Visibility = Visibility.Visible;
                PanBack.Visibility = Visibility.Collapsed;
                PanSchematicEmpty.Visibility = Visibility.Collapsed;
                return;
            }

            CaptureLoaderCompatibilityContext();
            // 修改缓存
            modItems.Clear();
            var rootPath = PageInstanceLeft.McInstance.PathIndie +
                           (PageInstanceLeft.McInstance.Info.HasLabyMod
                               ? Path.Combine("labymod-neo", "fabric", PageInstanceLeft.McInstance.Info.VanillaName)
                               : "") + ModLocalComp.GetPathNameByCompType(currentCompType) + @"\";
            rootPath = Path.GetFullPath(rootPath.TrimEnd('\\'));

            var itemsToShow = ModLocalComp.compResourceListLoader.output.Where(item =>
            {
                var itemPath = item.IsFolder ? item.ActualPath : item.path;
                var parentDir = Directory.GetParent(itemPath)?.FullName;
                if (string.IsNullOrEmpty(CurrentFolderPath))
                    return parentDir.Equals(rootPath, StringComparison.OrdinalIgnoreCase);

                return parentDir.Equals(CurrentFolderPath, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            foreach (var ModEntity in itemsToShow)
                modItems[ModEntity.RawPath] = BuildLocalCompItem(ModEntity);
            // 显示结果
            ModBase.RunInUi(() =>
            {
                Filter = FilterType.All;
                SearchBox.Text = ""; // 这会触发结果刷新，所以需要在 ModItems 更新之后，详见 #3124 的视频
                RefreshUI();
                SetSortMethod(SortMethod.CompName);
                OfferModIndex();
            });
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"加载 {currentCompType} 列表 UI 失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
        }
    }


    private MyLocalCompItem BuildLocalCompItem(ModLocalComp.LocalCompFile entry)
    {
        try
        {
            ModAnimation.AniControlEnabled += 1;
            var newItem = new MyLocalCompItem
            {
                SnapsToDevicePixels = true,
                Entry = entry,
                LoaderCompatibilityContext = _loaderCompatibilityContext,
                buttonHandler = BuildLocalCompItemBtnHandler,
                Checked = selectedMods.Contains(entry.RawPath)
            };
            newItem.CurrentSwipe = currentSwipSelect;
            newItem.Tags = entry.Tags;
            entry.OnCompUpdate += _ => newItem.Refresh();
            // AddHandler Entry.OnCompUpdate, Sub() RunInUi(Sub() DoSort())
            newItem.Refresh();
            ModAnimation.AniControlEnabled -= 1;
            return newItem;
        }
        catch (Exception ex)
        {
            ModAnimation.AniControlEnabled -= 1;
            ModBase.Log(ex, $"创建 UI 项失败：{entry.RawPath}");
            throw;
        }
    }


    private void BuildLocalCompItemBtnHandler(MyLocalCompItem sender, EventArgs e)
    {
        // 点击事件
        sender.Changed += (ss, ee) => CheckChanged((MyLocalCompItem)ss, ee);
        if (sender.Entry.IsFolder)
        {
            // 文件夹项的点击事件：双击进入文件夹，单击切换选中状态
            var lastClickTime = DateTime.MinValue;
            sender.Click += (sss, _) =>
            {
                var ss = (MyLocalCompItem)sss;
                var currentTime = DateTime.Now;
                var timeDiff = (currentTime - lastClickTime).TotalMilliseconds;

                if (timeDiff <= 300d)
                    // 300ms内双击，进入文件夹
                    EnterFolderWithCheck(ss.Entry.ActualPath);
                else
                    // 单击切换选中状态
                    ss.Checked = !ss.Checked;

                lastClickTime = currentTime;
            };
        }
        else
        {
            // 文件项的点击事件：切换选中状态
            sender.Click += (sss, _) =>
            {
                var ss = (MyLocalCompItem)sss;
                ss.Checked = !ss.Checked;
            };
        }

        // 图标按钮
        var btnOpen = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/folder-open", Tag = sender };
        btnOpen.ToolTip = Lang.Text("Instance.Saves.OpenFileLocation");
        ToolTipService.SetPlacement(btnOpen, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnOpen, 30d);
        ToolTipService.SetHorizontalOffset(btnOpen, 2d);
        btnOpen.Click += (ss, ee) => Open_Click((MyIconButton)ss, ee);
        var btnCont = new MyIconButton { LogoScale = 1d, SvgIcon = "lucide/info", Tag = sender };
        btnCont.ToolTip = Lang.Text("Instance.Saves.Detail");
        ToolTipService.SetPlacement(btnCont, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnCont, 30d);
        ToolTipService.SetHorizontalOffset(btnCont, 2d);
        btnCont.Click += Info_Click;
        sender.MouseRightButtonUp += Info_Click;
        MyIconButton? btnFavorite = null;
        if (!sender.Entry.IsFolder && sender.Entry.Comp is { } favoriteProject)
        {
            btnFavorite = new MyIconButton
            {
                LogoScale = 1d,
                SvgIcon = ModComp.CompFavorites.IsFavourite(favoriteProject.Id) ? "lucide/heart-filled" : "lucide/heart",
                Tag = sender
            };
            btnFavorite.ToolTip = Lang.Text("Download.Comp.Favorites.Action.Manage");
            ToolTipService.SetPlacement(btnFavorite, PlacementMode.Center);
            ToolTipService.SetVerticalOffset(btnFavorite, 30d);
            ToolTipService.SetHorizontalOffset(btnFavorite, 2d);
            btnFavorite.Click += (_, _) => ModComp.CompFavorites.ShowMenu(favoriteProject, btnFavorite, () =>
            {
                if (btnFavorite is not null)
                    btnFavorite.SvgIcon = ModComp.CompFavorites.IsFavourite(favoriteProject.Id)
                        ? "lucide/heart-filled"
                        : "lucide/heart";
            });
        }

        var btnDelete = new MyIconButton { LogoScale = 1d, SvgIcon = "lucide/trash-2", Tag = sender };
        btnDelete.ToolTip = Lang.Text("Common.Action.Delete");
        ToolTipService.SetPlacement(btnDelete, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnDelete, 30d);
        ToolTipService.SetHorizontalOffset(btnDelete, 2d);
        btnDelete.Click += (ss, ee) => Delete_Click((MyIconButton)ss, ee);
        if (currentCompType != ModComp.CompType.Mod ||
            sender.Entry.State == ModLocalComp.LocalCompFile.LocalFileStatus.Unavailable)
        {
            sender.Buttons = btnFavorite is null
                ? new[] { btnCont, btnOpen, btnDelete }
                : new[] { btnCont, btnOpen, btnFavorite, btnDelete };
        }
        else
        {
            var btnED = new MyIconButton
            {
                LogoScale = 1d,
                SvgIcon = sender.Entry.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine
                    ? "lucide/circle-minus"
                    : "lucide/circle-check",
                Tag = sender
            };
            btnED.ToolTip = sender.Entry.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine ? Lang.Text("Instance.Resource.Disable") : Lang.Text("Instance.Resource.Enable");
            ToolTipService.SetPlacement(btnED, PlacementMode.Center);
            ToolTipService.SetVerticalOffset(btnED, 30d);
            ToolTipService.SetHorizontalOffset(btnED, 2d);
            btnED.Click += (ss, ee) => ED_Click((MyIconButton)ss, ee);
            sender.Buttons = btnFavorite is null
                ? new[] { btnCont, btnOpen, btnED, btnDelete }
                : new[] { btnCont, btnOpen, btnED, btnFavorite, btnDelete };
        }
    }


    /// <summary>
    ///     刷新整个 UI。
    /// </summary>
    public void RefreshUI()
    {
        if (PanList is null)
            return;
        var showingMods = (IsSearching ? searchResult : modItems.Values.Select(i => i.Entry))
            .Where(m => CanPassFilter(m)).ToList();

        // 对显示的资源进行排序，确保文件夹置顶
        if (showingMods.Any())
        {
            var sortMethod = GetSortMethod(currentSortMethod);
            showingMods.Sort((a, b) => sortMethod(a, b));
        }

        // 重新列出列表
        ModAnimation.AniControlEnabled += 1;
        if (showingMods.Any())
        {
            PanList.Visibility = Visibility.Visible;
            PanList.Children.Clear();
            foreach (var TargetMod in showingMods)
            {
                if (!modItems.ContainsKey(TargetMod.RawPath))
                    continue;
                var item = modItems[TargetMod.RawPath];

                // 确保元素没有父容器，避免重复添加异常
                if (item.Parent is not null) ((Panel)item.Parent).Children.Remove(item);

                ModStyle.MinecraftFormatter.SetColorfulTextLab(item.LabTitle.Text, item.LabTitle,
                    ThemeService.IsDarkMode);
                ModStyle.MinecraftFormatter.SetColorfulTextLab(item.LabInfo.Text, item.LabInfo,
                    ThemeService.IsDarkMode);
                item.Checked = selectedMods.Contains(TargetMod.RawPath); // 更新选中状态
                PanList.Children.Add(item);
            }
        }
        else
        {
            PanList.Visibility = Visibility.Collapsed;
        }

        ModAnimation.AniControlEnabled -= 1;
        selectedMods =
            new HashSet<string>(selectedMods.Where(m => showingMods.Any(s => (s.RawPath ?? "") == (m ?? ""))));
        RefreshBars();
    }


    /// <summary>
    ///     刷新顶栏和底栏显示。
    /// </summary>
    public void RefreshBars()
    {
        Dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            // -----------------
            // 顶部栏
            // -----------------

            // 计数
            var anyCount = 0;
            var enabledCount = 0;
            var disabledCount = 0;
            var updateCount = 0;
            var unavalialeCount = 0;
            var itemSource = (IsSearching ? searchResult : modItems.Values.Select(i => i.Entry)).ToArray();
            await Task.Run(() =>
            {
                foreach (var item in itemSource)
                {
                    anyCount += 1;
                    if (item.CanUpdate) updateCount += 1;
                    if (item.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine) enabledCount += 1;
                    if (item.State == ModLocalComp.LocalCompFile.LocalFileStatus.Disabled) disabledCount += 1;
                    if (item.State == ModLocalComp.LocalCompFile.LocalFileStatus.Unavailable) unavalialeCount += 1;
                }
            });
            // 显示
            BtnFilterAll.Text = IsSearching ? Lang.Text("Instance.Resource.Filter.SearchResult") : Lang.Text("Instance.Resource.Filter.AllWithCount", anyCount);
            BtnFilterCanUpdate.Text = Lang.Text("Instance.Resource.Filter.UpdatableWithCount", updateCount);
            BtnFilterCanUpdate.Visibility = Filter == FilterType.CanUpdate || updateCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnFilterEnabled.Text = Lang.Text("Instance.Resource.Filter.EnabledWithCount", enabledCount);
            BtnFilterEnabled.Visibility = Filter == FilterType.Enabled || (enabledCount > 0 && enabledCount < anyCount)
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnFilterDisabled.Text = Lang.Text("Instance.Resource.Filter.DisabledWithCount", disabledCount);
            BtnFilterDisabled.Visibility = Filter == FilterType.Disabled || disabledCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnFilterError.Text = Lang.Text("Instance.Resource.Filter.ErrorWithCount", unavalialeCount);
            BtnFilterError.Visibility = Filter == FilterType.Unavailable || unavalialeCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            // 查找重复项目
            var duplicateItems = await Task.Run(() => itemSource.GroupBy(m =>
            {
                if (m.Comp is null) return ":Nothing:";

                return m.Comp.Id;
            }).Where(g => g.Count() > 1 && g.First().Comp is not null).SelectMany(g => g).ToList());
            BtnFilterDuplicate.Text = Lang.Text("Instance.Resource.Filter.DuplicateWithCount", duplicateItems.Count);
            BtnFilterDuplicate.Visibility = Filter == FilterType.Duplicate || duplicateItems.Any()
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 返回按钮显示控制（在子文件夹中时显示）
            if (!string.IsNullOrEmpty(CurrentFolderPath))
                BtnManageBack.Visibility = Visibility.Visible;
            else
                BtnManageBack.Visibility = Visibility.Collapsed;

            // -----------------
            // 底部栏
            // -----------------

            // 计数
            var newCount = selectedMods.Count;
            var selected = newCount > 0;
            if (selected)
                LabSelect.Text = Lang.Text("Instance.Resource.SelectedCount", newCount); // 取消所有选择时不更新数字
            // 按钮可用性
            if (selected)
            {
                var hasUpdate = false;
                var hasEnabled = false;
                var hasDisabled = false;
                var canFavoriteAndShare = true; // 是否可以收藏和分享


                // 检查是否所有选中的资源都有有效的项目信息（即已完成联网更新）
                await Task.Run(() =>
                {
                    foreach (var ModEntity in ModLocalComp.compResourceListLoader.output)
                        if (selectedMods.Contains(ModEntity.RawPath))
                        {
                            if (ModEntity.CanUpdate) hasUpdate = true;
                            if (ModEntity.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine)
                                hasEnabled = true;
                            else if (ModEntity.State == ModLocalComp.LocalCompFile.LocalFileStatus.Disabled)
                                hasDisabled = true;
                            if (ModEntity.Comp is null || string.IsNullOrEmpty(ModEntity.Comp.Id))
                                canFavoriteAndShare = false;
                        }
                });

                BtnSelectDisable.IsEnabled = hasEnabled;
                BtnSelectEnable.IsEnabled = hasDisabled;
                BtnSelectUpdate.IsEnabled = hasUpdate;

                // 针对投影原理图隐藏分享 更新 收藏按钮
                if (currentCompType == ModComp.CompType.Schematic)
                {
                    BtnSelectUpdate.Visibility = Visibility.Collapsed;
                    BtnSelectFavorites.Visibility = Visibility.Collapsed;
                    BtnSelectShare.Visibility = Visibility.Collapsed;
                }
                else
                {
                    BtnSelectUpdate.Visibility = Visibility.Visible;
                    BtnSelectFavorites.Visibility = Visibility.Visible;
                    BtnSelectShare.Visibility = Visibility.Visible;

                    // 根据是否已加载项目信息来启用/禁用收藏和分享按钮
                    BtnSelectFavorites.IsEnabled = canFavoriteAndShare;
                    BtnSelectShare.IsEnabled = canFavoriteAndShare;
                }
            }

            // 更新显示状态
            if (ModAnimation.AniControlEnabled == 0)
            {
                PanListBack.Margin = new Thickness(0d, 0d, 0d, selected ? 95 : 15);
                if (selected)
                {
                    // 仅在数量增加时播放出现/跳跃动画
                    if (bottomBarShownCount >= newCount)
                    {
                        bottomBarShownCount = newCount;
                        return;
                    }

                    bottomBarShownCount = newCount;
                    // 出现/跳跃动画
                    CardSelect.Visibility = Visibility.Visible;
                    ModAnimation.AniStart(
                        new[]
                        {
                            ModAnimation.AaOpacity(CardSelect, 1d - CardSelect.Opacity, 60),
                            ModAnimation.AaTranslateY(CardSelect, -27 - TransSelect.Y, 120,
                                ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                            ModAnimation.AaTranslateY(CardSelect, 3d, 150, 120,
                                new ModAnimation.AniEaseInoutFluent(ModAnimation.AniEasePower.Weak)),
                            ModAnimation.AaTranslateY(CardSelect, -1, 90, 270,
                                new ModAnimation.AniEaseInoutFluent(ModAnimation.AniEasePower.Weak))
                        }, "Mod Sidebar");
                }
                else
                {
                    // 不重复播放隐藏动画
                    if (bottomBarShownCount == 0)
                        return;
                    bottomBarShownCount = 0;
                    // 隐藏动画
                    ModAnimation.AniStart(
                        new[]
                        {
                            ModAnimation.AaOpacity(CardSelect, -CardSelect.Opacity, 90),
                            ModAnimation.AaTranslateY(CardSelect, -10 - TransSelect.Y, 90,
                                ease: new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
                            ModAnimation.AaCode(() => CardSelect.Visibility = Visibility.Collapsed, after: true)
                        }, "Mod Sidebar");
                }
            }
            else
            {
                ModAnimation.AniStop("Mod Sidebar");
                bottomBarShownCount = newCount;
                if (selected)
                {
                    CardSelect.Visibility = Visibility.Visible;
                    CardSelect.Opacity = 1d;
                    TransSelect.Y = -25;
                }
                else
                {
                    CardSelect.Visibility = Visibility.Collapsed;
                    CardSelect.Opacity = 0d;
                    TransSelect.Y = -10;
                }
            }
        }));
    }

}
