using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.UI;
using PCL.Core.UI.Theme;
using PCL.Network;
using PCL.Network.Loaders;
using FileSystem = Microsoft.VisualBasic.FileSystem;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageInstanceSavesDatapack
{

    /// <summary>
    ///     将加载器结果的数据包列表加载为 UI。
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
            }
            else
            {
                // 根据组件类型设置 PanEmpty 的文本内容
                TxtEmptyTitle.Text = Lang.Text("Instance.Resource.Datapack.Empty.Title");
                TxtEmptyDescription.Text = Lang.Text("Instance.Resource.Datapack.Empty.Description");

                PanEmpty.Visibility = Visibility.Visible;
                PanBack.Visibility = Visibility.Collapsed;
                return;
            }

            // 修改缓存
            datapackItems.Clear();
            var itemsToShow = ModLocalComp.compResourceListLoader.output.ToList();

            foreach (var DatapackEntity in itemsToShow)
                datapackItems[DatapackEntity.RawPath] = BuildLocalCompItem(DatapackEntity);

            // 显示结果
            ModBase.RunInUi(() =>
            {
                Filter = FilterType.All;
                SearchBox.Text = ""; // 这会触发结果刷新，所以需要在 DatapackItems 更新之后
                RefreshUI();
                SetSortMethod(SortMethod.CompName);
            });
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "加载数据包列表 UI 失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
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
                buttonHandler = BuildLocalCompItemBtnHandler,
                Checked = selectedDatapacks.Contains(entry.RawPath)
            };
            newItem.CurrentSwipe = currentSwipSelect;
            newItem.Tags = entry.Tags;
            entry.OnCompUpdate += _ => newItem.Refresh();
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
        sender.Changed += (ss, e) => CheckChanged((MyLocalCompItem)ss, e);

        // 文件项的点击事件：切换选中状态
        sender.Click += (ss, e) =>
        {
            var s = (MyLocalCompItem)ss;
            s.Checked = !s.Checked;
        };

        // 图标按钮
        var btnOpen = new MyIconButton { LogoScale = 1.05d, SvgIcon = "lucide/folder-open", Tag = sender };
        btnOpen.ToolTip = Lang.Text("Instance.Saves.OpenFileLocation");
        ToolTipService.SetPlacement(btnOpen, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnOpen, 30d);
        ToolTipService.SetHorizontalOffset(btnOpen, 2d);
        btnOpen.Click += (sender, e) => Open_Click((MyIconButton)sender, e);

        var btnCont = new MyIconButton { LogoScale = 1d, SvgIcon = "lucide/info", Tag = sender };
        btnCont.ToolTip = Lang.Text("Instance.Saves.Detail");
        ToolTipService.SetPlacement(btnCont, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnCont, 30d);
        ToolTipService.SetHorizontalOffset(btnCont, 2d);
        btnCont.Click += Info_Click;
        sender.MouseRightButtonUp += Info_Click;

        var btnDelete = new MyIconButton { LogoScale = 1d, SvgIcon = "lucide/trash-2", Tag = sender };
        btnDelete.ToolTip = Lang.Text("Common.Action.Delete");
        ToolTipService.SetPlacement(btnDelete, PlacementMode.Center);
        ToolTipService.SetVerticalOffset(btnDelete, 30d);
        ToolTipService.SetHorizontalOffset(btnDelete, 2d);
        btnDelete.Click += (sender, e) => Delete_Click((MyIconButton)sender, e);

        if (sender.Entry.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine)
        {
            var btnDisable = new MyIconButton { LogoScale = 1d, SvgIcon = "lucide/circle-minus", Tag = sender };
            btnDisable.ToolTip = Lang.Text("Instance.Resource.Disable");
            ToolTipService.SetPlacement(btnDisable, PlacementMode.Center);
            ToolTipService.SetVerticalOffset(btnDisable, 30d);
            ToolTipService.SetHorizontalOffset(btnDisable, 2d);
            btnDisable.Click += (ss, e) => Disable_Click((MyIconButton)ss, e);
            sender.Buttons = new[] { btnCont, btnOpen, btnDisable, btnDelete };
        }
        else if (sender.Entry.State == ModLocalComp.LocalCompFile.LocalFileStatus.Disabled)
        {
            var btnEnable = new MyIconButton { LogoScale = 1d, SvgIcon = "lucide/circle-check", Tag = sender };
            btnEnable.ToolTip = Lang.Text("Instance.Resource.Enable");
            ToolTipService.SetPlacement(btnEnable, PlacementMode.Center);
            ToolTipService.SetVerticalOffset(btnEnable, 30d);
            ToolTipService.SetHorizontalOffset(btnEnable, 2d);
            btnEnable.Click += (ss, e) => Enable_Click((MyIconButton)ss, e);
            sender.Buttons = new[] { btnCont, btnOpen, btnEnable, btnDelete };
        }
        else
        {
            sender.Buttons = new[] { btnCont, btnOpen, btnDelete };
        }
    }


    /// <summary>
    ///     刷新整个 UI。
    /// </summary>
    public void RefreshUI()
    {
        if (PanList is null)
            return;
        var showingDatapacks = (IsSearching ? searchResult : datapackItems.Values.Select(i => i.Entry))
            .Where(m => CanPassFilter(m)).ToList();

        // 对显示的数据包进行排序
        if (showingDatapacks.Any())
        {
            var sortMethod = GetSortMethod(currentSortMethod);
            showingDatapacks.Sort((a, b) => sortMethod(a, b));
        }

        // 重新列出列表
        ModAnimation.AniControlEnabled += 1;
        if (showingDatapacks.Any())
        {
            PanList.Visibility = Visibility.Visible;
            PanList.Children.Clear();
            foreach (var TargetDatapack in showingDatapacks)
            {
                if (!datapackItems.ContainsKey(TargetDatapack.RawPath))
                    continue;
                var item = datapackItems[TargetDatapack.RawPath];

                // 确保元素没有父容器，避免重复添加异常
                if (item.Parent is not null) ((Panel)item.Parent).Children.Remove(item);

                ModStyle.MinecraftFormatter.SetColorfulTextLab(item.LabTitle.Text, item.LabTitle,
                    ThemeService.IsDarkMode);
                ModStyle.MinecraftFormatter.SetColorfulTextLab(item.LabInfo.Text, item.LabInfo,
                    ThemeService.IsDarkMode);
                item.Checked = selectedDatapacks.Contains(TargetDatapack.RawPath); // 更新选中状态
                PanList.Children.Add(item);
            }
        }
        else
        {
            PanList.Visibility = Visibility.Collapsed;
        }

        ModAnimation.AniControlEnabled -= 1;
        selectedDatapacks =
            new HashSet<string>(selectedDatapacks.Where(m =>
                showingDatapacks.Any(s => (s.RawPath ?? "") == (m ?? ""))));
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
            var itemSource = (IsSearching ? searchResult : datapackItems.Values.Select(i => i.Entry)).ToArray();
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

            // -----------------
            // 底部栏
            // -----------------

            // 计数
            var newCount = selectedDatapacks.Count;
            var selected = newCount > 0;
            if (selected)
                LabSelect.Text = Lang.Text("Instance.Resource.SelectedCount", newCount);

            // 按钮可用性
            if (selected)
            {
                var hasUpdate = false;
                var hasEnabled = false;
                var hasDisabled = false;
                var canFavoriteAndShare = true;


                // 检查是否所有选中的数据包都有有效的项目信息
                await Task.Run(() =>
                {
                    foreach (var DatapackEntity in ModLocalComp.compResourceListLoader.output)
                        if (selectedDatapacks.Contains(DatapackEntity.RawPath))
                        {
                            if (DatapackEntity.CanUpdate) hasUpdate = true;
                            if (DatapackEntity.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine)
                                hasEnabled = true;
                            else if (DatapackEntity.State == ModLocalComp.LocalCompFile.LocalFileStatus.Disabled)
                                hasDisabled = true;
                            if (DatapackEntity.Comp is null || string.IsNullOrEmpty(DatapackEntity.Comp.Id))
                                canFavoriteAndShare = false;
                        }
                });

                BtnSelectDisable.IsEnabled = hasEnabled;
                BtnSelectEnable.IsEnabled = hasDisabled;
                BtnSelectUpdate.IsEnabled = hasUpdate;
                BtnSelectFavorites.IsEnabled = canFavoriteAndShare;
                BtnSelectShare.IsEnabled = canFavoriteAndShare;
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
                        }, "Datapack Sidebar");
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
                        }, "Datapack Sidebar");
                }
            }
            else
            {
                ModAnimation.AniStop("Datapack Sidebar");
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
