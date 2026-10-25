using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Updates;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;

namespace PCL;

public static class UpdateManager
{
    public static bool isUpdateWaitingRestart;

    public static UpdatesWrapperModel remoteServer = new(new List<IUpdateSource>
    {
        // 第一优先：Pcl-Lite-service 仓库里的 update.json。
        // 它才是「当前该去哪个仓库拿版本」的唯一真源——发布仓库搬家后只改这份 json，
        // 所有还在跑老版本的机器照样能找到新版本，不会失联。
        new UpdatesManifestModel(),
        // 兜底：直接读当前仓库的 GitHub Releases。
        // 清单仓库挂了、或者清单还没跟上发版节奏时靠它。
        // 注意不要加回上游那三个源（Pysio 的 S3、Naids 的静态站、PCL2_CE_Server 的 raw），
        // 它们拉的是 PCL-CE 的版本清单，留着会把本项目的 exe 自动更新成 CE 官方版。
        new UpdatesGitHubModel()
    });

    /// <summary>
    ///     当前系统架构。
    /// </summary>
    private static UpdateArch CurrentArch => SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64;

    /// <summary>
    ///     当前用户通道对应的取版本通道。
    ///     <para>
    ///         三个通道都是**包含式**的：预览体验收正式版 + pre，快照尝鲜再加快照，
    ///         始终最新全收。所以激进通道的用户在正式版发布后照样能升上去，
    ///         不会因为手里是 nightly 就永远等不到「更新的」预发布。
    ///     </para>
    /// </summary>
    public static UpdateChannel TargetChannel => UpdateChannelMapping.ToChannel(Config.Update.UpdateChannel);

    /// <summary>
    ///     当前版本自身的不稳定等级。用于日志与「通道比当前版本更保守」时的提示。
    /// </summary>
    public static int CurrentVersionLevel => UpdateChannelLevels.FromVersionName(ModBase.versionBaseName);

    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        try
        {
            // 包含式通道 + SemVer 比较已经覆盖了「从激进通道切回保守通道」的情形：
            // 手里的 nightly 比当前通道最新版还新时 IsLatest 自然为 true，不会提示降级。
            return remoteServer.IsLatest(TargetChannel, CurrentArch, SemVer.Parse(ModBase.versionBaseName),
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
                    TargetChannel,
                    CurrentArch
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
                    TargetChannel,
                    CurrentArch, dlTargetPath));
                loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.Check"), _ =>
                {
                    // 更新源给不出官方哈希时 Sha256 是空串（Atom 降级通道、老接口）。
                    // 这时候必须跳过校验：拿空串去比只会必然失败，把本来能装上的更新拦下来，
                    // 报错还会写成「应该为 ，实际为 xxx」。
                    if (string.IsNullOrWhiteSpace(version.Sha256)) return;
                    var curHash = ModBase.GetFileSHA256(dlTargetPath);
                    if (!string.Equals(curHash, version.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new Exception(Lang.Text("Update.Error.Sha256Mismatch", version.Sha256, curHash));
                }));
                if (type == UpdateEnums.UpdateType.UpdateNow)
                    loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Update.Task.Install"), _ => UpdateRestart(true)));
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
            CurrentArch);
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
            CurrentArch, latestPCLPath);
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
        // 总开关：没勾「自动更新」就完全不碰网络，只有用户手动点检查更新才会去查。
        // 默认就是关的——装完不联网、不占带宽，也不在用户没同意的时候换掉自己的 exe。
        if (!Config.Update.AutoUpdate)
        {
            ModBase.Log("[Update] 未开启自动更新，跳过自动检查");
            return;
        }

        // 静默更新已经移除：不再存在「悄悄下完直接替换」的路径。
        // 老配置里选了 DownloadAndInstall 的用户在这里被就地降级成「下载并提示」，
        // 下载完成后会亮出重启按钮并弹提示，用户不点就不替换。
        if (Config.Update.UpdateMode == LauncherAutoUpdateBehavior.DownloadAndInstall)
        {
            ModBase.Log("[Update] 静默更新已移除，更新设置自动改为「自动下载并提示更新」");
            Config.Update.UpdateMode = LauncherAutoUpdateBehavior.DownloadAndAnnounce;
        }

        switch (Config.Update.UpdateMode)
        {
            case LauncherAutoUpdateBehavior.DownloadAndAnnounce:
                ModBase.Log("[Update] 更新设置: 自动下载并提示更新");
                UpdateStart(UpdateEnums.UpdateType.DownloadAndPrompt);
                break;
            case LauncherAutoUpdateBehavior.AnnounceOnly:
                ModBase.Log("[Update] 更新设置: 提示更新");
                UpdateStart(UpdateEnums.UpdateType.PromptOnly);
                break;
            default:
                ModBase.Log("[Update] 更新设置: 不自动检查更新（已选「关闭」）");
                return;
        }
    }
}
