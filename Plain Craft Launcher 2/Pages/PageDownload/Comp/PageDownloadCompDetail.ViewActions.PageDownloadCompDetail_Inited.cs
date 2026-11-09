using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FluentValidation;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.UI;
using PCL.Core.Utils;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Network.Loaders;
using Control = System.Windows.Forms.Control;

namespace PCL;

public partial class PageDownloadCompDetail
{

    // 初始化加载器信息
    private void PageDownloadCompDetail_Inited(object sender, EventArgs e)
    {
        LoadTargetFromAdditional();
        PageLoaderInit(Load, PanLoad, PanMain, CardIntro, _compFileLoader, _ => Load_OnFinish());
    }


    public void LoadTargetFromAdditional()
    {
        var additional = ModMain.frmMain.pageCurrent.additional.Value;
        _project = additional.CompProject;
        _targetInstance = additional.TargetVersion;
        _targetLoader = additional.TargetLoader;
        _pageType = additional.ResourceType;
    }


    // 自动重试
    private void Load_State(object sender, MyLoading.MyLoadingState state, MyLoading.MyLoadingState oldState)
    {
        switch (_compFileLoader.State)
        {
            case ModBase.LoadState.Failed:
            {
                var errorMessage = "";
                if (_compFileLoader.Error is not null)
                    errorMessage = _compFileLoader.Error.Message;
                if (errorMessage.Contains(Lang.Text("Common.Error.InvalidJson")))
                {
                    ModBase.Log("[Comp] 下载的文件 Json 列表损坏，已自动重试", ModBase.LogLevel.Debug);
                    PageLoaderRestart();
                }

                break;
            }
        }
    }


    // 结果 UI 化
    private class CardSorter : IComparer<string>
    {
        public readonly string topmost = "";

        public CardSorter(string topmost = "")
        {
            this.topmost = topmost ?? "";
        }

        public int Compare(string x, string y)
        {
            // 相同
            if ((x ?? "") == (y ?? ""))
                return 0;
            // 置顶
            if ((x ?? "") == (topmost ?? ""))
                return -1;
            if ((y ?? "") == (topmost ?? ""))
                return 1;
            // 特殊版本
            var isXSpecial = !x.Contains(".");
            var isYSpecial = !y.Contains(".");
            if (isXSpecial && isYSpecial)
                return string.Compare(x, y, StringComparison.Ordinal);
            if (isXSpecial)
                return 1;
            if (isYSpecial)
                return -1;
            // 比较版本号
            var versionCodeSort = -McVersionComparer.CompareVersion(x.Replace(x.BeforeFirst(" ") + " ", ""),
                y.Replace(y.BeforeFirst(" ") + " ", ""));
            if (versionCodeSort != 0)
                return versionCodeSort;
            // 比较全部
            return -McVersionComparer.CompareVersion(x, y);
        }
    }


    // 筛选类型相同的结果（Modrinth 会返回 Mod、服务端插件、数据包混合的列表）
    private List<ModComp.CompFile> GetResults()
    {
        var results = _compFileLoader.output;
        if (_pageType == ModComp.CompType.Any)
        {
            results = results.Where(r => r.Type != ModComp.CompType.Plugin).ToList();
        }
        else if (_pageType == ModComp.CompType.Shader || _pageType == ModComp.CompType.ResourcePack)
        {
        }
        // 不筛选光影和资源包，否则原版光影会因为是资源包格式而被过滤（Meloong-Git/#6473）
        else
        {
            results = results.Where(r => r.Type == _pageType).ToList();
        }

        return results;
    }


    private void Load_OnFinish()
    {
        var results = GetResults();

        // 初始化筛选器
        List<string> instanceFilters = null;
        List<string> modLoaderFilters = null;

        void updateFilters()
        {
            instanceFilters = results.SelectMany(v => v.GameVersions)
                .Select(v => GetGroupedVersionName(v, groupedDrop, groupedOld)).Distinct()
                .OrderByDescending(s => s, new McVersionComparer.VersionComparer()).ToList();
            modLoaderFilters = results.SelectMany(v => v.ModLoaders).Select(l => l.ToString()).Distinct()
                .OrderByDescending(s => s).ToList();
        }

        ;

        // 确定分组方式
        groupedDrop = false;
        groupedOld = false;
        updateFilters();
        if (instanceFilters.Count >= 9)
        {
            groupedDrop = true;
            groupedOld = false;
            updateFilters();
            if (instanceFilters.Count >= 9)
            {
                groupedDrop = false;
                groupedOld = true;
                updateFilters();
                if (instanceFilters.Count >= 9)
                {
                    groupedDrop = true;
                    groupedOld = true;
                    updateFilters();
                }
            }
        }


        // UI 化筛选器
        PanInstanceFilter.Children.Clear();
        PanModLoaderFilter.Children.Clear();
        if (_pageType == ModComp.CompType.Mod)
        {
            PanInstanceFilter.Margin = new Thickness(10d, 10d, 0d, 5d);
            PanModLoaderFilter.Margin = new Thickness(10d, 5d, 0d, 10d);
        }
        else
        {
            PanInstanceFilter.Margin = new Thickness(10d, 10d, 0d, 10d);
            PanModLoaderFilter.Margin = new Thickness(0d);
        }

        if (instanceFilters.Count < 2)
        {
            CardFilter.Visibility = Visibility.Collapsed;
            _instanceFilter = null;
        }
        else
        {
            CardFilter.Visibility = Visibility.Visible;
            // 插入标签
            if (_pageType == ModComp.CompType.Mod)
            {
                var instanceTextBlock = new TextBlock
                {
                    Text = Lang.Text("Download.Comp.Detail.InstanceFilter"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2d, 0d, 0d, 0d)
                };
                PanInstanceFilter.Children.Add(instanceTextBlock);
                var modLoaderTextBlock = new TextBlock
                {
                    Text = Lang.Text("Download.Comp.Detail.LoaderFilter"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2d, 0d, 0d, 0d)
                };
                PanModLoaderFilter.Children.Add(modLoaderTextBlock);
            }

            instanceFilters.Insert(0, Lang.Text("Common.Option.All"));
            modLoaderFilters.Insert(0, Lang.Text("Common.Option.All"));
            // 转化为按钮
            foreach (var version in instanceFilters)
            {
                var newButton = new MyRadioButton
                {
                    Text = version, Margin = new Thickness(2d, 0d, 2d, 0d),
                    ColorType = MyRadioButton.ColorState.Highlight
                };
                newButton.LabText.Margin = new Thickness(-2, 0d, 10d, 0d);
                newButton.Check += (sender, raiseByMouse) =>
                {
                    _instanceFilter = sender.Text == Lang.Text("Common.Option.All") ? null : sender.Text;
                    UpdateFilterResult();
                };
                PanInstanceFilter.Children.Add(newButton);
            }

            if (_pageType == ModComp.CompType.Mod)
                foreach (var loader in modLoaderFilters)
                {
                    var newButton = new MyRadioButton
                    {
                        Text = loader,
                        Margin = new Thickness(2d, 0d, 2d, 0d),
                        ColorType = MyRadioButton.ColorState.Highlight
                    };
                    newButton.LabText.Margin = new Thickness(-2, 0d, 10d, 0d);
                    newButton.Check += (sender, raiseByMouse) =>
                    {
                        _modLoaderFilter = sender.Text == Lang.Text("Common.Option.All") ? null : sender.Text;
                        UpdateFilterResult();
                    };
                    PanModLoaderFilter.Children.Add(newButton);
                }

            // 自动选择
            MyRadioButton instanceToCheck = null;
            MyRadioButton modLoaderToCheck = null;
            if (!string.IsNullOrEmpty(_targetInstance))
            {
                var targetFile = results.FirstOrDefault(v => v.GameVersions.Contains(_targetInstance));
                if (targetFile is not null)
                {
                    var targetGroup = GetGroupedVersionName(_targetInstance, groupedDrop, groupedOld);
                    var children = _pageType == ModComp.CompType.Mod
                        ? PanInstanceFilter.Children.Cast<UIElement>().Skip(1)
                        : PanInstanceFilter.Children.Cast<UIElement>();
                    foreach (MyRadioButton button in (IEnumerable)children)
                    {
                        if ((button.Text ?? "") != (targetGroup ?? ""))
                            continue;
                        instanceToCheck = button;
                        break;
                    }
                }
            }

            if (_pageType == ModComp.CompType.Mod)
                if (_targetLoader != ModComp.CompLoaderType.Any)
                {
                    var targetFile = results.FirstOrDefault(v => v.ModLoaders.Contains(_targetLoader));
                    if (targetFile is not null)
                    {
                        var children = _pageType == ModComp.CompType.Mod
                            ? PanInstanceFilter.Children.Cast<UIElement>().Skip(1)
                            : PanInstanceFilter.Children.Cast<UIElement>();
                        foreach (MyRadioButton button in (IEnumerable)children)
                        {
                            if ((button.Text ?? "") != (_targetLoader.ToString() ?? ""))
                                continue;
                            modLoaderToCheck = button;
                            break;
                        }
                    }
                }

            // 注意：在 Mod 下 index 0 是 TextBlock
            var index = _pageType == ModComp.CompType.Mod ? 1 : 0;
            if (instanceToCheck is null)
                instanceToCheck = (MyRadioButton)PanInstanceFilter.Children[index];
            if (modLoaderToCheck is null && (_pageType == ModComp.CompType.Mod))
                modLoaderToCheck = (MyRadioButton)PanModLoaderFilter.Children[index];
            instanceToCheck.Checked = true;
            if (_pageType == ModComp.CompType.Mod)
                modLoaderToCheck.Checked = true;
        }

        // 更新筛选结果（文件列表 UI 化）
        UpdateFilterResult();
    }


    private void UpdateFilterResult()
    {
        var results = GetResults();
        if (results is null)
            return;

        // 1. 预处理基础变量
        var targetVersionText = _targetLoader != ModComp.CompLoaderType.Any ? _targetLoader + " " : "";
        var targetCardName = !string.IsNullOrEmpty(_targetInstance) || _targetLoader != ModComp.CompLoaderType.Any
            ? Lang.Text("Download.Comp.Detail.SelectedVersion", targetVersionText, _targetInstance)
            : "";

        // 使用 HashSet 提高查询性能 O(1)
        var supportedLoaders =
            new HashSet<ModComp.CompLoaderType>(Enum.GetValues(typeof(ModComp.CompLoaderType))
                .Cast<ModComp.CompLoaderType>());
        var ignoreQuilt = Config.Download.Comp.IgnoreQuilt;
        var hasMultipleLoaders = _project.ModLoaders.Count > 1;

        // 2. 核心数据归类 (使用 Dictionary 配合 HashSet 去重)
        var dict = new SortedDictionary<string, List<ModComp.CompFile>>(new CardSorter(targetCardName));
        dict.Add(Lang.Text("Download.Comp.Detail.VersionGroup.Other"), new List<ModComp.CompFile>());

        // 用于记录每个卡片内已存在的 version，防止 Contains(version) 的 O(n) 消耗
        var versionDuplicateChecker = new Dictionary<string, HashSet<ModComp.CompFile>>();

        foreach (var version in results)
        {
            // 处理普通卡片归类
            foreach (var gameVersion in version.GameVersions)
            {
                // 筛选器预检查
                var currentGroupedName = GetGroupedVersionName(gameVersion, groupedDrop, groupedOld);
                if (_instanceFilter is not null && (currentGroupedName ?? "") != (_instanceFilter ?? ""))
                    continue;
                var verName = GetGroupedVersionName(gameVersion, false, false);
                var loaders = new List<string>();

                // 判定 Loader 逻辑
                if (hasMultipleLoaders && version.Type == ModComp.CompType.Mod &&
                    McInstanceInfo.IsFormatFit(verName))
                {
                    foreach (var loader in version.ModLoaders)
                    {
                        if (loader == ModComp.CompLoaderType.Quilt && ignoreQuilt)
                            continue;
                        if (!supportedLoaders.Contains(loader))
                            continue;

                        // 模组加载器筛选器
                        if (_modLoaderFilter is not null && (loader.ToString() ?? "") != (_modLoaderFilter ?? ""))
                            continue;

                        loaders.Add(loader + " ");
                    }

                    if (loaders.Count == 0 && _modLoaderFilter is not null) continue;
                }

                if (loaders.Count == 0)
                    loaders.Add("");

                // 填充数据
                foreach (var loaderPrefix in loaders)
                {
                    var targetKey = loaderPrefix + verName;
                    AddVersionToDict(dict, versionDuplicateChecker, targetKey, version);
                }
            }

            // 处理“所选版本”卡片 (逻辑合并，减少二次循环)
            if (!string.IsNullOrEmpty(targetCardName))
            {
                var isMatchFilter = _instanceFilter is null ||
                                    GetGroupedVersionName(_targetInstance, groupedDrop, groupedOld)
                                        .StartsWithF(_instanceFilter);

                if (isMatchFilter && version.GameVersions.Contains(_targetInstance))
                    if (_targetLoader == ModComp.CompLoaderType.Any || version.ModLoaders.Contains(_targetLoader))
                        // 再次检查 version 是否符合筛选器（针对该文件的所有游戏版本）
                        if (_instanceFilter is null || version.GameVersions.Any(v =>
                                (GetGroupedVersionName(v, groupedDrop, groupedOld) ?? "") == (_instanceFilter ?? "")))
                            AddVersionToDict(dict, versionDuplicateChecker, targetCardName, version);
            }
        }

        // 3. 渲染 UI
        try
        {
            PanResults.Children.Clear();
            var additional = ModMain.frmMain.pageCurrent.additional;
            var additionalTitles = additional is not null
                ? additional.Value.ExpandedTitles
                : new List<string>();

            foreach (var pair in dict)
            {
                if (pair.Value.Count == 0)
                    continue;

                // 创建卡片组件
                var newCard = new MyCard
                {
                    Title = pair.Key,
                    Margin = new Thickness(0d, 0d, 0d, 15d)
                };

                // 闭包引用：避免在 Sub 内做高耗时操作
                var files = pair.Value;
                var currentKey = pair.Key;

                var newStack = new StackPanel
                {
                    Margin = new Thickness(20d, MyCard.SwapedHeight, 18d, 0d),
                    VerticalAlignment = VerticalAlignment.Top,
                    Tag = files
                };

                newCard.Children.Add(newStack);
                newCard.SwapControl = newStack;

                // 延迟加载安装项的逻辑
                newCard.InstallMethod = stack =>
                {
                    var list = (List<ModComp.CompFile>)stack.Tag;
                    // 排序和去重检查
                    list.Sort((a, b) => b.ReleaseDate.CompareTo(a.ReleaseDate));
                    var distinctCount = list.Select(f => f.DisplayName).Distinct().Count();
                    var badDisplayName = distinctCount != list.Count;

                    // 批量添加子项
                    switch (_project.Type)
                    {
                        case ModComp.CompType.ModPack:
                        {
                            foreach (var item in list)
                                stack.Children.Add(item.ToListItem(
                                    (sender, e) => ModMain.frmDownloadCompDetail.Install_Click((MyListItem)sender, e),
                                    ModMain.frmDownloadCompDetail.Save_Click, badDisplayName));
                            break;
                        }
                        case ModComp.CompType.World:
                        {
                            foreach (var item in list)
                                stack.Children.Add(item.ToListItem(
                                    (sender, e) =>
                                        ModMain.frmDownloadCompDetail.InstallWorld_Click((MyListItem)sender, e),
                                    ModMain.frmDownloadCompDetail.Save_Click, badDisplayName));
                            break;
                        }

                        default:
                        {
                            ModComp.CompFilesCardPreload(stack, list);
                            foreach (var item in list)
                                stack.Children.Add(item.ToListItem(ModMain.frmDownloadCompDetail.Save_Click,
                                    badDisplayName: badDisplayName));
                            break;
                        }
                    }
                };

                PanResults.Children.Add(newCard);

                // 展开逻辑
                if ((currentKey ?? "") == (targetCardName ?? "") || additionalTitles.Contains(newCard.Title))
                    newCard.StackInstall();
                else
                    newCard.IsSwapped = true;

                // 特殊提示
                if (currentKey == Lang.Text("Download.Comp.Detail.VersionGroup.Other"))
                    newStack.Children.Add(new MyHint
                    {
                        Text = Lang.Text("Download.Comp.Detail.VersionRecognitionDelayHint"),
                        Theme = MyHint.Themes.Yellow,
                        Margin = new Thickness(5d, 0d, 0d, 8d)
                    });
            }

            // 单卡片自动展开
            if (PanResults.Children.Count == 1) ((MyCard)PanResults.Children[0]).IsSwapped = false;
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化工程下载列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Download.Comp.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     辅助方法：向字典添加数据并处理去重
    /// </summary>
    private void AddVersionToDict(SortedDictionary<string, List<ModComp.CompFile>> dict,
        Dictionary<string, HashSet<ModComp.CompFile>> checker, string key, ModComp.CompFile version)
    {
        if (!dict.ContainsKey(key))
        {
            dict.Add(key, new List<ModComp.CompFile>());
            checker.Add(key, new HashSet<ModComp.CompFile>());
        }

        // 使用 HashSet.Add 判断是否重复，比 List.Contains 快得多
        if (checker[key].Add(version)) dict[key].Add(version);
    }


    private string GetGroupedVersionName(string name, bool groupedByDrop, bool foldOld)
    {
        if (name is null)
            return Lang.Text("Download.Comp.Detail.VersionGroup.Other");
        if (name.Contains('w'))
            return Lang.Text("Download.Comp.Detail.VersionGroup.Snapshot");
        if (foldOld && McInstanceInfo.VersionToDrop(name, true) < 120)
            return Lang.Text("Download.Comp.Detail.VersionGroup.Old");
        if (groupedByDrop)
        {
            var drop = McInstanceInfo.VersionToDrop(name, true);
            if (drop >= 0)
                return McInstanceInfo.DropToVersion(drop);
            return name;
        }

        return name;
    }

}
