using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;

namespace PCL;

public static class UpdateManager
{
    public static bool isUpdateWaitingRestart;

    public static UpdatesWrapperModel remoteServer = new(new List<IUpdateSource>
    {
        // 只用自己的 GitHub Releases。上游那三个源（Pysio 的 S3、Naids 的静态站、
        // PCL2_CE_Server 的 raw）拉的是 PCL-CE 的版本清单，留着会把本项目的 exe
        // 自动更新成 CE 官方版。仓库地址见 ProjectLinks。
        new UpdatesGitHubModel()
    });

    /// <summary>
    ///     用户选的通道是不是预发布（Beta / Dev）。
    ///     <para>
    ///     Dev 也走 beta 通道：本项目没有单独的 Dev 版本源，把 Dev 当正式版处理
    ///     会让选了 Dev 的人反而收不到任何预发布版本。
    ///     </para>
    /// </summary>
    private static bool _UseBetaChannel =>
        Config.Update.UpdateChannel != Core.App.UpdateChannel.Release;

    /// <summary>
    ///     是否应当从预发布通道取版本。当前版本自带 beta 后缀时无条件走 beta，
    ///     否则跟随用户选的通道。
    /// </summary>
    public static bool IsCurrentVersionBeta
    {
        get
        {
            if (ModBase.versionBaseName.Contains("beta"))
                return true;
            return _UseBetaChannel;
        }
    }

    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        try
        {
            // 当前是 beta 版，但用户把通道选回了正式版：只有正式版和 beta 版都不比
            // 当前版本新时才算「已是最新」，否则提示更新。
            if (ModBase.versionBaseName.Contains("beta") && !_UseBetaChannel)
            {
                var isNewerThanStable = remoteServer.IsLatest(UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, SemVer.Parse(ModBase.versionBaseName),
                    ModBase.versionCode);
                var isBetaLatest = remoteServer.IsLatest(UpdateChannel.beta,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, SemVer.Parse(ModBase.versionBaseName),
                    ModBase.versionCode);
                return isNewerThanStable && isBetaLatest
                    ? UpdateEnums.VersionStatus.Latest
                    : UpdateEnums.VersionStatus.NotLatest;
            }

            return remoteServer.IsLatest(
                IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, SemVer.Parse(ModBase.versionBaseName),
                ModBase.versionCode)
                ? UpdateEnums.VersionStatus.Latest
                : UpdateEnums.VersionStatus.NotLatest;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Update.Check.Failed"),
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Update.Check.Failed"));
            return UpdateEnums.VersionStatus.Unknown;
        }
    }
    
    public static ModLoader.LoaderCombo<JsonObject> updateLoader;

    public static void UpdateStart(UpdateEnums.UpdateType type, string receivedKey = null, bool forceValidated = false)
    {
        var dlTargetPath = ModBase.exePath + @"PCL\Plain Craft Launcher Community Edition.exe";
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var version = remoteServer.GetLatestVersion(
                    IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64
                );

                ModBase.WriteFile($"{ModBase.pathTemp}CEUpdateLog.md", version.Changelog);
                ModBase.Log($"[Update] 远程最新版本: {version.VersionName}, 当前版本: {ModBase.versionBaseName}");
                if (!(SemVer.Parse(version.VersionName) > SemVer.Parse(ModBase.versionBaseName)))
                    return;
                if (type == UpdateEnums.UpdateType.PromptOnly)
                {
                    ModBase.RunInUi(() =>
                    {
                        if (ModMain.MyMsgBox(
                                Lang.Text("Update.Available", ModBase.versionBaseName, version.VersionName),
                                Lang.Text("Update.Title"),
                                Lang.Text("Update.Action"),
                                Lang.Text("Common.Action.Cancel")
                            ) == 1)
                            ModMain.frmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate);
                    });
                    return;
                    // 构造步骤加载器
                }

                var loaders = new List<ModLoader.LoaderBase>();
                // 下载
                loaders.AddRange(remoteServer.GetDownloadLoader(
                    IsCurrentVersionBeta ? UpdateChannel.beta : UpdateChannel.stable,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, dlTargetPath));
                loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.Check"), _ =>
                {
                    var curHash = ModBase.GetFileSHA256(dlTargetPath);
                    if ((curHash ?? "") != (version.Sha256 ?? ""))
                        throw new Exception(Lang.Text("Update.Error.Sha256Mismatch", version.Sha256, curHash));
                }));
                if (type == UpdateEnums.UpdateType.UpdateNow)
                    loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.Install"), _ => UpdateRestart(true)));
                else if (type == UpdateEnums.UpdateType.Silent)
                    loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.Prepare"), _ => isUpdateWaitingRestart = true));
                else if (type == UpdateEnums.UpdateType.DownloadAndPrompt)
                    loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.ShowButton"), _ =>
                    {
                        isUpdateWaitingRestart = true;
                        ModBase.RunInUi(() =>
                        {
                            ModMain.frmMain.BtnExtraUpdateRestart.ToolTip =
                                Lang.Text("Main.Extra.UpdateRestart.ToolTipWithVersion", ModBase.versionBaseName, version.VersionName);
                            ModMain.frmMain.BtnExtraUpdateRestart.ShowRefresh();
                            ModMain.frmMain.BtnExtraUpdateRestart.Ribble();
                        });
                    })
                    {
                        show = false
                    });
                loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.RefreshSettings"), _ =>
                {
                    if (ModMain.frmSetupUpdate is not null)
                        ModBase.RunInUi(() =>
                        {
                            ModMain.frmSetupUpdate.BtnUpdate.Text = Lang.Text("Update.Task.RestartInstall");
                            ModMain.frmSetupUpdate.BtnUpdate.IsEnabled = true;
                        });
                })
                {
                    show = false
                });
                // 启动
                updateLoader = new ModLoader.LoaderCombo<JsonObject>(Lang.Text("Update.Title"), loaders);
                updateLoader.Start();
                if (type == UpdateEnums.UpdateType.UpdateNow)
                {
                    ModLoader.LoaderTaskbarAdd(updateLoader);
                    ModMain.frmMain.BtnExtraDownload.ShowRefresh();
                    ModMain.frmMain.BtnExtraDownload.Ribble();
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Update] 获取启动器更新失败");
                if (type != UpdateEnums.UpdateType.Silent)
                    HintService.Hint(_DescribeUpdateError(ex), HintType.Error);
            }
        });
    }

    /// <summary>
    /// 把更新失败翻译成一句能让人动手的话。
    /// <para>
    /// 更新源自己能解释原因时（没发 Release、tag 不合规、仓库没配）就直接用它的说法；
    /// 其余情况（断网、超时、DNS 挂了）仍然用统一的提示，因为底层异常文本对用户没有意义。
    /// </para>
    /// </summary>
    private static string _DescribeUpdateError(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
            if (cur is UpdateSourceException source && !string.IsNullOrWhiteSpace(source.Message))
                return source.Message;

        return Lang.Text("Update.Error.FetchFailed");
    }

    public static void UpdateRestart(bool triggerRestartAndByEnd, bool triggerRestart = true)
    {
        try
        {
            var fileName = ModBase.exePath + @"PCL\Plain Craft Launcher Community Edition.exe";
            if (!File.Exists(fileName))
            {
                ModBase.Log("[System] 更新失败：未找到更新文件");
                return;
            }

            // id old new restart
            var text =
                $"update {Process.GetCurrentProcess().Id} \"{Basics.ExecutablePath}\" \"{fileName}\" {(triggerRestart ? "true" : "false")}";
            ModBase.Log("[System] 更新程序启动，参数：" + text);
            Process.Start(new ProcessStartInfo(fileName)
                { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, Arguments = text });
            if (triggerRestartAndByEnd)
            {
                ModMain.frmMain.EndProgram(false, true);
                ModBase.Log("[System] 已由于更新强制结束程序");
            }
        }
        catch (Win32Exception ex)
        {
            ModBase.Log(ex, "自动更新时触发 Win32 错误，疑似被拦截");
            ModMain.MyMsgBox(
                Lang.Text("Update.Error.UpdateBlockedMessage", ModBase.exePath),
                Lang.Text("Update.Error.UpdateBlocked"),
                Lang.Text("Common.Action.Confirm"),
                "",
                "",
                true);
        }
    }

    /// <summary>
    ///     确保 PathTemp 下的 Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。
    ///     如果不是，则下载一个。
    /// </summary>
    internal static void DownloadLatestPCL(ModLoader.LoaderBase loaderToSyncProgress = null)
    {
        // 注意：如果要自行实现这个功能，请换用另一个文件路径，以免与官方版本冲突
        var latestPCLPath = Path.Combine(ModBase.pathTemp, "CE-Latest.exe");
        var target = remoteServer.GetLatestVersion(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
        if (target is null)
            throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
        if (File.Exists(latestPCLPath) && (ModBase.GetFileSHA256(latestPCLPath) ?? "") == (target.Sha256 ?? ""))
        {
            ModBase.Log("[System] 最新版 PCL 已存在，跳过下载");
            return;
        }

        if ((ModBase.GetFileSHA256(Basics.ExecutablePath) ?? "") == (target.Sha256 ?? "")) // 正在使用的版本符合要求，直接拿来用
        {
            ModBase.CopyFile(Basics.ExecutablePath, latestPCLPath);
            return;
        }

        var loaders = remoteServer.GetDownloadLoader(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, latestPCLPath);
        var loader = new ModLoader.LoaderCombo<int>(Lang.Text("Update.Task.DownloadLatestStable"), loaders);
        loader.Start();
        loader.WaitForExit();
    }

    public static ModLoader.LoaderTask<int, int> serverLoader =
        new(Lang.Text("Update.Service.PclCe"),
            _ => LoadOnlineInfo(),
            priority: ThreadPriority.BelowNormal);

    private static void LoadOnlineInfo()
    {
        ScheduleBasedOnConfig();
        AnnouncementService.Load();
    }

    private static void ScheduleBasedOnConfig()
    {
        switch (Config.Update.UpdateMode)
        {
            case LauncherAutoUpdateBehavior.DownloadAndInstall:
                ModBase.Log("[Update] 更新设置: 自动下载并安装更新");
                if (GetVersionStatus() != UpdateEnums.VersionStatus.Latest)
                    UpdateStart(UpdateEnums.UpdateType.Silent);
                break;
            case LauncherAutoUpdateBehavior.DownloadAndAnnounce:
                ModBase.Log("[Update] 更新设置: 自动下载并提示更新");
                UpdateStart(UpdateEnums.UpdateType.DownloadAndPrompt);
                break;
            case LauncherAutoUpdateBehavior.AnnounceOnly:
                ModBase.Log("[Update] 更新设置: 提示更新");
                UpdateStart(UpdateEnums.UpdateType.PromptOnly);
                break;
            default:
                ModBase.Log("[Update] 更新设置: 不自动检查更新");
                return;
        }
    }

    /// <summary>
    ///     展示社区版提示
    /// </summary>
    /// <param name="IsUpdate">是否为更新时启动</param>
    public static void ShowCEAnnounce()
    {
        ModMain.MyMsgBox(Lang.Text("Update.CommunityNotice.Body"),
            Lang.Text("Update.CommunityNotice.Title"),
            Lang.Text("Update.CommunityNotice.Confirm"));
    }
}
