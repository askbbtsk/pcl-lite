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
    // 愚人节鼠标位置
    public MouseEventArgs lastMouseArg;

    // 窗口加载
    private bool isWindowLoadFinished;
    private readonly DragHelper _helper = new();

    public FormMain()
    {
        ModBase.applicationStartTick = TimeUtils.GetTimeTick();
        // 刷新主题
        // ThemeCheckAll(False)
        // ThemeRefreshColor()
        ThemeService.ColorModeChanged += (_, _) => ThemeManager.ThemeRefresh();
        ThemeService.ColorThemeChanged += theme => ThemeManager.ThemeRefresh((int)theme);
        // 窗体参数初始化
        ModMain.frmMain = this;
        ModMain.frmLaunchLeft = new PageLaunchLeft();
        ModMain.frmLaunchRight = new PageLaunchRight();
        // 版本号改变
        var lastVersion = States.System.LastVersion;
        if (lastVersion < ModBase.versionCode)
        {
            // 触发升级
            UpgradeSub(lastVersion);
        }
        else if (lastVersion > ModBase.versionCode)
            // 触发降级
            DowngradeSub(lastVersion);

        _ = Config.Preference.Theme.ThemeSelected;
        // 注册拖拽事件（不能直接加 Handles，否则没用；#6340）
        AddHandler(DragDrop.DragEnterEvent, new DragEventHandler(HandleDrag), true);
        AddHandler(DragDrop.DragOverEvent, new DragEventHandler(HandleDrag), true);
        // 注册 MsgBox 事件
        MsgBoxWrapper.OnShow += ModMain.MsgBoxWrapper_OnShow;
        // 注册 Hint 事件
        HintWrapper.OnShow += HintService.HintWrapper_OnShow;
        // 加载 UI
        InitializeComponent();
        InitializeCommandPalette();
        PanBack.SizeChanged += FormMain_SizeChanged;
        RefreshNavigationOverflow();
        PanTitleMain.SizeChanged += PanTitleSelect_SizeChanged;
        Loaded += (_, _) => QueueNavigationOverflowRefresh();
        Opacity = 0d;
        try
        {
            Height = States.UI.WindowHeight;
            Width = States.UI.WindowWidth;
        }
        catch (Exception ex) // 修复 #2019
        {
            ModBase.Log(
                ex,
                "读取窗口默认大小失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
            Height = MinHeight + 100d;
            Width = MinWidth + 100d;
        }

        // 管理员权限下文件拖拽
        if (ProcessInterop.IsAdmin())
        {
            ModBase.Log("[Start] PCL 当前正以管理员权限运行");
            SourceInitialized += (_, _) =>
            {
                var windowInterop = new WindowInteropHelper(this);
                _helper.HwndSource = HwndSource.FromHwnd(windowInterop.Handle);
                _helper.AddHook();
            };
            Closing += (_, _) => _helper.RemoveHook();
            _helper.DragDrop += (_, _) => FileDrag(_helper.DropFilePaths);
        }

        if (ModMain.frmLaunchLeft.Parent is not null)
            ModMain.frmLaunchLeft.SetValue(ContentPresenter.ContentProperty, null);
        if (ModMain.frmLaunchRight.Parent is not null)
            ModMain.frmLaunchRight.SetValue(ContentPresenter.ContentProperty, null);
        PanMainLeft.Child = ModMain.frmLaunchLeft;
        pageLeft = ModMain.frmLaunchLeft;
        PanMainRight.Child = ModMain.frmLaunchRight;
        pageRight = ModMain.frmLaunchRight;
        ModMain.frmLaunchRight.PageState = MyPageRight.PageStates.ContentStay;
        // 调试模式提醒
        if (ModBase.modeDebug)
            HintService.Hint(Lang.Text("Main.DebugMode.Hint"));
        // 尽早执行的加载池
        ModFolder.mcFolderListLoader
            .Start(0); // 为了让下载已存在文件检测可以正常运行，必须跑一次；为了让启动按钮尽快可用，需要尽早执行；为了与 PageLaunchLeft 联动，需要为 0 而不是 GetUuid

        ModBase.Log("[Start] 第二阶段加载用时：" + (TimeUtils.GetTimeTick() - ModBase.applicationStartTick) + " ms");
        // 注册生命周期状态事件
        Lifecycle.When(LifecycleState.WindowCreated, FormMain_Loaded);
    }

    private void FormMain_Loaded() // (sender As Object, e As RoutedEventArgs) Handles Me.Loaded
    {
        FormMain_SizeChanged();
        ModBase.applicationStartTick = TimeUtils.GetTimeTick();
        ModBase.frmHandle = new WindowInteropHelper(this).Handle;
        // 读取设置
        PageSetupUI.BackgroundRefresh(false, true);
        ModMusic.MusicRefreshPlay(false, true);
        // 扩展按钮
        BtnExtraUpdateRestart.showCheck = BtnExtraUpdateRestart_ShowCheck;
        BtnExtraDownload.showCheck = BtnExtraDownload_ShowCheck;
        BtnExtraBack.showCheck = BtnExtraBack_ShowCheck;
        BtnExtraApril.showCheck = BtnExtraApril_ShowCheck;
        BtnExtraShutdown.showCheck = BtnExtraShutdown_ShowCheck;
        BtnExtraLog.showCheck = BtnExtraLog_ShowCheck;
        BtnExtraApril.ShowRefresh();
        // 初始化尺寸改变
        if (!Config.Preference.LockWindowSize)
            AddResizer();
        else
            RemoveResizer();
        // PLC 彩蛋
        if (RandomUtils.NextInt(1, 1000) == 233)
            ShapeTitleLogo.Data = (Geometry)new GeometryConverter().ConvertFromString(
                "M26,29 v-25 h6 a7,7 180 0 1 0,14 h-6 M83,6.5 a10,11.5 180 1 0 0,18 M48,2.5 v24.5 h13.5");
        // 加载窗口

        ThemeManager.ThemeRefresh();
        ModSetup.ApplyAll();
        Lifecycle.CurrentApplication.Resources["BlurSamplingRate"] = Config.Preference.Blur.SamplingRate * 0.01d;
        Lifecycle.CurrentApplication.Resources["BlurType"] = Config.Preference.Blur.KernelType;
        if (Config.Preference.Blur.IsEnabled && !Config.Preference.TahoeGlass.IsEnabled)
            Lifecycle.CurrentApplication.Resources["BlurRadius"] = Config.Preference.Blur.Radius * 1.0d;
        else
            Lifecycle.CurrentApplication.Resources["BlurRadius"] = 0.0d;

        // #If DEBUG Then
        // MinHeight = 50
        // MinWidth = 50
        // #End If
        Topmost = false;
        if (ModMain.frmStart is not null)
            ModMain.frmStart.Close(new TimeSpan(0, 0, 0, 0, (int)Math.Round(400d / ModAnimation.aniSpeed)));
        // 更改窗口
        // Top = (GetWPFSize(My.Computer.Screen.WorkingArea.Height) - Height) / 2
        // Left = (GetWPFSize(My.Computer.Screen.WorkingArea.Width) - Width) / 2
        ShowWindowToTop();
        var hwndSource = (HwndSource)PresentationSource.FromVisual(this);
        hwndSource.AddHook(WndProc);
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() => ModAnimation.AniControlEnabled -= 1, 50),
            ModAnimation.AaOpacity(this,
                PCL.Core.UI.LiquidGlassComposition.WindowOpacity(Config.Preference.TahoeGlass.IsEnabled,
                    Config.Preference.Theme.WindowOpacity) - Opacity, 250, 100),
            ModAnimation.AaDouble(i => TransformPos.Y += (double)i, -TransformPos.Y, 600,
                100, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaDouble(i => TransformRotate.Angle += (double)i,
                -TransformRotate.Angle, 500, 100, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() =>
            {
                RenderTransform = null;
                isWindowLoadFinished = true;
                ModBase.Log(
                    $"[System] DPI：{ModBase.dpi}，系统版本：{Environment.OSVersion.VersionString}，PCL 位置：{Basics.ExecutablePath}");
            }, after: true)
        }, "Form Show");
        // Timer 启动
        ModAnimation.AniStart();
        ModMain.TimerMainStart();
        // 特殊版本提示
        ModBase.RunInNewThread(() =>
        {
            // 特殊版本提示
            try
            {


#if DEBUG || DEBUGCI

                if (Environment.GetEnvironmentVariable("PCL_DISABLE_DEBUG_HINT") is null)
                {

#if DEBUG
                    var hint = Lang.Text("Main.SpecialVersion.DebugHint");
#else
                    var hint = Lang.Text("Main.SpecialVersion.CiHint");
#endif

                    ModMain.MyMsgBox(
                        $"{hint}{"\r\n"}{"\r\n"}{Lang.Text("Main.SpecialVersion.HideHintNotice")}",
                        Lang.Text("Main.SpecialVersion.Title"), Lang.Text("Main.SpecialVersion.IUnderstand"), Lang.Text("Main.SpecialVersion.OpenDownloadPageAndExit"), isWarn: true, button2Action: () =>
                        {
                            ModBase.OpenWebsite(ProjectLinks.LatestRelease);
                            EndProgram(false);
                        });
                }


#endif
                // EULA 提示
                if (!States.System.LauncherEula)
                    switch (ModMain.MyMsgBox(Lang.Text("Main.Eula.Message"), Lang.Text("Main.Eula.Title"), Lang.Text("Common.Action.Agree"), Lang.Text("Common.Action.Decline"), Lang.Text("Main.Eula.View"),
                                button3Action: () => ModBase.OpenWebsite("https://shimo.im/docs/rGrd8pY8xWkt6ryW")))
                    {
                        case 1:
                            {
                                States.System.LauncherEula = true;
                                break;
                            }
                        case 2:
                            {
                                EndProgram(false);
                                break;
                            }
                    }

                // 启动加载器池
                try
                {
                    ModDownload.dlClientListMojangLoader.Start(1); // PCL 会同时根据这里的加载结果决定是否使用官方源进行下载
                    RunCountSub();
                    UpdateManager.serverLoader.Start(1);
                    ModBase.RunInNewThread(ModMain.TryClearTaskTemp, "TryClearTaskTemp", ThreadPriority.BelowNormal);
                }
                catch (Exception ex)
                {
                    ModBase.Log(
                        ex,
                        "初始化加载池运行失败",
                        ModBase.LogLevel.Feedback,
                        userSummary: Lang.Text("Main.Error.OperationFailed"));
                }

                HardwareInfo.GetHardwareInfo();
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "初始弹窗提示运行失败",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Main.Error.OperationFailed"));
            }
        }, "Start Loader", ThreadPriority.BelowNormal);

        ModBase.Log($"[Start] 第三阶段加载用时：{TimeUtils.GetTimeTick() - ModBase.applicationStartTick} ms");
    }

    // 升级与降级事件
    private void UpgradeSub(int lastVersionCode)
    {
        ModBase.Log("[Start] 版本号从 " + lastVersionCode + " 升高到 " + ModBase.versionCode);
        States.System.LastVersion = ModBase.versionCode;
        // 检查有记录的最高版本号
        int lowerVersionCode;
#if BETA
        lowerVersionCode = States.System.LastBetaVersion;
        if (lowerVersionCode < ModBase.versionCode)
        {
            States.System.LastBetaVersion = ModBase.versionCode;
            ModBase.Log($"[Start] 最高版本号从 {lowerVersionCode} 升高到 {ModBase.versionCode}");
        }
#else
        lowerVersionCode = States.System.LastAlphaVersion;
        if (lowerVersionCode < ModBase.versionCode)
        {
            States.System.LastAlphaVersion = ModBase.versionCode;
            ModBase.Log($"[Start] 最高版本号从 {lowerVersionCode} 升高到 {ModBase.versionCode}");
        }
#endif
        // 被移除的窗口设置选项 (Commit 3161488 2026/1/23)
        if ((int)Config.Launch.GameWindowMode == 5)
            Config.Launch.GameWindowMode = GameWindowSizeMode.Default;

        // 输出更新日志
        if (lastVersionCode <= 0)
            return;
        if (lowerVersionCode >= ModBase.versionCode)
            return;
        ShowUpdateLog();
        
        // 重置自定义主页配置
        if (lastVersionCode < 521 && Config.Preference.Homepage.SelectedPreset >= 3)
        {
            Config.Preference.Homepage.SelectedPreset = 0;
            ModMain.MyMsgBox(Lang.Text("Main.HomepageReset.Content"), Lang.Text("Main.HomepageReset.Title"));
        }
    }



    private bool canResize = true;

    private static bool isLogShown;

    private bool _navigationExpanded;
    private bool _navigationReflowQueued;
    private HashSet<int> _navigationAutoCollapsedIds = [];

    private IDataObject _HandleDrag_PrevData;
    private DragDropEffects _HandleDrag_PrevEffects;

    // 接受到 Windows 窗体事件
    public bool isSystemTimeChanged;

    // 页面状态存储
    /// <summary>
    ///     当前的主页面。
    /// </summary>
    public PageStackData pageCurrent = PageType.Launch;

    /// <summary>
    ///     上一个主页面。
    /// </summary>
    public PageStackData pageLast = PageType.Launch;

    /// <summary>
    ///     上层页面的编号堆栈，用于返回。
    /// </summary>
    public List<PageStackData> pageStack = new();

    public MyPageLeft pageLeft;
    public MyPageRight pageRight;

    // 引发实际页面切换的入口
    private bool isChangingPage;

}
