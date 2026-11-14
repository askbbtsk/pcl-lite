using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Minecraft.Profile;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft;
using PCL.Core.Plugins;
using PCL.Core.UI;
using PCL.Core.UI.Navigation;
using PCL.Core.UI.Theme;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Plugin.SDK;

namespace PCL;

public partial class FormMain
{

    // 文件拖放
    private void HandleDrag(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Handled && e.Effects != DragDropEffects.None)
                return;
            // 缓存
            e.Handled = true;
            if (ReferenceEquals(e.Data, _HandleDrag_PrevData))
            {
                e.Effects = _HandleDrag_PrevEffects;
                return;
            }

            // 确定拖放效果
            e.Effects = DragDropEffects.None;
            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                var str = (string)e.Data.GetData(DataFormats.Text);
                if (str.StartsWithF("authlib-injector:yggdrasil-server:"))
                    e.Effects = DragDropEffects.Copy;
                else if (str.StartsWithF("file:///")) e.Effects = DragDropEffects.Copy;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files is not null && files.Length > 0) e.Effects = DragDropEffects.Link;
            }

            _HandleDrag_PrevData = e.Data;
            _HandleDrag_PrevEffects = e.Effects;
            ModBase.Log("[System] 设置拖放类型：" + ModBase.GetStringFromEnum(e.Effects));
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "处理拖放时出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }
    }


    private void FrmMain_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                // 获取文本
                try
                {
                    var str = (string)e.Data.GetData(DataFormats.Text);
                    ModBase.Log("[System] 接受文本拖拽：" + str);
                    if (str.StartsWithF("authlib-injector:yggdrasil-server:"))
                    {
                        // Authlib 拖拽
                        e.Handled = true;
                        e.Effects = DragDropEffects.Copy;
                        var authlibServer =
                            WebUtility.UrlDecode(str.Substring("authlib-injector:yggdrasil-server:".Length));
                        ModBase.Log("[System] Authlib 拖拽：" + authlibServer);
                        if (!new HttpValidator().Validate(authlibServer).IsValid)
                        {
                            HintService.Hint(Lang.Text("Main.FileDrag.AuthlibInvalid", authlibServer), HintType.Error);
                            return;
                        }

                        if (ModMain.MyMsgBox(Lang.Text("Main.FileDrag.CreateAuthlibProfile", authlibServer), Lang.Text("Main.FileDrag.CreateAuthlibProfileTitle"),
                                Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")) == 2)
                            return;
                        ModBase.RunInUi(() =>
                        {
                            PageLoginAuth.draggedAuthServer = authlibServer;
                            ModMain.frmLaunchLeft.RefreshPage(true, ModLaunch.McLoginType.Auth);
                        });
                        if (pageCurrent == PageType.InstanceSetup && PageCurrentSub == PageSubType.VersionSetup)
                            // 正在服务器选项页，需要刷新设置项显示
                            ModMain.frmInstanceSetup.Reload();
                    }
                    else if (str.StartsWithF("file:///"))
                    {
                        // 文件拖拽（例如从浏览器下载窗口拖入）
                        var filePath = WebUtility.UrlDecode(str).Substring("file:///".Length).Replace("/", @"\");
                        e.Handled = true;
                        e.Effects = DragDropEffects.Copy;
                        FileDrag(new List<string> { filePath });
                    }
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "无法接取文本拖拽事件", ModBase.LogLevel.Developer);
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // 获取文件并检查
                var filePathRaw = e.Data.GetData(DataFormats.FileDrop);
                if (filePathRaw is null) // #2690
                {
                    HintService.Hint(Lang.Text("Main.FileDrag.ExtractFirst"), HintType.Error);
                    return;
                }

                e.Handled = true;
                e.Effects = DragDropEffects.Link;
                FileDrag((IEnumerable<string>)filePathRaw);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "接取拖拽事件失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }
    }


    private void FileDrag(IEnumerable<string> filePathList)
    {
        ModBase.RunInNewThread(() =>
        {
            var dropped = filePathList.ToArray();
            if (TryInstallDroppedTheme(dropped)) return;
            var filePath = filePathList.First();
            ModBase.Log("[System] 接受文件拖拽：" + filePath + (filePathList.Any() ? $" 等 {filePathList.Count()} 个文件" : ""),
                ModBase.LogLevel.Developer);
            // 基础检查
            if (Directory.Exists(filePathList.First()) && !File.Exists(filePathList.First()))
            {
                HintService.Hint(Lang.Text("Main.FileDrag.FileOnly"), HintType.Error);
                return;
            }

            if (!File.Exists(filePathList.First()))
            {
                HintService.Hint(Lang.Text("Main.FileDrag.FileNotFound", filePathList.First()), HintType.Error);
                return;
            }

            // 多文件拖拽
            if (filePathList.Count() > 1)
            {
                // 检查是否为同类型文件
                var firstExtension = filePathList.First().AfterLast(".").ToLower();
                var allSameType = filePathList.All(f => (f.AfterLast(".").ToLower() ?? "") == (firstExtension ?? ""));

                if (allSameType &&
                    new[] { "jar", "litemod", "disabled", "old", "litematic", "nbt", "schematic", "schem" }.Contains(
                        firstExtension))
                {
                }
                // 允许同类型的 Mod 文件或投影文件批量拖拽
                else
                {
                    HintService.Hint(Lang.Text("Main.FileDrag.SameTypeOnly"), HintType.Error);
                    return;
                }
            }

            // 主页
            var extension = filePath.AfterLast(".").ToLower();
            if (extension == "xaml")
            {
                ModBase.Log("[System] 文件后缀为 XAML，作为主页加载");
                if (File.Exists(ModBase.exePath + @"PCL\Custom.xaml"))
                    if (ModMain.MyMsgBox(Lang.Text("Main.FileDrag.HomepageExists"), Lang.Text("Main.FileDrag.OverwriteTitle"), Lang.Text("Common.Action.Overwrite"), Lang.Text("Common.Action.Cancel")) == 2)
                        return;

                ModBase.CopyFile(filePath, ModBase.exePath + @"PCL\Custom.xaml");
                ModBase.RunInUi(() =>
                {
                    Config.Preference.Homepage.Type = 1;
                    ModMain.frmLaunchRight.ForceRefresh();
                    HintService.Hint(Lang.Text("Main.FileDrag.HomepageLoaded"), HintType.Success);
                });
                return;
            }

            // 安装 Mod
            if (PageInstanceCompResource.InstallMods(filePathList))
                return;
            // 安装投影文件
            if (new[] { "litematic", "nbt", "schematic", "schem" }.Contains(extension))
            {
                ModBase.Log($"[System] 文件为 {extension} 格式，尝试作为原理图安装");
                // 获取当前文件夹路径（如果在资源管理页面）
                string targetFolderPath = null;
                if (pageCurrent == PageType.InstanceSetup && PageCurrentSub == PageSubType.VersionSchematic &&
                    ModMain.frmInstanceSchematic is not null &&
                    ModMain.frmInstanceSchematic is PageInstanceCompResource)
                    targetFolderPath = ModMain.frmInstanceSchematic.CurrentFolderPath;
                PageInstanceCompResource.InstallCompFiles(filePathList, ModComp.CompType.Schematic, targetFolderPath);
                return;
            }

            // 处理资源安装
            if (pageCurrent == PageType.InstanceSetup && new[] { "zip" }.Any(i => (i ?? "") == (extension ?? "")))
                switch (PageCurrentSub)
                {
                    case PageSubType.VersionWorld:
                    {
                        var destFolder = PageInstanceLeft.McInstance.PathIndie + @"saves\" +
                                         ModBase.GetFileNameWithoutExtentionFromPath(filePath);
                        var destLevelDat = Path.Combine(destFolder, "level.dat");
                        if (Directory.Exists(destFolder))
                        {
                            HintService.Hint(Lang.Text("Main.FileDrag.SameFolderExists", destFolder), HintType.Error);
                            return;
                        }

                        var extractFolder = Path.Combine(ModBase.pathTemp, "Cache", "WorldImport", ModBase.GetUuid().ToString());
                        try
                        {
                            ModBase.ExtractFile(filePath, extractFolder);
                            var saveRoot = SaveImportHelper.GetSaveRootDirectory(extractFolder);
                            if (saveRoot is null)
                            {
                                HintService.Hint(Lang.Text("Main.FileDrag.SaveNotFound"), HintType.Error);
                                return;
                            }

                            ModBase.CopyDirectory(saveRoot, destFolder);
                            if (!File.Exists(destLevelDat))
                            {
                                if (Directory.Exists(destFolder))
                                    ModBase.DeleteDirectory(destFolder, true);
                                HintService.Hint(Lang.Text("Main.FileDrag.SaveInvalid"), HintType.Error);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            if (Directory.Exists(destFolder))
                                ModBase.DeleteDirectory(destFolder, true);
                            ModBase.Log(
                                ex,
                                Lang.Text("Main.FileDrag.SaveImportFailed"),
                                ModBase.LogLevel.Hint,
                                userSummary: Lang.Text("Main.FileDrag.SaveImportFailed"));
                            return;
                        }
                        finally
                        {
                            if (Directory.Exists(extractFolder))
                                ModBase.DeleteDirectory(extractFolder, true);
                        }

                        HintService.Hint(Lang.Text("Main.FileDrag.Imported", ModBase.GetFileNameWithoutExtentionFromPath(filePath)),
                            HintType.Success);
                        if (ModMain.frmInstanceSaves is not null)
                            ModBase.RunInUi(() => ModMain.frmInstanceSaves.Reload());
                        return;
                    }
                    case PageSubType.VersionResourcePack:
                    {
                        var destFile = PageInstanceLeft.McInstance.PathIndie + @"resourcepacks\" +
                                       ModBase.GetFileNameFromPath(filePath);
                        if (File.Exists(destFile))
                        {
                            HintService.Hint(Lang.Text("Main.FileDrag.SameFileExists", destFile), HintType.Error);
                            return;
                        }

                        ModBase.CopyFile(filePath, destFile);
                        HintService.Hint(Lang.Text("Main.FileDrag.Imported", ModBase.GetFileNameFromPath(filePath)), HintType.Success);
                        if (ModMain.frmInstanceResourcePack is not null)
                            ModBase.RunInUi(() => ModMain.frmInstanceResourcePack.ReloadCompFileList());
                        return;
                    }
                    case PageSubType.VersionShader:
                    {
                        var destFile = PageInstanceLeft.McInstance.PathIndie + @"shaderpacks\" +
                                       ModBase.GetFileNameFromPath(filePath);
                        if (File.Exists(destFile))
                        {
                            HintService.Hint(Lang.Text("Main.FileDrag.SameFileExists", destFile), HintType.Error);
                            return;
                        }

                        ModBase.CopyFile(filePath, destFile);
                        HintService.Hint(Lang.Text("Main.FileDrag.Imported", ModBase.GetFileNameFromPath(filePath)), HintType.Success);
                        if (ModMain.frmInstanceShader is not null)
                            ModBase.RunInUi(() => ModMain.frmInstanceShader.ReloadCompFileList());
                        return;
                    }
                }

            // 处理投影文件
            if (pageCurrent == PageType.InstanceSetup &&
                new[] { "litematic", "nbt", "schematic", "schem" }.Contains(extension) &&
                PageCurrentSub == PageSubType.VersionSchematic)
            {
                var destFile = PageInstanceLeft.McInstance.PathIndie + @"schematics\" +
                               ModBase.GetFileNameFromPath(filePath);
                if (File.Exists(destFile))
                {
                    HintService.Hint(Lang.Text("Main.FileDrag.SameFileExists", destFile), HintType.Error);
                    return;
                }

                Directory.CreateDirectory(PageInstanceLeft.McInstance.PathIndie + @"schematics\");
                ModBase.CopyFile(filePath, destFile);
                HintService.Hint(Lang.Text("Main.FileDrag.Imported", ModBase.GetFileNameFromPath(filePath)), HintType.Success);
                if (ModMain.frmInstanceSchematic is not null)
                    ModBase.RunInUi(() => ModMain.frmInstanceSchematic.ReloadCompFileList());
                return;
            }

            // 安装整合包
            if (new[] { "zip", "rar", "mrpack" }.Any(t =>
                    (t ?? "") == (extension ?? ""))) // 部分压缩包是 zip 格式但后缀为 rar，总之试一试
            {
                ModBase.Log("[System] 文件为压缩包，尝试作为整合包安装");
                try
                {
                    ModModpack.ModpackInstall(filePath);
                    return;
                }
                catch (ModBase.CancelledException)
                {
                    return; // 用户主动取消
                }
                catch (Exception)
                {
                    // 安装失败，继续往后尝试
                }
            }

            // 错误报告分析
            do
            {
                try
                {
                    ModBase.Log("[System] 尝试进行错误报告分析");
                    var analyzer = new CrashAnalyzer(ModBase.GetUuid());
                    analyzer.Import(filePath);
                    if (!analyzer.Prepare())
                        break;
                    analyzer.Analyze();
                    analyzer.Output(true, new List<string>());
                    return;
                }
                catch (Exception ex)
                {
                    ModBase.Log(
                        ex,
                        "自主错误报告分析失败",
                        ModBase.LogLevel.Feedback,
                        userSummary: Lang.Text("Main.Error.OperationFailed"));
                }
            } while (false);

            // 未知操作
            HintService.Hint(Lang.Text("Main.FileDrag.UnknownOperation"));
        }, "文件拖拽");
    }


    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 30)
        {
            var nowDate = DateTime.Now;
            if (nowDate.Date == ModBase.applicationOpenTime.Date)
            {
                ModBase.Log("[System] 系统时间微调为：" + nowDate.ToLongDateString() + " " + nowDate.ToLongTimeString());
                isSystemTimeChanged = false;
            }
            else
            {
                ModBase.Log("[System] 系统时间修改为：" + nowDate.ToLongDateString() + " " + nowDate.ToLongTimeString());
                isSystemTimeChanged = true;
            }
        }
        else if (msg == 400 * 16 + 2)
        {
            ModBase.Log("[System] 收到置顶信息：" + hwnd.ToInt64());
            if (!isWindowLoadFinished)
            {
                ModBase.Log("[System] 窗口尚未加载完成，忽略置顶请求");
                return nint.Zero;
            }

            ShowWindowToTop();
            handled = true;
        }
        else if (msg == 26) // WM_SETTINGCHANGE
        {
            if (Marshal.PtrToStringAuto(lParam) == "ImmersiveColorSet")
            {
                ModBase.Log($"[System] 系统主题更改，深色模式：{SystemTheme.IsSystemInDarkMode()}");
                if (Config.Preference.Theme.ColorMode == ColorMode.System &&
                    (ThemeManager.IsDarkMode != SystemTheme.IsSystemInDarkMode())) ThemeService.RefreshColorMode();
            }
        }

        return nint.Zero;
    }


    // 窗口隐藏与置顶
    public bool Hidden
    {
        get => field;
        set
        {
            if (field == value)
                return;
            field = value;
            if (value)
            {
                // 隐藏
                Left -= 10000d;
                ShowInTaskbar = false;
                Visibility = Visibility.Hidden;
                ModBase.Log("[System] 窗口已隐藏，位置：(" + Left + "," + Top + ")");
            }
            else
            {
                // 取消隐藏
                if (Left < -2000)
                    Left += 10000d;
                ShowWindowToTop();
            }
        }
    }


    // 解决龙猫的非通用实现史山
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (Hidden)
            Hidden = false;
    }


    /// <summary>
    ///     把当前窗口拖到最前面。
    /// </summary>
    public void ShowWindowToTop()
    {
        ModBase.RunInUi(() =>
        {
            // 这一坨乱七八糟的，别改，改了指不定就炸了，自己电脑还复现不出来
            Visibility = Visibility.Visible;
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Hidden = false;
            Topmost = true; // 偶尔 SetForegroundWindow 失效
            Topmost = false;
            ModMain.SetForegroundWindow(ModBase.frmHandle);
            Focus();
            ModBase.Log($"[System] 窗口已置顶，位置：({Left}, {Top}), {Width} x {Height}");
        });
    }


    // 背景视频循环播放
    private void VideoEnded(object sender, RoutedEventArgs e)
    {
        VideoBack.Position = TimeSpan.Zero;
        ModVideoBack.VideoPlay();
    }


    // 最小化时暂停背景视频
    private void WindowStateChanged(object sender, EventArgs e)
    {
        switch (WindowState)
        {
            case WindowState.Minimized:
            {
                ModVideoBack.isMinimized = true;
                ModVideoBack.VideoPause();
                break;
            }
            case WindowState.Normal:
            {
                ModVideoBack.isMinimized = false;
                ModVideoBack.VideoPlay();
                break;
            }
        }
    }




    // 页面种类与属性
    // 注意，这一枚举在 “切换页面” EventType 中调用，应视作公开 API 的一部分
    /// <summary>
    ///     页面种类。
    /// </summary>
    public enum PageType
    {
        /// <summary>
        ///     启动。
        /// </summary>
        Launch = 0,

        /// <summary>
        ///     下载。
        /// </summary>
        Download = 1,

        /// <summary>
        ///     基岩版（下载 + 启动）。与导航栏 <c>PanTitleSelect</c> 中的下标一致，紧跟在「下载」右侧。
        /// </summary>
        Bedrock = 2,

        /// <summary>Plugin marketplace and installed plugin management. Added without renumbering existing pages.</summary>
        Plugins = 14,

        /// <summary>
        ///     设置。
        /// </summary>
        Setup = 3,

        /// <summary>
        ///     联机。
        /// </summary>
        Tools = 4,

        /// <summary>
        ///     实例选择。这是一个副页面。
        /// </summary>
        InstanceSelect = 5,

        /// <summary>
        ///     任务管理。这是一个副页面。
        /// </summary>
        TaskManager = 6,

        /// <summary>
        ///     实例设置。这是一个副页面。
        /// </summary>
        InstanceSetup = 7,

        /// <summary>
        ///     资源工程详情。这是一个副页面。
        /// </summary>
        CompDetail = 8,

        /// <summary>
        ///     帮助详情。这是一个副页面。
        /// </summary>
        HelpDetail = 9,

        /// <summary>
        ///     游戏实时日志。这是一个副页面。
        /// </summary>
        GameLog = 10,

        /// <summary>
        ///     存档详细管理，这是一个副页面。
        /// </summary>
        VersionSaves = 12,

        /// <summary>
        ///     Lunar Client（实验特性）。导航栏里排在「基岩版」右侧。
        ///     <para>
        ///         数值故意取 13 而不是插进中间：导航按钮的 <c>Tag</c> 会被直接强转成这个枚举，
        ///         但按钮的**排列顺序由 XAML 决定**，与数值无关。所以新增一级页面时
        ///         追加一个没被占用的值即可，不需要给 Setup / Tools / 各个副页面重新编号。
        ///         </para>
        /// </summary>
        Lunar = 13,

        /// <summary>
        ///     IPv6 直连联机。导航栏里排在「Lunar」右侧。
        /// </summary>
        Link = 15,
    }


    /// <summary>
    ///     次要页面种类。其数值必须与 StackPanel 中的下标一致。
    /// </summary>
    public enum PageSubType
    {
        Default = 0,
        DownloadInstall = 1,
        DownloadMod = 2,
        DownloadPack = 3,
        DownloadDataPack = 4,
        DownloadResourcePack = 5,
        DownloadShader = 6,
        DownloadWorld = 7,
        DownloadCompFavorites = 8,
        DownloadClient = 9,
        DownloadOptiFine = 10,
        DownloadForge = 11,
        DownloadNeoForge = 12,
        DownloadCleanroom = 13,
        DownloadFabric = 14,
        DownloadLiteLoader = 16,

        /// <summary>
        ///     Lunar Client 的启动页。只在 <see cref="PageType.Lunar" /> 下使用，
        ///     与下载页的下标空间互不干扰（和基岩版复用 0 / 1 是同一个道理）。
        /// </summary>
        LunarLaunch = 17,
        DownloadLabyMod = 17,
        DownloadLegacyFabric = 18,

        SetupLaunch = 0,
        SetupUI = 1,
        SetupGameManage = 2,
        SetupLink = 3,
        SetupAbout = 4,
        SetupLog = 5,
        SetupFeedback = 6,
        SetupUpdate = 8,
        SetupJava = 9,
        SetupLauncherMisc = 10,
        SetupLauncherLanguage = 11,

        PluginMarket = 20,
        PluginInstalled = 21,

        /// <summary>
        ///     Lunar Client 的启动设置（Java、内存、JVM 参数）。
        /// </summary>
        SetupLunar = 12,

        ToolsTest = 3,

        /// <summary>
        ///     百宝箱：导入 PCL2 / PCLCE 的设置。
        /// </summary>
        ToolsImport = 4,

        /// <summary>
        ///     IPv6 离线邀请码直连联机（房主 / 房客）。
        /// </summary>
        LinkHost = 30,
        LinkGuest = 31,

        BedrockDownload = 0,
        BedrockLaunch = 1,

        VersionOverall = 0,
        VersionSetup = 1,
        VersionExport = 2,
        VersionWorld = 3,
        VersionScreenshot = 4,
        VersionMod = 5,
        VersionModDisabled = 6,
        VersionResourcePack = 7,
        VersionShader = 8,
        VersionSchematic = 9,
        VersionInstall = 10,
        VersionServer = 11,
        VersionModJarInJar = 12,
        VersionSavesInfo = 0,
        VersionSavesDatapack = 1
    }


    /// <summary>
    ///     获取次级页面的名称。若并非次级页面则返回空字符串，故可以以此判断是否为次级页面。
    /// </summary>
    private string PageNameGet(PageStackData stack)
    {
        switch (stack.page)
        {
            case PageType.InstanceSelect:
            {
                return Lang.Text("Main.Title.InstanceSelect");
            }
            case PageType.TaskManager:
            {
                return Lang.Text("Main.Title.TaskManager");
            }
            case PageType.GameLog:
            {
                return Lang.Text("Main.Title.GameLog");
            }
            case PageType.InstanceSetup:
            {
                return Lang.Text("Main.Title.InstanceSetup", PageInstanceLeft.McInstance is null ? Lang.Text("Common.State.Unknown") : PageInstanceLeft.McInstance.Name);
            }
            case PageType.CompDetail:
            {
                return Lang.Text("Main.Title.ResourceDownload", stack.additional.Value.CompProject.TranslatedName);
            }
            case PageType.HelpDetail:
            {
                return stack.helpPage?.Title ?? "";
            }
            case PageType.VersionSaves:
            {
                return Lang.Text("Main.Title.SaveManagement", ModBase.GetFolderNameFromPath(stack.additional.Value.SavePath));
            }

            default:
            {
                return "";
            }
        }
    }

}
