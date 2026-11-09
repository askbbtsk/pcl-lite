using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.Minecraft.Backups;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public partial class PageInstanceSaves : IRefreshable
{
    private readonly DispatcherTimer fileSystemRefreshTimer;
    private readonly DispatcherTimer searchTimer;
    private FileSystemWatcher fileSystemWatcher;
    private bool isLoad;

    private object quickPlayFeature = false;

    private List<string> saveFolders = new();
    private string worldPath;

    public PageInstanceSaves()
    {
        InitializeComponent();
        fileSystemRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100d) };
        searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100d) };
        Loaded += PageSetupLaunch_Loaded;
        Unloaded += Page_Unloaded;
        fileSystemRefreshTimer.Tick += FileSystemRefreshTimer_Tick;
        searchTimer.Tick += SearchTimer_Tick;
        SearchBox.TextChanged += SearchRun;

    }

    void IRefreshable.Refresh()
    {
        RefreshSelf();
    }

    private void RefreshSelf()
    {
        Refresh();
        CheckQuickPlay();
    }

    public static void Refresh()
    {
        if (ModMain.frmInstanceSaves is not null)
            ModMain.frmInstanceSaves.Reload();
        ModMain.frmInstanceLeft.ItemWorld.Checked = true;
        HintService.Hint(Lang.Text("Instance.Saves.Status.Refreshing"), log: false);
    }

    private void PageSetupLaunch_Loaded(object sender, RoutedEventArgs e)
    {
        // 重复加载部分
        PanBack.ScrollToHome();
        worldPath = PageInstanceLeft.McInstance.PathIndie + @"saves\";
        if (!Directory.Exists(worldPath))
            Directory.CreateDirectory(worldPath);
        Reload();

        // 非重复加载部分
        if (isLoad)
            return;
        isLoad = true;
        CheckQuickPlay();

        // 初始化文件系统监视器和排序按钮
        SetupFileSystemWatcher();
        BtnSort.Click += BtnSortClick;
        SetSortMethod(_currentSortMethod);
    }

    private string GetFolderNameFromPath(string fullPath)
    {
        return string.IsNullOrEmpty(fullPath) ? "" :
            fullPath.EndsWith(@"\") ? new DirectoryInfo(fullPath).Parent?.Name : new DirectoryInfo(fullPath).Name;
    }

    private string GetFileNameFromPath(string fullPath)
    {
        return Path.GetFileName(fullPath);
    }

    private void SetupFileSystemWatcher()
    {
        if (fileSystemWatcher is not null) fileSystemWatcher.Dispose();

        // 确保目录存在
        if (!Directory.Exists(worldPath))
            Directory.CreateDirectory(worldPath);

        fileSystemWatcher = new FileSystemWatcher();
        fileSystemWatcher.Path = worldPath;
        fileSystemWatcher.IncludeSubdirectories = false;
        fileSystemWatcher.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.LastWrite;

        fileSystemWatcher.Created += OnFileSystemChanged;
        fileSystemWatcher.Deleted += OnFileSystemChanged;
        fileSystemWatcher.Renamed += OnFileSystemChanged;

        fileSystemWatcher.EnableRaisingEvents = true;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        fileSystemRefreshTimer.Stop();
        fileSystemRefreshTimer.Start();
    }

    private void FileSystemRefreshTimer_Tick(object sender, EventArgs e)
    {
        fileSystemRefreshTimer.Stop();
        ModBase.RunInUi(() => Reload(), true);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (fileSystemWatcher is not null)
        {
            fileSystemWatcher.Created -= OnFileSystemChanged;
            fileSystemWatcher.Deleted -= OnFileSystemChanged;
            fileSystemWatcher.Renamed -= OnFileSystemChanged;
            fileSystemWatcher.Dispose();
            fileSystemWatcher = null;
        }

        fileSystemRefreshTimer.Stop();
        searchTimer.Stop();
    }

    /// <summary>
    ///     确保当前页面上的信息已正确显示。
    /// </summary>
    public void Reload()
    {
        ModAnimation.AniControlEnabled += 1;
        PanBack.ScrollToHome();
        LoadFileList();
        ModAnimation.AniControlEnabled -= 1;
    }

    private void RefreshUI()
    {
        try
        {

            if (IsSearching)
            {
                var resultCount = _searchResult is null ? 0 : _searchResult.Count;
                PanListBack.Title = Lang.Text("Instance.Saves.SearchResultTitle", resultCount.ToString());
            }
            else
            {
                PanListBack.Title = Lang.Text("Instance.Saves.SaveListTitle", saveFolders.Count.ToString());
            }

            if (saveFolders.Count == 0)
            {
                PanNoWorld.Visibility = Visibility.Visible;
                PanContent.Visibility = Visibility.Collapsed;
                PanNoWorld.UpdateLayout();
            }
            else
            {
                PanNoWorld.Visibility = Visibility.Collapsed;
                PanContent.Visibility = Visibility.Visible;
                PanContent.UpdateLayout();

                var showingSaves = (IsSearching ? _searchResult : saveFolders).ToList();
                var selectedInstance = PageInstanceLeft.McInstance;
                var capturedInstancePath = Path.GetFullPath(selectedInstance.PathInstance);

                if (showingSaves.Any())
                {
                    var sortMethod = GetSortMethod(_currentSortMethod);
                    showingSaves.Sort((a, b) => sortMethod(a, b));
                }

                ModAnimation.AniControlEnabled += 1;
                PanList.Children.Clear();

                foreach (var curFolder in showingSaves)
                {
                    // 检查文件夹是否仍然存在
                    if (!Directory.Exists(curFolder)) continue;

                    var saveLogo = Path.Combine(curFolder, "icon.png");
                    var tmpCurFolder = Path.GetFullPath(curFolder);
                    if (File.Exists(saveLogo))
                    {
                        var target =
                            $@"{PageInstanceLeft.McInstance.PathInstance}PCL\ImgCache\{ModBase.GetStringMD5(saveLogo)}.png";
                        ModBase.CopyFile(saveLogo, target);
                        saveLogo = target;
                    }
                    else
                    {
                        saveLogo = ModBase.pathImage + "Icons/NoIcon.png";
                    }

                    var worldItem = new MyListItem
                    {
                        Logo = saveLogo,
                        Title = GetFolderNameFromPath(curFolder),
                        Info =
                            Lang.Text("Instance.Saves.CreationTime", Lang.Date(Directory.GetCreationTime(curFolder), "d"), Lang.Date(Directory.GetLastWriteTime(curFolder), "d")),
                        Type = MyListItem.CheckType.Clickable
                    };
                    worldItem.Click += (_, _) => ModMain.frmMain.PageChange(new FormMain.PageStackData
                        { page = FormMain.PageType.VersionSaves, additional = (null, null, null, ModComp.CompLoaderType.Any, ModComp.CompType.Any, tmpCurFolder) });

                    var btnOpen = new MyIconButton
                    {
                        SvgIcon = "lucide/folder-open",
                        ToolTip = Lang.Text("Common.Action.Open")
                    };
                    btnOpen.Click += (_, _) => ModBase.OpenExplorer(tmpCurFolder);
                    var btnDelete = new MyIconButton
                    {
                        SvgIcon = "lucide/trash-2",
                        ToolTip = Lang.Text("Common.Action.Delete")
                    };
                    btnDelete.Click += (_, _) =>
                    {
                        worldItem.IsEnabled = false;
                        worldItem.Info = Lang.Text("Instance.Saves.Deleting");
                        ModBase.RunInNewThread(() =>
                        {
                            try
                            {
                                FileSystem.DeleteDirectory(tmpCurFolder, UIOption.OnlyErrorDialogs,
                                    Config.Launch.DeleteToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently);
                                HintService.Hint(Lang.Text(Config.Launch.DeleteToRecycleBin
                                    ? "Instance.Saves.DeletedToRecycleBin" : "Instance.Saves.DeletedPermanently"));
                                ModBase.RunInUiWait(() => RemoveItem(worldItem));
                            }
                            catch (Exception ex)
                            {
                                ModBase.Log(
                                    ex,
                                    Lang.Text("Instance.Saves.DeleteFailed"),
                                    ModBase.LogLevel.Hint,
                                    userSummary: Lang.Text("Instance.Saves.DeleteFailed"));
                                ModBase.RunInUiWait(() => Reload());
                            }
                        });
                    };
                    var btnCopy = new MyIconButton
                    {
                        SvgIcon = "lucide/copy",
                        ToolTip = Lang.Text("Common.Action.Copy")
                    };
                    btnCopy.Click += (_, _) =>
                    {
                        try
                        {
                            if (Directory.Exists(tmpCurFolder))
                            {
                                Clipboard.SetFileDropList(new StringCollection { tmpCurFolder });
                                HintService.Hint(Lang.Text("Instance.Saves.CopiedToClipboard"));
                                HintService.Hint(Lang.Text("Instance.Saves.CopyPasteWarning"));
                            }
                            else
                            {
                                HintService.Hint(Lang.Text("Instance.Saves.FolderNotFound"));
                            }
                        }
                        catch (Exception ex)
                        {
                            ModBase.Log(
                                ex,
                                Lang.Text("Instance.Saves.CopyFailed"),
                                ModBase.LogLevel.Hint,
                                userSummary: Lang.Text("Instance.Saves.CopyFailed"));
                        }
                    };
                    var btnInfo = new MyIconButton
                    {
                        SvgIcon = "lucide/info",
                        ToolTip = Lang.Text("Instance.Saves.Details")
                    };
                    btnInfo.Click += (_, _) => ModMain.frmMain.PageChange(new FormMain.PageStackData
                        { page = FormMain.PageType.VersionSaves, additional = (null, null, null, ModComp.CompLoaderType.Any, ModComp.CompType.Any, tmpCurFolder) });

                    var btnBackups = new MyIconButton
                    {
                        SvgIcon = "lucide/archive",
                        ToolTip = ModWorldBackups.Text("button")
                    };
                    var backupCard = CreateBackupCard(capturedInstancePath, tmpCurFolder);
                    btnBackups.Click += (_, _) => backupCard.IsSwapped = !backupCard.IsSwapped;
                    var btnLaunch = new MyIconButton
                    {
                        SvgIcon = "lucide/play",
                        ToolTip = Lang.Text("Instance.Saves.QuickPlay")
                    };
                    btnLaunch.Click += (_, _) =>
                    {
                        var worldName = GetFileNameFromPath(tmpCurFolder);
                        var launchOptions = new ModLaunch.McLaunchOptions
                        {
                            WorldName = worldName,
                            WorldPath = tmpCurFolder,
                            instance = PageInstanceLeft.McInstance
                        };
                        ModLaunch.McLaunchStart(launchOptions);
                        ModMain.frmMain.PageChange(new FormMain.PageStackData { page = FormMain.PageType.Launch });
                    };
                    if ((bool)quickPlayFeature)
                        worldItem.Buttons = new[] { btnOpen, btnDelete, btnCopy, btnInfo, btnBackups, btnLaunch };
                    else
                        worldItem.Buttons = new[] { btnOpen, btnDelete, btnCopy, btnInfo, btnBackups };

                    PanList.Children.Add(worldItem);
                    PanList.Children.Add(backupCard);
                }

                ModAnimation.AniControlEnabled -= 1;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Instance.Saves.RefreshUiFailed"),
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Instance.Saves.RefreshUiFailed"));
        }
    }


    private MyCard CreateBackupCard(string instancePath, string worldFolder)
    {
        var items = new StackPanel
        {
            Margin = new Thickness(18, 42, 18, 0),
            Tag = worldFolder
        };
        var card = new MyCard
        {
            Tag = worldFolder,
            Title = ModWorldBackups.Text("card", GetFolderNameFromPath(worldFolder)),
            Margin = new Thickness(42, 0, 0, 10),
            CanSwap = true,
            IsSwapped = true,
            SwapControl = items,
            InstallMethod = stack => FillBackupCard(stack, instancePath, worldFolder)
        };
        card.Children.Add(items);
        return card;
    }

    private void FillBackupCard(StackPanel stack, string instancePath, string worldFolder)
    {
        stack.Children.Clear();
        var createButton = new MyButton
        {
            Text = ModWorldBackups.Text("create"),
            Height = 32,
            MinWidth = 130,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ColorType = MyButton.ColorState.Highlight
        };
        createButton.Click += (_, _) => RunBackupWork(instancePath, worldFolder, "create", () =>
        {
            ModWorldBackups.CreateManual(instancePath, worldFolder);
            return ModWorldBackups.Text("created");
        });
        stack.Children.Add(createButton);

        IReadOnlyList<BackupSnapshot> backups;
        try
        {
            backups = ModWorldBackups.List(worldFolder);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "读取世界备份列表失败");
            stack.Children.Add(_BackupHint(ModWorldBackups.Text("failed")));
            return;
        }

        if (backups.Count == 0)
            stack.Children.Add(_BackupHint(ModWorldBackups.Text("empty")));

        foreach (var snapshot in backups)
        {
            var row = new MyListItem
            {
                Title = snapshot.CreatedUtc.ToLocalTime().ToString("g"),
                Info = Lang.Text("Instance.Backup.ItemDetail", ModWorldBackups.DescribeReason(snapshot), snapshot.Files.Count, snapshot.ArchiveLength),
                SvgIcon = "lucide/file-archive"
            };
            var restore = new MyIconButton { SvgIcon = "lucide/rotate-ccw", ToolTip = ModWorldBackups.Text("restore") };
            restore.Click += (_, _) =>
            {
                if (ModMain.MyMsgBox(ModWorldBackups.Text("confirmRestore"), ModWorldBackups.Text("restore"),
                        ModWorldBackups.Text("restore"), Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
                RunBackupWork(instancePath, worldFolder, "restore", () =>
                {
                    ModWorldBackups.Restore(instancePath, worldFolder, snapshot.Id);
                    return ModWorldBackups.Text("restored");
                });
            };
            var delete = new MyIconButton { SvgIcon = "lucide/trash-2", ToolTip = ModWorldBackups.Text("delete") };
            delete.Click += (_, _) =>
            {
                if (ModMain.MyMsgBox(Lang.Text(Config.Launch.DeleteToRecycleBin ? "Instance.Saves.Backup.ConfirmDelete.Windows" : "Instance.Saves.Backup.ConfirmDelete.Permanent"), ModWorldBackups.Text("delete"),
                        ModWorldBackups.Text("delete"), Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
                RunBackupWork(instancePath, worldFolder, "delete", () =>
                {
                    ModWorldBackups.Delete(worldFolder, snapshot.Id);
                    return Lang.Text(Config.Launch.DeleteToRecycleBin ? "Instance.Saves.Backup.DeletedToWindowsRecycleBin" : "Instance.Saves.Backup.DeletedPermanently");
                });
            };
            row.Buttons = [restore, delete];
            stack.Children.Add(row);
        }

    }

    private static TextBlock _BackupHint(string text)
    {
        var hint = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7d,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray1");
        return hint;
    }

    private void RunBackupWork(string instancePath, string worldFolder, string operation, Func<string> work)
    {
        var instanceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(instancePath));
        var taskName = $"{instanceName} - {ModWorldBackups.Text("card", GetFolderNameFromPath(worldFolder))}";
        var task = new ModLoader.LoaderTask<int, int>(taskName, loader =>
        {
            var token = loader.AbortedToken;
            token.ThrowIfCancellationRequested();
            loader.output = 0;
            var successText = work();
            token.ThrowIfCancellationRequested();
            ModBase.RunInUi(() =>
            {
                HintService.Hint(successText, HintType.Success);
                if (PageInstanceLeft.McInstance is { } selected
                    && string.Equals(Path.GetFullPath(selected.PathInstance), instancePath, StringComparison.OrdinalIgnoreCase))
                    Reload();
            });
        });
        var combo = new ModLoader.LoaderCombo<int>(taskName, [task]);
        combo.OnStateChanged = loader =>
        {
            if (loader.State == ModBase.LoadState.Failed)
            {
                var error = loader.Error;
                ModBase.Log(error ?? new InvalidOperationException("Backup operation failed."), $"世界备份操作失败：{operation}");
                HintService.Hint(ModWorldBackups.Text("failed"), HintType.Error);
            }
        };
        combo.Start(1);
        ModLoader.LoaderTaskbarAdd(combo);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
    }

    private void CheckQuickPlay()
    {
        try
        {
            var cur = new ModLaunch.LaunchArgument(PageInstanceLeft.McInstance);
            quickPlayFeature = cur.HasArguments("--quickPlaySingleplayer");
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "检查存档快捷启动失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
        }
    }

    private void LoadFileList()
    {
        try
        {
            ModBase.Log("[World] 刷新存档文件");
            saveFolders.Clear();
            if (Directory.Exists(worldPath))
                saveFolders = Directory.EnumerateDirectories(worldPath).ToList();
            else
                saveFolders = new List<string>();

            if (ModBase.modeDebug)
                ModBase.Log("[World] 共发现 " + saveFolders.Count + " 个存档文件夹", ModBase.LogLevel.Debug);
            PanList.Children.Clear();
            CheckQuickPlay();

            if (ModBase.modeDebug)
            {
                if ((bool)quickPlayFeature)
                    ModBase.Log("[World] 该实例支持存档快捷启动", ModBase.LogLevel.Debug);
                else
                    ModBase.Log("[World] 该实例不支持存档快捷启动", ModBase.LogLevel.Debug);
            }

            RefreshUI(); // 确保UI刷新
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Instance.Saves.LoadListFailed"),
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Instance.Saves.LoadListFailed"));
        }
    }

    private void RemoveItem(MyListItem item)
    {
        if (PanList.Children.IndexOf(item) == -1)
            return;
        var index = PanList.Children.IndexOf(item);
        PanList.Children.RemoveAt(index);
        if (index < PanList.Children.Count && PanList.Children[index] is MyCard { Tag: string })
            PanList.Children.RemoveAt(index);
        RefreshUI();
    }

    private void BtnOpenFolder_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenExplorer(worldPath);
    }

    private void BtnPaste_Click(object sender, MouseButtonEventArgs e)
    {
        var files = Clipboard.GetFileDropList();
        var loaders = new List<ModLoader.LoaderBase>();
        loaders.Add(new ModLoader.LoaderTask<int, int>("Copy saves", _ =>
        {
            var copied = 0;
            foreach (var i in files)
                try
                {
                    if (Directory.Exists(i))
                    {
                        if (Directory.Exists(worldPath + GetFolderNameFromPath(i)))
                        {
                            HintService.Hint(Lang.Text("Instance.Saves.DuplicateFolder", GetFolderNameFromPath(i)));
                        }
                        else
                        {
                            ModBase.CopyDirectory(i, worldPath + GetFolderNameFromPath(i));
                            copied += 1;
                        }
                    }
                    else
                    {
                        HintService.Hint(Lang.Text("Instance.Saves.SourceNotFolder"));
                    }
                }
                catch (Exception ex)
                {
                    ModBase.Log(
                        ex,
                        Lang.Text("Instance.Saves.PasteFolderFailed"),
                        ModBase.LogLevel.Hint,
                        userSummary: Lang.Text("Instance.Saves.PasteFolderFailed"));
                }

            if (copied > 0)
                HintService.Hint(Lang.Text("Instance.Saves.PastedCount", copied.ToString()), HintType.Success);
            ModBase.RunInUi(() => Reload());
        }));
        var loader = new ModLoader.LoaderCombo<int>($"{PageInstanceLeft.McInstance.Name} - {Lang.Text("Instance.Saves.CopySave")}", loaders)
            { OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly };
        loader.Start(1);
        ModLoader.LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
    }

    private void BtnDownloadNew_Click(object sender, MouseButtonEventArgs e)
    {
        ModMain.frmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadWorld);
        PageComp.targetVersion = PageInstanceLeft.McInstance; // 将当前实例设置为筛选器
    }

    #region 搜索和排序

    private SortMethod _currentSortMethod = SortMethod.FileName;
    private List<string> _searchResult;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchBox.Text);

    private enum SortMethod
    {
        FileName,
        CreateTime,
        ModifyTime
    }

    private string GetSortName(SortMethod method)
    {
        switch (method)
        {
            case SortMethod.FileName:
            {
                return Lang.Text("Instance.Saves.SortFileName");
            }
            case SortMethod.CreateTime:
            {
                return Lang.Text("Instance.Saves.SortCreateTime");
            }
            case SortMethod.ModifyTime:
            {
                return Lang.Text("Instance.Saves.SortModifyTime");
            }

            default:
            {
                return Lang.Text("Instance.Saves.SortFileName");
            }
        }
    }

    private void SetSortMethod(SortMethod target)
    {
        _currentSortMethod = target;
        BtnSort.Text = Lang.Text("Instance.Saves.SortBy", GetSortName(target));
        RefreshUI();
    }

    private void BtnSortClick(object sender, EventArgs e)
    {
        var body = new ContextMenu();
        foreach (SortMethod i in Enum.GetValues(typeof(SortMethod)))
        {
            var item = new MyMenuItem();
            item.Header = GetSortName(i);
            item.Click += (_, _) => SetSortMethod(i);
            body.Items.Add(item);
        }

        body.PlacementTarget = (UIElement)sender;
        body.Placement = PlacementMode.Bottom;
        body.IsOpen = true;
    }

    private void SearchRun(object sender, EventArgs e)
    {
        searchTimer.Stop();
        searchTimer.Start();
    }

    private void SearchTimer_Tick(object sender, EventArgs e)
    {
        searchTimer.Stop();
        PerformSearch();
    }

    private void PerformSearch()
    {
        try
        {
            if (IsSearching)
            {
                var queryList = new List<ModBase.SearchEntry<string>>();
                foreach (var saveFolder in saveFolders)
                {
                    var folderName = GetFolderNameFromPath(saveFolder);
                    var searchSource = new List<ModBase.SearchSource>();
                    searchSource.Add(new ModBase.SearchSource(folderName, 1d));
                    queryList.Add(new ModBase.SearchEntry<string> { item = saveFolder, searchSource = searchSource });
                }

                _searchResult = ModBase.Search(queryList, SearchBox.Text, ModBase.MaxLocalSearchDepth, 0.35d).Select(r => r.item).ToList();
            }
            else
            {
                _searchResult = null;
            }

            RefreshUI();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, Lang.Text("Instance.Saves.SearchError"));
        }
    }

    private Func<string, string, int> GetSortMethod(SortMethod method)
    {
        switch (method)
        {
            case SortMethod.FileName:
            {
                return (a, b) => string.Compare(GetFolderNameFromPath(a), GetFolderNameFromPath(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            case SortMethod.CreateTime:
            {
                return (a, b) => Directory.GetCreationTime(b).CompareTo(Directory.GetCreationTime(a));
            }
            case SortMethod.ModifyTime:
            {
                return (a, b) => Directory.GetLastWriteTime(b).CompareTo(Directory.GetLastWriteTime(a));
            }

            default:
            {
                return (a, b) => string.Compare(GetFolderNameFromPath(a), GetFolderNameFromPath(b),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion
}
