using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.Utils;
using PCL.Network;

namespace PCL;

public partial class PageLaunchLeft
{
    public interface ILoginPage { void Reload(); }


    private enum LaunchButtonAction
    {
        Loading,
        Launch,
        Download,
        Disabled
    }


    private static string StageWaitWindow => Lang.Text("Minecraft.Launch.Stage.WaitWindow");

    private static string StageEnd => Lang.Text("Minecraft.Launch.Stage.End");

    private static string StageRoot => Lang.Text("Minecraft.Launch.Stage.Root");

    private static void OnLanguageChanged(PageLaunchLeft page) => ModBase.RunInUi(page.RefreshButtonsUI);


    public void PageLaunchLeft_Loaded(object sender, RoutedEventArgs e)
    {
        if (isLoad)
            RefreshPage(false);

        AprilPosTrans.X = 0d;
        AprilPosTrans.Y = 0d;

        if (isLoad)
            return;
        isLoad = true;
        ModAnimation.AniControlEnabled += 1;

        // 开始按钮
        ModInstanceList.mcInstanceListLoader.LoadingStateChanged += (_, _) => RefreshButtonsUI();
        ModFolder.mcFolderListLoader.LoadingStateChanged += (_, _) => RefreshButtonsUI();
        RefreshButtonsUI();

        // 初始化档案
        ProfileService.Load();
        if (ProfileService.LastUsedProfile >= 0 && ProfileService.LastUsedProfile < ProfileService.Profiles.Count)
            ProfileService.SelectAt(ProfileService.LastUsedProfile);

        // 加载实例
        ModBase.RunInNewThread(() =>
        {
            // 自动整合包安装：准备
            string packInstallPath = null;
            if (File.Exists(Path.Combine(ModBase.exePath, "modpack.zip")))
                packInstallPath = Path.Combine(ModBase.exePath, "modpack.zip");
            if (File.Exists(Path.Combine(ModBase.exePath, "modpack.mrpack")))
                packInstallPath = Path.Combine(ModBase.exePath, "modpack.mrpack");
            if (packInstallPath is not null && !RestrictedFeaturePolicy.Check("auto-modpack-install"))
                packInstallPath = null;
            if (packInstallPath is not null)
            {
                ModBase.Log("[Launch] 需自动安装整合包：" + packInstallPath, ModBase.LogLevel.Debug);
                States.Game.SelectedFolder = @"$.minecraft\";
                if (!Directory.Exists(ModBase.exePath + @".minecraft\"))
                {
                    Directory.CreateDirectory(ModBase.exePath + @".minecraft\");
                    Directory.CreateDirectory(ModBase.exePath + @".minecraft\versions\");
                    ModFolder.McFolderLauncherProfilesJsonCreate(ModBase.exePath + @".minecraft\");
                }

                PageSelectLeft.AddFolder(ModBase.exePath + @".minecraft\",
                    ModBase.GetFolderNameFromPath(ModBase.exePath), false);
                ModFolder.mcFolderListLoader.WaitForExit();
            }

            // 确认 Minecraft 文件夹存在
            ModFolder.mcFolderSelected =
                States.Game.SelectedFolder.ToString().Replace("$", ModBase.exePath);
            if (string.IsNullOrEmpty(ModFolder.mcFolderSelected) || !Directory.Exists(ModFolder.mcFolderSelected))
            {
                // 无效的文件夹
                if (string.IsNullOrEmpty(ModFolder.mcFolderSelected))
                    ModBase.Log("[Launch] 没有已储存的 Minecraft 文件夹");
                else
                    ModBase.Log("[Launch] Minecraft 文件夹无效，该文件夹已不存在：" + ModFolder.mcFolderSelected,
                        ModBase.LogLevel.Debug);
                ModFolder.mcFolderListLoader.WaitForExit(isForceRestart: true);
                States.Game.SelectedFolder = ModFolder.mcFolderList[0].Location.Replace(ModBase.exePath, "$");
            }

            ModBase.Log("[Launch] Minecraft 文件夹：" + ModFolder.mcFolderSelected);
            if (Config.Debug.AddRandomDelay)
                Thread.Sleep(RandomUtils.NextInt(500, 3000));
            // 自动整合包安装
            if (packInstallPath is not null)
                try
                {
                    var installLoader = ModModpack.ModpackInstall(packInstallPath);
                    ModBase.Log("[Launch] 自动安装整合包已开始：" + packInstallPath);
                    installLoader.WaitForExit();
                    if (installLoader.State == ModBase.LoadState.Finished)
                    {
                        ModBase.Log("[Launch] 自动安装整合包成功，清理安装包：" + packInstallPath);
                        if (File.Exists(packInstallPath))
                            File.Delete(packInstallPath);
                    }
                }
                catch (ModBase.CancelledException ex)
                {
                    ModBase.Log(ex, "自动安装整合包被用户取消：" + packInstallPath);
                }
                catch (Exception ex)
                {
                    ModBase.Log(
                        ex,
                        Lang.Text("Select.Folder.Error.InstallPack", packInstallPath),
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Select.Folder.Error.InstallPack", packInstallPath));
                }

            // 确认 Minecraft 版本实例
            var selection = States.Game.SelectedInstance;
            var instance = selection == "" ? null : new McInstance(selection);
            if (instance is null || !instance.PathInstance.StartsWithF(ModFolder.mcFolderSelected) ||
                !instance.Check())
            {
                // 无效的实例
                ModBase.Log("[Launch] 当前选择的 Minecraft 实例无效：" + (instance is null ? "null" : instance.PathInstance),
                    instance is null ? ModBase.LogLevel.Normal : ModBase.LogLevel.Debug);
                if (ModInstanceList.mcInstanceListLoader.State != ModBase.LoadState.Finished)
                    ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                        ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\", true);
                if (ModInstanceList.mcInstanceList.Count == 0 ||
                    ModInstanceList.mcInstanceList.First().Value[0].Logo.Contains("RedstoneBlock"))
                {
                    instance = null;
                    States.Game.SelectedInstance = "";
                    ModBase.Log("[Launch] 无可用 Minecraft 实例");
                }
                else
                {
                    instance = ModInstanceList.mcInstanceList.First().Value[0];
                    States.Game.SelectedInstance = instance.Name;
                    ModBase.Log("[Launch] 自动选择 Minecraft 实例：" + instance.PathInstance);
                }
            }

            ModBase.RunInUi(() =>
            {
                ModInstanceList.McMcInstanceSelected = instance; // 绕这一圈是为了避免 McInstanceCheck 触发第二次实例改变
                isLoadFinished = true;
                RefreshButtonsUI();
                RefreshPage(false); // 有可能选择的版本变化了，需要重新刷新
                // If IsProfileVaild() = "" Then McLoginLoader.Start() '自动登录
            });
        }, "Instance Check", ThreadPriority.AboveNormal);

        // 改变页面
        RefreshPage(false);

        ModAnimation.AniControlEnabled -= 1;
    }


    // 实例选择按钮
    private void BtnInstance_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading)
            return;
        ModMain.frmMain.PageChange(FormMain.PageType.InstanceSelect);
    }


    // 启动按钮
    public void LaunchButtonClick()
    {
        if (ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading || !BtnLaunch.IsEnabled ||
            (ModMain.frmMain.pageRight is not null &&
             ModMain.frmMain.pageRight.PageState != MyPageRight.PageStates.ContentStay &&
             ModMain.frmMain.pageRight.PageState != MyPageRight.PageStates.ContentEnter))
            return;
        // 愚人节处理
        if (ModMain.isAprilEnabled && !ModMain.isAprilGiveup)
        {
            ModMain.isAprilGiveup = true;
            ModMain.frmLaunchLeft.AprilScaleTrans.ScaleX = 1d;
            ModMain.frmLaunchLeft.AprilScaleTrans.ScaleY = 1d;
            ModMain.frmLaunchLeft.AprilPosTrans.X = 0d;
            ModMain.frmLaunchLeft.AprilPosTrans.Y = 0d;
            ModMain.frmMain.BtnExtraApril.ShowRefresh();
        }

        // 实际的启动
        switch (_launchButtonAction)
        {
            case LaunchButtonAction.Launch:
            {
                if (File.Exists(ModInstanceList.McMcInstanceSelected.PathInstance + ".pclignore"))
                {
                    HintService.Hint(Lang.Text("Launch.Home.Instance.InstallingCannotLaunch"), HintType.Error);
                    return;
                }

                ModLaunch.McLaunchStart();
                break;
            }
            case LaunchButtonAction.Download:
            {
                ModMain.frmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall);
                break;
            }
        }
    }


    public void RefreshButtonsUI()
    {
        if (!BtnLaunch.IsLoaded)
            return;
        // 获取当前状态
        int currentState;
        if (!isLoadFinished || ModInstanceList.mcInstanceListLoader.State == ModBase.LoadState.Loading ||
            ModFolder.mcFolderListLoader.State == ModBase.LoadState.Loading)
        {
            currentState = 0;
        }
        else if (ModInstanceList.McMcInstanceSelected is null)
        {
            if (Config.Preference.Hide.PageDownload && !PageSetupUI.HiddenForceShow)
                currentState = 1;
            else
                currentState = 2;
        }
        else
        {
            currentState = 3;
        }

        // 更新状态。
        var currentLanguage = LocalizationService.CurrentLanguage.Code;
        if (currentState == btnLaunchState &&
            currentLanguage == _btnLaunchLanguage &&
            ((ModInstanceList.McMcInstanceSelected is null ? "" : ModInstanceList.McMcInstanceSelected.PathInstance) ?? "") ==
            ((btnLaunchVersion is null ? "" : btnLaunchVersion.PathInstance) ?? ""))
            goto ExitRefresh;
        _btnLaunchLanguage = currentLanguage;
        btnLaunchVersion = ModInstanceList.McMcInstanceSelected;
        btnLaunchState = currentState;
        switch (currentState)
        {
            case 0:
            {
                _launchButtonAction = LaunchButtonAction.Loading;
                ModBase.Log("[Minecraft] 启动按钮：正在加载 Minecraft 实例");
                ModMain.frmLaunchLeft.BtnLaunch.Text = Lang.Text("Launch.Home.Button.Loading");
                ModMain.frmLaunchLeft.BtnLaunch.IsEnabled = false;
                ModMain.frmLaunchLeft.LabVersion.Text = Lang.Text("Launch.Home.Instance.Loading");
                ModMain.frmLaunchLeft.BtnInstance.IsEnabled = false;
                ModMain.frmLaunchLeft.BtnMore.Visibility = Visibility.Collapsed;
                break;
            }
            case 1:
            {
                _launchButtonAction = LaunchButtonAction.Disabled;
                ModBase.Log("[Minecraft] 启动按钮：无 Minecraft 实例，下载已禁用");
                ModMain.frmLaunchLeft.BtnLaunch.Text = Lang.Text("Launch.Home.Button.Launch");
                ModMain.frmLaunchLeft.BtnLaunch.IsEnabled = false;
                ModMain.frmLaunchLeft.LabVersion.Text = Lang.Text("Launch.Home.Instance.NotFound");
                ModMain.frmLaunchLeft.BtnInstance.IsEnabled = true;
                ModMain.frmLaunchLeft.BtnMore.Visibility = Visibility.Collapsed;
                break;
            }
            case 2:
            {
                _launchButtonAction = LaunchButtonAction.Download;
                ModBase.Log("[Minecraft] 启动按钮：无 Minecraft 实例，要求下载");
                ModMain.frmLaunchLeft.BtnLaunch.Text = Lang.Text("Launch.Home.Button.Download");
                ModMain.frmLaunchLeft.BtnLaunch.IsEnabled = true;
                ModMain.frmLaunchLeft.LabVersion.Text = Lang.Text("Launch.Home.Instance.NotFound");
                ModMain.frmLaunchLeft.BtnInstance.IsEnabled = true;
                ModMain.frmLaunchLeft.BtnMore.Visibility = Visibility.Collapsed;
                break;
            }
            case 3:
            {
                _launchButtonAction = LaunchButtonAction.Launch;
                ModBase.Log("[Minecraft] 启动按钮：Minecraft 实例：" + ModInstanceList.McMcInstanceSelected.PathInstance);
                ModMain.frmLaunchLeft.BtnLaunch.Text = Lang.Text("Launch.Home.Button.Launch");
                ModMain.frmLaunchLeft.BtnInstance.IsEnabled = true;
                if (ProfileService.Current is not null)
                    BtnLaunch.IsEnabled = true;
                else
                    BtnLaunch.IsEnabled = false;
                ModMain.frmLaunchLeft.LabVersion.Text = ModInstanceList.McMcInstanceSelected.Name;
                break;
            }
            // FrmLaunchLeft.BtnMore.Visibility = Visibility.Visible '由功能隐藏设置修改
        }

        ExitRefresh: ;

        // 功能隐藏
        ModMain.frmLaunchLeft.BtnInstance.Visibility =
            !PageSetupUI.HiddenForceShow && Config.Preference.Hide.FunctionSelect
                ? Visibility.Collapsed
                : Visibility.Visible;
        if (currentState == 3) ModMain.frmLaunchLeft.BtnMore.Visibility = ModMain.frmLaunchLeft.BtnInstance.Visibility;
    }


    // 取消按钮
    private void BtnCancel_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModLaunch.mcLaunchLoaderReal is not null)
        {
            ModLaunch.mcLaunchLoaderReal.Abort();
            ModLaunch.McLaunchLog("已取消启动");
            try
            {
                if (ModLaunch.mcLaunchWatcher is not null)
                    ModLaunch.mcLaunchWatcher.Kill();
                else if (ModLaunch.mcLaunchProcess is not null)
                    if (!ModLaunch.mcLaunchProcess.HasExited)
                        ModLaunch.mcLaunchProcess.Kill();
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    Lang.Text("Minecraft.Launch.Error.CancelProcess"),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Minecraft.Launch.Error.CancelProcess"));
            }
        }
    }


    // 实例设置按钮
    private void BtnMore_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading)
            return;
        ModInstanceList.McMcInstanceSelected.Load();
        PageInstanceLeft.McInstance = ModInstanceList.McMcInstanceSelected;
        if (File.Exists(ModInstanceList.McMcInstanceSelected.PathInstance + ".pclignore"))
        {
            HintService.Hint(Lang.Text("Launch.Home.Instance.InstallingCannotSetup"), HintType.Error);
            return;
        }

        ModMain.frmMain.PageChange(FormMain.PageType.InstanceSetup);
    }


    /// <summary>
    ///     每 0.2s 执行一次，刷新启动的数据 UI 显示。
    /// </summary>
    public void LaunchingRefresh()
    {
        try
        {
            if (ModLaunch.mcLaunchLoaderReal.State == ModBase.LoadState.Aborted)
                return;
            // 阶段状态获取
            var isLaunched = false; // 是否已经启动游戏，只是在等待窗口
            do
            {
                try
                {
                    var exitTry = false;
                    foreach (var Loader in ModLaunch.mcLaunchLoaderReal.GetLoaderList(false))
                        if (Loader.State == ModBase.LoadState.Loading || Loader.State == ModBase.LoadState.Waiting)
                        {
                            LabLaunchingStage.Text = Loader.name;
                            isLaunched = Loader.name == StageWaitWindow || Loader.name == StageEnd;
                            exitTry = true;
                            break;
                        }

                    if (exitTry) break;
                    LabLaunchingStage.Text = Lang.Text("Launch.Status.Completed");
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "获取是否启动完成失败，可能是由于启动状态改变导致集合已修改");
                    return;
                }
            } while (false);

            if (ModAnimation.AniIsRun("Launch State Page"))
                isLaunched = false; // 等待页面切换动画完成
            // 计算应显示的进度
            var actualProgress = ModLaunch.mcLaunchLoaderReal.Progress;
            if (actualProgress >= showProgress)
                showProgress += (actualProgress - showProgress) * 0.2d + 0.005d; // 向实际进度靠一点
            if (actualProgress <= showProgress)
                showProgress = actualProgress; // 原来或处理后变得比实际进度高，直接回退
            if (isLaunched)
                showProgress = 1d; // 如果已经完成了，就不卖关子了
            // 文本
            LabLaunchingTitle.Text = isLaunched ? Lang.Text("Launch.Status.Title.Launched") :
                ModLaunch.currentLaunchOptions.SaveBatch is null ? Lang.Text("Launch.Status.Title.Launching") : Lang.Text("Launch.Status.Title.ExportingScript");
            LabLaunchingProgress.Text = Lang.Number(showProgress, "P2");
            var hasLaunchDownloader = false;
            try
            {
                foreach (var Loader in ModNet.NetManager.Tasks)
                    if (Loader.RealParent is not null && Loader.RealParent.name == StageRoot &&
                        Loader.State == ModBase.LoadState.Loading)
                        hasLaunchDownloader = true;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "获取 Minecraft 启动下载器失败，可能是因为启动被取消");
                hasLaunchDownloader = false;
            }

            LabLaunchingDownload.Text = ModBase.GetString(ModNet.NetManager.Speed) + "/s";
            var shouldShowHint = Config.Preference.ShowLaunchingHint;
            // 进度改变动画
            var animList = new List<ModAnimation.AniData>
            {
                ModAnimation.AaGridLengthWidth(ProgressLaunchingFinished,
                    showProgress - ProgressLaunchingFinished.Width.Value, 260,
                    ease: new ModAnimation.AniEaseOutFluent()),
                ModAnimation.AaGridLengthWidth(ProgressLaunchingUnfinished,
                    1d - showProgress - ProgressLaunchingUnfinished.Width.Value, 260,
                    ease: new ModAnimation.AniEaseOutFluent())
            };
            var isDownloadStateChanged =
                hasLaunchDownloader == (LabLaunchingDownload.Visibility == Visibility.Collapsed);
            if (isDownloadStateChanged)
            {
                LabLaunchingDownload.Visibility = Visibility.Visible;
                LabLaunchingDownloadLeft.Visibility = Visibility.Visible;
                animList.AddRange(new[]
                {
                    ModAnimation.AaOpacity(LabLaunchingDownload,
                        (hasLaunchDownloader ? 1 : 0) - LabLaunchingDownload.Opacity, 100),
                    ModAnimation.AaOpacity(LabLaunchingDownloadLeft,
                        (hasLaunchDownloader ? 0.5d : 0d) - LabLaunchingDownloadLeft.Opacity, 100),
                    ModAnimation.AaCode(() =>
                    {
                        if (!hasLaunchDownloader)
                        {
                            LabLaunchingDownload.Visibility = Visibility.Collapsed;
                            LabLaunchingDownloadLeft.Visibility = Visibility.Collapsed;
                        }
                    }, 110)
                });
            }

            var isProgressStateChanged = !isLaunched == (LabLaunchingProgress.Visibility == Visibility.Collapsed);
            if (isProgressStateChanged)
            {
                LabLaunchingProgress.Visibility = Visibility.Visible;
                LabLaunchingProgressLeft.Visibility = Visibility.Visible;
                if (isLaunched && shouldShowHint) PanLaunchingHint.Visibility = Visibility.Visible;
                animList.AddRange(new[]
                {
                    ModAnimation.AaOpacity(LabLaunchingProgress, (!isLaunched ? 1 : 0) - LabLaunchingProgress.Opacity,
                        100),
                    ModAnimation.AaOpacity(LabLaunchingProgressLeft,
                        (!isLaunched ? 0.5d : 0d) - LabLaunchingProgressLeft.Opacity, 100),
                    ModAnimation.AaOpacity(PanLaunchingHint,
                        (isLaunched && shouldShowHint ? 1 : 0) - PanLaunchingHint.Opacity, 100)
                });
            }

            ModAnimation.AniStart(animList, "Launching Progress");
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Minecraft.Launch.Error.RefreshInfo"),
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Launch.Error.RefreshInfo"));
        }
    }


    private void PanLaunchingInfo_SizeChangedW(object sender, SizeChangedEventArgs e)
    {
        var deltaWidth = e.NewSize.Width - e.PreviousSize.Width;
        if (e.PreviousSize.Width == 0d || isWidthAnimating || Math.Abs(deltaWidth) < 1d ||
            PanLaunchingInfo.ActualWidth == 0d)
            return;
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaWidth(PanLaunchingInfo, deltaWidth, 180, ease: new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaCode(() =>
            {
                isWidthAnimating = false;
                PanLaunchingInfo.Width = actualUsedWidth;
            }, after: true)
        }, "Launching Info Width");
        isWidthAnimating = true;
        actualUsedWidth = PanLaunchingInfo.Width;
        PanLaunchingInfo.Width = e.PreviousSize.Width;
    }


    private void PanLaunchingInfo_SizeChangedH(object sender, SizeChangedEventArgs e)
    {
        var deltaHeight = e.NewSize.Height - e.PreviousSize.Height;
        if (e.PreviousSize.Height == 0d || isHeightAnimating || Math.Abs(deltaHeight) < 1d ||
            PanLaunchingInfo.ActualHeight == 0d)
            return;
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaHeight(PanLaunchingInfo, deltaHeight, 180, ease: new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaCode(() =>
            {
                isHeightAnimating = false;
                PanLaunchingInfo.Height = actualUsedHeight;
            }, after: true)
        }, "Launching Info Height");
        isHeightAnimating = true;
        actualUsedHeight = PanLaunchingInfo.Height;
        PanLaunchingInfo.Height = e.PreviousSize.Height;
    }


    // 启动游戏按钮
    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        LaunchButtonClick();
    }

}
