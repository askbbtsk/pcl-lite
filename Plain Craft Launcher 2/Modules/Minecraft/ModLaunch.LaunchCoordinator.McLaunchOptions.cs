using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Backups;
using PCL.Core.Minecraft.Worlds;
using PCL.Core.Minecraft.PersonalOptions;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.Launch.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils;
using PCL.Core.Utils.Secret;
using PCL.Network;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using System.Globalization;
using PCL.Core.Logging;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;

namespace PCL;

public static partial class ModLaunch
{

    public class McLaunchOptions
    {
        /// <summary>
        ///     额外的启动参数。
        /// </summary>
        public List<string> ExtraArgs = new();

        /// <summary>Additional JVM arguments inserted after the generated JVM arguments.</summary>
        public List<string> AdditionalJvmArguments = new();

        /// <summary>Additional environment variables supplied by an authorized integration.</summary>
        public Dictionary<string, string> Environment = new(StringComparer.Ordinal);

        /// <summary>
        ///     强行指定启动的 MC 实例。
        ///     默认值：Nothing。使用 McInstanceCurrent。
        /// </summary>
        public McInstance instance = null;

        /// <summary>
        ///     是否为 “测试游戏” 按钮启动的游戏。
        ///     如果是，则显示游戏实时日志。
        /// </summary>
        public bool IsTest = false;

        /// <summary>
        ///     将启动脚本保存到该地址，然后取消启动。这同时会改变启动时的提示等。
        ///     默认值：Nothing。不保存。
        /// </summary>
        public string SaveBatch = null;

        /// <summary>
        ///     强制指定在启动后进入的服务器 IP。
        ///     默认值：Nothing。使用实例设置的值。
        /// </summary>
        public string ServerIp = null;

        /// <summary>
        ///     指定在启动之后进入的存档名称。
        ///     默认值：Nothing。使用实例设置的值。
        /// </summary>
        public string WorldName = null;

        /// <summary>Optional explicit world directory when launching a selected save.</summary>
        public string? WorldPath;
    }


    /// <summary>
    ///     尝试启动 Minecraft。必须在 UI 线程调用。
    ///     返回是否实际开始了启动（如果没有，则一定弹出了错误提示）。
    /// </summary>
    public static bool McLaunchStart(McLaunchOptions options = null)
    {
        isLaunching = true;
        currentLaunchOptions = options ?? new McLaunchOptions();
        // 预检查
        if (!ModBase.RunInUi())
            throw new Exception("McLaunchStart 必须在 UI 线程调用！");
        if (mcLaunchLoader.State == ModBase.LoadState.Loading)
        {
            HintService.Hint(Lang.Text("Minecraft.Launch.Error.AlreadyLaunching"), HintType.Error);
            isLaunching = false;
            return false;
        }

        // 强制切换需要启动的实例
        if (currentLaunchOptions.instance is not null &&
            ModInstanceList.McMcInstanceSelected != currentLaunchOptions.instance)
        {
            McLaunchLog("在启动前切换到实例 " + currentLaunchOptions.instance.Name);
            // 检查实例
            currentLaunchOptions.instance.Load();
            if (currentLaunchOptions.instance.state == McInstanceState.Error)
            {
                HintService.Hint(Lang.Text("Minecraft.Launch.Error.CannotLaunch", currentLaunchOptions.instance.Desc), HintType.Error);
                isLaunching = false;
                return false;
            }

            // 切换实例
            ModInstanceList.McMcInstanceSelected = currentLaunchOptions.instance;
            States.Game.SelectedInstance = ModInstanceList.McMcInstanceSelected.Name;
            ModMain.frmLaunchLeft.RefreshButtonsUI();
            ModMain.frmLaunchLeft.RefreshPage(false);
        }

        ModMain.frmMain.AprilGiveup();
        var launchEventInstance = currentLaunchOptions.instance ?? ModInstanceList.McMcInstanceSelected;
        if (launchEventInstance is not null)
            PluginRuntimeManager.Notify(new PluginGameLaunchingEvent(launchEventInstance.PathInstance, launchEventInstance.Name));
        // 禁止进入实例选择页面（否则就可以在启动中切换 McInstanceCurrent 了）
        ModMain.frmMain.pageStack =
            ModMain.frmMain.pageStack.Where(p => p.page != FormMain.PageType.InstanceSelect).ToList();
        // 实际启动加载器
        mcLaunchLoader.Start(options, true);
        return true;
    }


    /// <summary>
    ///     记录启动日志。
    /// </summary>
    public static void McLaunchLog(string text)
    {
        text = McLogFilter.FilterAccessToken(text, '*');
        // 用户文件夹名默认打码（个性化里可关）：启动日志里全是 C:\Users\xxx\... 这种路径
        text = UserPathMasker.Apply(text);
        ModBase.RunInUi(() =>
            ModMain.frmLaunchRight.LabLog.Text += "\r\n" + "[" + TimeUtils.GetTimeNow() + "] " + text);
        ModBase.Log("[Launch] " + text);
    }


    private static void McLaunchState(ModLoader.LoaderTask<McLaunchOptions, object> loader)
    {
        switch (mcLaunchLoader.State)
        {
            case ModBase.LoadState.Finished:
            case ModBase.LoadState.Failed:
            case ModBase.LoadState.Waiting:
            case ModBase.LoadState.Aborted:
            {
                ModMain.frmLaunchLeft.PageChangeToLogin();
                break;
            }
            case ModBase.LoadState.Loading:
            {
                // 在预检测结束后再触发动画
                ModMain.frmLaunchRight.LabLog.Text = "";
                break;
            }
        }
    }


    // 实际的启动方法
    private static void McLaunchStart(ModLoader.LoaderTask<McLaunchOptions, object> loader)
    {
        // 开始动画
        ModBase.RunInUiWait(ModMain.frmLaunchLeft.PageChangeToLaunching);
        // 预检测（预检测的错误将直接抛出）
        try
        {
            McLaunchPrecheck();
            if (!ModLaunchDependencyPrecheck.Confirm(ModInstanceList.McMcInstanceSelected, currentLaunchOptions?.SaveBatch is not null))
                throw new ModBase.CancelledException();
            McLaunchLog("预检测已通过");
        }
        catch (ModBase.CancelledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!ex.Message.StartsWithF("$$"))
                HintService.Hint(Lang.Text("Minecraft.Launch.Precheck.Failed.WithDetail", ex.Message), HintType.Error);
            throw;
        }

        // 正式加载
        try
        {
            // 构造主加载器
            var skipClientDownloads = RestrictedFeaturePolicy.IsRestricted;
            if (skipClientDownloads && !ModDownload.McClientFilesAvailableLocally(ModInstanceList.McMcInstanceSelected))
            {
                RestrictedFeaturePolicy.Check("minecraft-launch-missing-files");
                throw new ModBase.CancelledException();
            }

            var loaders = new List<ModLoader.LoaderBase>
            {
                new ModLoader.LoaderTask<int, int>(Lang.Text("Minecraft.Launch.Stage.GetJava"), McLaunchJava) { ProgressWeight = 4d, block = false },
                mcLoginLoader,
                new ModLoader.LoaderCombo<string>(Lang.Text("Minecraft.Launch.Stage.CompleteFiles"),
                        skipClientDownloads
                            ? new List<ModLoader.LoaderBase>()
                            : ModDownload.DlClientFix(ModInstanceList.McMcInstanceSelected, false,
                                ModDownload.AssetsIndexExistsBehaviour.DownloadInBackground))
                    { ProgressWeight = 15d, show = false },
                new ModLoader.LoaderTask<string, List<ModLibrary.McLibToken>>(Lang.Text("Minecraft.Launch.Stage.GetArguments"), McLaunchArgumentMain)
                    { ProgressWeight = 2d },
                new ModLoader.LoaderTask<List<ModLibrary.McLibToken>, int>(Lang.Text("Minecraft.Launch.Stage.ExtractNatives"), McLaunchNatives)
                    { ProgressWeight = 2d },
                new ModLoader.LoaderTask<int, int>(Lang.Text("Minecraft.Launch.Stage.PreLaunch"), _ => McLaunchPrerun()) { ProgressWeight = 1d },
                new ModLoader.LoaderTask<int, int>(Lang.Text("Minecraft.Launch.Stage.CustomCommand"), McLaunchCustom) { ProgressWeight = 1d },
                new ModLoader.LoaderTask<int, Process>(Lang.Text("Minecraft.Launch.Stage.StartProcess"), McLaunchRun) { ProgressWeight = 2d },
                new ModLoader.LoaderTask<Process, int>(Lang.Text("Minecraft.Launch.Stage.WaitWindow"), McLaunchWait) { ProgressWeight = 1d },
                new ModLoader.LoaderTask<int, int>(Lang.Text("Minecraft.Launch.Stage.End"), _ => McLaunchEnd()) { ProgressWeight = 1d }
            }; // .ProgressWeight = 15, .Block = False

            var launchLoader = new ModLoader.LoaderCombo<object>(Lang.Text("Minecraft.Launch.Stage.Root"), loaders) { show = false };
            if (mcLoginLoader.State == ModBase.LoadState.Finished)
                mcLoginLoader.State = ModBase.LoadState.Waiting; // 要求重启登录主加载器，它会自行决定是否启动副加载器
            // 等待加载器执行并更新 UI
            mcLaunchLoaderReal = launchLoader;
            abortHint = null;
            launchLoader.Start();
            // 任务栏进度条
            ModLoader.LoaderTaskbarAdd(launchLoader);
            while (launchLoader.State == ModBase.LoadState.Loading)
            {
                ModMain.frmLaunchLeft.Dispatcher.Invoke(ModMain.frmLaunchLeft.LaunchingRefresh);
                Thread.Sleep(100);
            }

            ModMain.frmLaunchLeft.Dispatcher.Invoke(ModMain.frmLaunchLeft.LaunchingRefresh);
            // 成功与失败处理
            switch (launchLoader.State)
            {
                case ModBase.LoadState.Finished:
                {
                    HintService.Hint(Lang.Text("Minecraft.Launch.Success", ModInstanceList.McMcInstanceSelected.Name), HintType.Success);
                    break;
                }
                case ModBase.LoadState.Aborted:
                {
                    if (abortHint is null)
                        HintService.Hint(currentLaunchOptions?.SaveBatch is null ? Lang.Text("Minecraft.Launch.Cancelled") : Lang.Text("Minecraft.Launch.ExportScript.Cancelled"));
                    else
                        HintService.Hint(abortHint, HintType.Success);

                    break;
                }
                case ModBase.LoadState.Failed:
                {
                    throw launchLoader.Error;
                }

                default:
                {
                    throw new Exception(Lang.Text("Minecraft.Launch.Error.InvalidState", ModBase.GetStringFromEnum(launchLoader.State)));
                }
            }

            isLaunching = false;
        }
        catch (Exception ex)
        {
            var currentEx = ex;
            while (currentEx is not null)
            {
                if (currentEx.Message.StartsWithF("$"))
                {
                    // 若有以 $ 开头的错误信息，则以此为准显示提示
                    // 若错误信息为 $$，则不提示
                    if (currentEx.Message != "$$")
                        ModMain.MyMsgBox(
                            Lang.Text("Minecraft.Launch.Error.SpecialMessage.WithDetail",
                                currentEx.Message.TrimStart('$')),
                            currentLaunchOptions?.SaveBatch is null
                                ? Lang.Text("Launch.Error.Title")
                                : Lang.Text("Launch.Error.ExportScriptTitle"));
                    throw;
                }

                if (currentEx.InnerException is null)
                    break;

                // 检查下一级错误
                currentEx = currentEx.InnerException;
            }

            // 没有特殊处理过的错误信息
            McLaunchLog("错误：" + ex);
            ModBase.Log(
                ex,
                currentLaunchOptions?.SaveBatch is null
                    ? "Minecraft launch failed"
                    : "Export script failed",
                ModBase.LogLevel.Msgbox,
                currentLaunchOptions?.SaveBatch is null
                    ? Lang.Text("Launch.Error.Title")
                    : Lang.Text("Launch.Error.ExportScriptTitle"),
                userSummary: currentLaunchOptions?.SaveBatch is null
                    ? Lang.Text("Minecraft.Launch.Error.LaunchFailed")
                    : Lang.Text("Minecraft.Launch.Error.ExportScriptFailed"));
            throw;
        }
    }

}
