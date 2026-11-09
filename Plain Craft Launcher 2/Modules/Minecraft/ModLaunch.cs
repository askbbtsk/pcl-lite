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
    public const string mesaLoaderWindowsVersion = "26.0.4";


    private static void McLaunchPrecheck()
    {
        if (Config.Debug.AddRandomDelay)
            Thread.Sleep(RandomUtils.NextInt(100, 2000));
        // 检查路径
        if (ModInstanceList.McMcInstanceSelected.PathIndie.Contains("!") ||
            ModInstanceList.McMcInstanceSelected.PathIndie.Contains(";"))
            throw new Exception(Lang.Text("Minecraft.Launch.Precheck.InvalidPathChars", ModInstanceList.McMcInstanceSelected.PathIndie));
        if (ModInstanceList.McMcInstanceSelected.PathInstance.Contains("!") ||
            ModInstanceList.McMcInstanceSelected.PathInstance.Contains(";"))
            throw new Exception(Lang.Text("Minecraft.Launch.Precheck.InvalidPathChars", ModInstanceList.McMcInstanceSelected.PathInstance));
        if (ModBase.IsUtf8CodePage() && !States.Hint.NonAsciiGamePath &&
            !ModInstanceList.McMcInstanceSelected.PathInstance.IsASCII())
        {
            var userChoice = ModMain.MyMsgBox(
                Lang.Text("Minecraft.Launch.Precheck.NonAsciiPath.Message", ModInstanceList.McMcInstanceSelected.Name),
                Lang.Text("Minecraft.Launch.Precheck.NonAsciiPath.Title"), Lang.Text("Minecraft.Launch.Precheck.NonAsciiPath.Continue"), Lang.Text("Minecraft.Launch.Precheck.NonAsciiPath.Back"), Lang.Text("Common.Hint.DoNotShowAgain"));
            if (userChoice == 2) throw new Exception("$$");
            if (userChoice == 3) States.Hint.NonAsciiGamePath = true;
        }

        // 检查实例
        if (ModInstanceList.McMcInstanceSelected is null)
            throw new Exception(Lang.Text("Minecraft.Launch.Precheck.NoInstance"));
        ModInstanceList.McMcInstanceSelected.Load();
        if (ModInstanceList.McMcInstanceSelected.state == McInstanceState.Error)
            throw new Exception(Lang.Text("Minecraft.Launch.Precheck.InstanceError", ModInstanceList.McMcInstanceSelected.Desc));
        EnvironmentRestorePointService.RecoverInterruptedRestore(ModInstanceList.McMcInstanceSelected.PathIndie);
        PackUpdateTransaction.RecoverInterrupted(ModInstanceList.McMcInstanceSelected.PathIndie);
        var savesPath = Path.Combine(ModInstanceList.McMcInstanceSelected.PathIndie, "saves");
        if (Directory.Exists(savesPath) && (File.GetAttributes(savesPath) & FileAttributes.ReparsePoint) == 0)
            foreach (var worldPath in Directory.EnumerateDirectories(savesPath))
                if ((File.GetAttributes(worldPath) & FileAttributes.ReparsePoint) == 0)
                    WorldBackupService.RecoverInterruptedRestore(worldPath);
        // 检查输入信息
        var checkResult = "";
        ModBase.RunInUiWait(() => checkResult = ProfileUi.IsProfileValid());
        var selectedProfile = ProfileService.Current;
        if (selectedProfile is null) // 没选档案
        {
            checkResult = Lang.Text("Minecraft.Launch.Precheck.NoProfile");
        }
        else if (ModInstanceList.McMcInstanceSelected.Info.HasLabyMod ||
                 Config.InstanceAuth.LoginRequirementSolution[ModInstanceList.McMcInstanceSelected?.PathInstance] == 1) // 要求正版验证
        {
            if (selectedProfile.ProfileType != ProfileType.Microsoft) checkResult = Lang.Text("Minecraft.Launch.Precheck.RequireMicrosoft");
        }
        else if (Config.InstanceAuth.LoginRequirementSolution[ModInstanceList.McMcInstanceSelected?.PathInstance] == 2) // 要求第三方验证
        {
            if (selectedProfile.ProfileType is not (ProfileType.Authlib or ProfileType.YggdrasilConnect))
                checkResult = Lang.Text("Minecraft.Launch.Precheck.RequireThirdParty");
            else if (selectedProfile.Server?.BeforeLast("/authserver") !=
                     Config.InstanceAuth.AuthServerAddress[ModInstanceList.McMcInstanceSelected?.PathInstance])
                checkResult = Lang.Text("Minecraft.Launch.Precheck.AuthServerMismatch");
        }
        else if (Config.InstanceAuth.LoginRequirementSolution[ModInstanceList.McMcInstanceSelected?.PathInstance] == 3) // 要求正版验证或第三方验证
        {
            if (selectedProfile.ProfileType == ProfileType.Offline)
                checkResult = Lang.Text("Minecraft.Launch.Precheck.RequireMicrosoftOrThirdParty");
            else if ((selectedProfile.ProfileType is ProfileType.Authlib or ProfileType.YggdrasilConnect) &&
                     selectedProfile.Server?.BeforeLast("/authserver") !=
                     Config.InstanceAuth.AuthServerAddress[ModInstanceList.McMcInstanceSelected?.PathInstance])
                checkResult = Lang.Text("Minecraft.Launch.Precheck.AuthServerMismatch");
        }

        if (!string.IsNullOrEmpty(checkResult))
            throw new ArgumentException(checkResult);

#if BETA
        if (currentLaunchOptions?.SaveBatch is null) // 保存脚本时不提示
        {
            ModBase.RunInNewThread(() =>
            {
                switch (States.System.LaunchCount)
                {
                    case 10:
                    case 20:
                    case 40:
                    case 60:
                    case 80:
                    case 100:
                    case 120:
                    case 150:
                    case 200:
                    case 250:
                    case 300:
                    case 350:
                    case 400:
                    case 500:
                    case 600:
                    case 700:
                    case 800:
                    case 900:
                    case 1000:
                    case 1200:
                    case 1400:
                    case 1600:
                    case 1800:
                    case 2000:
                        if (ModMain.MyMsgBox(
                                Lang.Text("Minecraft.Launch.Donate.Message", States.System.LaunchCount),
                                Lang.Text("Minecraft.Launch.Donate.Title", States.System.LaunchCount),
                                Lang.Text("Minecraft.Launch.Donate.Support"),
                                Lang.Text("Minecraft.Launch.Donate.Decline")) == 1)
                        {
                            ModBase.OpenWebsite("https://afdian.com/a/LTCat");
                        }
                        break;
                }
            }, "Donate");
        }
#endif
        
    }



    public static bool isLaunching;
    public static McLaunchOptions currentLaunchOptions;

    // 启动状态切换
    public static ModLoader.LoaderTask<McLaunchOptions, object> mcLaunchLoader = new("Loader Launch", McLaunchStart)
        { OnStateChanged = a => McLaunchState((dynamic)a) };

    public static ModLoader.LoaderCombo<object> mcLaunchLoaderReal;
    public static Process mcLaunchProcess;
    public static ModWatcher.Watcher mcLaunchWatcher;

    /// <summary>
    ///     指定启动中断时的提示文本。若不为 Nothing 则会显示为绿色。
    /// </summary>
    private static string abortHint;

    // 登录主模块加载器
    public static ModLoader.LoaderTask<McLoginData, McLoginResult> mcLoginLoader =
        new(Lang.Text("Minecraft.Launch.Stage.Login"), McLoginStart, McLoginInput, ThreadPriority.BelowNormal)
            { reloadTimeout = 1, ProgressWeight = 15d, block = false };


    // 各个登录方式的主对象与输入构造
    public static ModLoader.LoaderTask<McLoginMs, McLoginResult> mcLoginMsLoader =
        new("Loader Login Ms", McLoginMsStart) { reloadTimeout = 1 };

    public static ModLoader.LoaderTask<McLoginLegacy, McLoginResult> mcLoginLegacyLoader =
        new("Loader Login Legacy", McLoginLegacyStart);

    public static ModLoader.LoaderTask<McLoginServer, McLoginResult> mcLoginAuthLoader =
        new("Loader Login Auth", McLoginServerStartNew) { reloadTimeout = 1000 * 60 * 10 };




    public static JavaEntry mcLaunchJavaSelected;

    private static string mcLaunchArgument;

    private static readonly object extractJavaWrapperLock = new();

    private static readonly object extractLinkDLock = new();

}
