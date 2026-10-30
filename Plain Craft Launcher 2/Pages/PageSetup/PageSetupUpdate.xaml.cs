using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageSetupUpdate
{
    public VersionDataModel updateInfo;

    /// <summary>
    ///     进度刷新计时器。以前这里是一个 <c>while + Thread.Sleep</c> 的忙等循环，
    ///     而且直接跑在 UI 线程上——只要进页面时正好有更新在下载，整个窗口就再也画不动，
    ///     连「取消」都点不了（UI 线程不泵消息，动画线程的 RunInUiWait 也跟着一起堵死）。
    ///     现在改成 DispatcherTimer：每 200ms 回一次 UI 线程刷文案，期间界面照常响应。
    /// </summary>
    private DispatcherTimer? _updateProgressTimer;

    public PageSetupUpdate()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
        Unloaded += (_, _) => _StopUpdateProgressTimer();
    }

    private void Init()
    {
        ModAnimation.AniControlEnabled += 1;

        // 老配置迁移：
        // - DownloadAndInstall（静默更新）已移除，改成「下载并提示」
        // - Disable（旧下拉里的「不自动检查」）现在由上面的开关表达，统一收成「仅提示」+ 关开关
        if (Config.Update.UpdateMode is LauncherAutoUpdateBehavior.DownloadAndInstall
            or LauncherAutoUpdateBehavior.Disable)
        {
            Config.Update.UpdateMode = LauncherAutoUpdateBehavior.DownloadAndAnnounce;
        }

        CheckAutoUpdate.Checked = Config.Update.AutoUpdate;
        ComboSystemUpdateChannel.SelectedIndex = (int)Config.Update.UpdateChannel;
        ComboSystemUpdateMode.SelectedIndex = (int)Config.Update.UpdateMode;
        _RefreshUpdateModeEnabled();

        TextCurrentVersion.Text = "PCL Lite " + VersionNameFormat(ModBase.versionBaseName);
        ModAnimation.AniControlEnabled -= 1;
        CheckUpdate();
    }

    /// <summary>
    ///     没勾自动更新时，「更新方式」下拉没有意义，置灰。
    /// </summary>
    private void _RefreshUpdateModeEnabled()
    {
        var on = Config.Update.AutoUpdate;
        ComboSystemUpdateMode.IsEnabled = on;
        ComboSystemUpdateMode.Opacity = on ? 1 : 0.4;
        TextUpdateModeTitle.Opacity = on ? 1 : 0.4;
    }

    private void CheckAutoUpdate_Change(object sender, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        Config.Update.AutoUpdate = CheckAutoUpdate.Checked == true;
        _RefreshUpdateModeEnabled();
    }

    private async Task<UpdateStatus> IsLatestAsync()
    {
        try
        {
            // 修复：使用 dynamic 绕过命名空间重名导致的编译期类型冲突，
            // 或者你可以尝试替换为 PCL.Core.App.SemVer.Parse(ModBase.versionBaseName)
            if (await UpdateManager.remoteServer.IsLatestAsync(
                    UpdateManager.TargetChannel,
                    SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64,
                    SemVer.Parse(ModBase.versionBaseName),
                    ModBase.versionCode))
            {
                ModBase.Log("[Update] 已是最新版本");
                return UpdateStatus.Latest;
            }

            ModBase.Log("[Update] 有可用的新版本");
            return UpdateStatus.Available;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Setup.Update.Error.NetworkFailed"),
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Setup.Update.Error.NetworkFailed"));
            return UpdateStatus.Error;
        }
    }

    public async void CheckUpdate()
    {
        await CheckUpdateAsync(false);
    }

    /// <summary>
    ///     检查更新。
    /// </summary>
    /// <param name="force">
    ///     是否强制重新拉取版本列表。用户主动点「重新检查」或切换通道时为 true——
    ///     否则会沿用启动时拉到的缓存，发版前就开着的启动器永远看不到新版本。
    /// </param>
    private async Task CheckUpdateAsync(bool force)
    {
        ModBase.Log(force ? "[Update] 开始检查更新（强制刷新）" : "[Update] 开始检查更新");
        CardUpdate.Visibility = Visibility.Collapsed;
        CardCheck.Visibility = Visibility.Visible;
        TextCurrentDesc.Text = Lang.Text("Setup.Update.Checking");
        BtnCheckAgain.IsEnabled = false;

        // 强制刷新时先把版本列表换掉，后面取详情才不会拿到旧的那一条
        if (force)
            await Task.Run(() => UpdateManager.remoteServer.RefreshCache(true));

        switch (await IsLatestAsync())
        {
            case UpdateStatus.Available:
            {
                Exception checkUpdateEx = null;
                try
                {
                    // 取版本详情要读缓存里的 Release 列表，缓存刚好过期时会真的发一次网络请求。
                    // 这里原本是同步调用，等于把网络 IO 放在 UI 线程上——断网时窗口直接假死。
                    updateInfo = await Task.Run(() => UpdateManager.remoteServer.GetLatestVersion(
                        UpdateManager.TargetChannel, SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64));
                    TextUpdateName.Text = "PCL Lite " + VersionNameFormat(updateInfo.VersionName);
                    var summary = updateInfo.Changelog.Between("<summary>", "</summary>");
                    if (!updateInfo.Changelog.Contains("<summary>") || string.IsNullOrWhiteSpace(summary.Trim()))
                        TextChangelog.Text = Lang.Text("Setup.Update.Changelog.Empty");
                    else
                        TextChangelog.Text = summary;
                }
                catch (Exception ex)
                {
                    checkUpdateEx = ex;
                }

                BtnCheckAgain.IsEnabled = true;
                if (updateInfo is null)
                {
                    TextCurrentDesc.Text = Lang.Text("Setup.Update.CheckFailed");
                    if (checkUpdateEx is not null)
                        ModBase.Log(
                            checkUpdateEx,
                            "[Update] 检查更新失败",
                            ModBase.LogLevel.Msgbox,
                            userSummary: Lang.Text("Update.Check.Failed"));
                    else
                        ModBase.Log(
                            "[Update] 检查更新失败",
                            ModBase.LogLevel.Msgbox,
                            userSummary: Lang.Text("Update.Check.Failed"));
                    return;
                }

                if (UpdateManager.updateLoader is not null && UpdateManager.updateLoader.State == ModBase.LoadState.Loading)
                {
                    // 先把按钮按住再开计时器：以前顺序反了，阻塞期间按钮还是可点的，
                    // 用户等得不耐烦就会连点，起出一堆并发下载
                    BtnUpdate.IsEnabled = false;
                    _StartUpdateProgressTimer();
                }
                else if (UpdateManager.isUpdateWaitingRestart)
                {
                    BtnUpdate.Text = Lang.Text("Setup.Update.RestartInstall");
                    BtnUpdate.IsEnabled = true;
                }
                else
                {
                    BtnUpdate.Text = Lang.Text("Setup.Update.Install");
                    BtnUpdate.IsEnabled = true;
                }

                CardUpdate.Visibility = Visibility.Visible;
                CardCheck.Visibility = Visibility.Collapsed;
                break;
            }
            case UpdateStatus.Latest:
            {
                CardUpdate.Visibility = Visibility.Collapsed;
                CardCheck.Visibility = Visibility.Visible;
                BtnCheckAgain.IsEnabled = true;
                TextCurrentDesc.Text = Lang.Text("Setup.Update.Latest");
                break;
            }
            case UpdateStatus.Error:
            {
                CardUpdate.Visibility = Visibility.Collapsed;
                CardCheck.Visibility = Visibility.Visible;
                BtnCheckAgain.IsEnabled = true;
                TextCurrentDesc.Text = Lang.Text("Setup.Update.CheckFailed");
                break;
            }
        }
    }

    /// <summary>
    ///     开始刷新按钮上的下载进度。
    ///     <para>
    ///     绝对不能写成 <c>while (State == Loading) { ...; Thread.Sleep(200); }</c>：
    ///     这个方法是从 <see cref="CheckUpdateAsync"/> 里调的，那里 await 之后回到了 UI 线程，
    ///     忙等就等于把消息泵掐死——窗口变「无响应」，动画线程卡在 RunInUiWait，
    ///     任务管理里的取消按钮也永远收不到点击。加载器本身还在后台跑，
    ///     所以看起来是「点更新卡死、点取消也卡死」。
    ///     </para>
    /// </summary>
    private void _StartUpdateProgressTimer()
    {
        _StopUpdateProgressTimer();
        _updateProgressTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _updateProgressTimer.Tick += (_, _) =>
        {
            if (UpdateManager.updateLoader is null ||
                UpdateManager.updateLoader.State != ModBase.LoadState.Loading)
            {
                // 加载器结束了：停止计时，把按钮交还给正常的收尾流程
                _StopUpdateProgressTimer();
                return;
            }

            BtnUpdate.Text = Lang.Number(UpdateManager.updateLoader.Progress, "P2");
        };
        _updateProgressTimer.Start();
    }

    private void _StopUpdateProgressTimer()
    {
        if (_updateProgressTimer is null) return;
        _updateProgressTimer.Stop();
        _updateProgressTimer = null;
    }

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        // 已经下载好了：这一下是「重启并安装」，走完就直接结束程序。
        // 以前没有 return，UpdateRestart 里 EndProgram 都跑了还会继续往下执行
        // UpdateStart(UpdateNow)，等于一边关程序一边又起一个下载加载器
        if (UpdateManager.isUpdateWaitingRestart)
        {
            UpdateManager.UpdateRestart(true);
            return;
        }

        // 开始更新流程
        UpdateManager.UpdateStart(UpdateEnums.UpdateType.UpdateNow);
    }

    private void BtnChangelogDetail_Click(object sender, EventArgs e)
    {
        if (updateInfo is null)
            ModMain.MyMsgBox(Lang.Text("Setup.Update.Changelog.Unavailable"), Lang.Text("Setup.Update.Changelog.Title"));
        else
            ModMain.MyMsgBoxMarkdown(updateInfo.Changelog, Lang.Text("Setup.Update.Changelog.Title"));
    }

    private void ComboSystemUpdateMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled == 0)
            Config.Update.UpdateMode = (LauncherAutoUpdateBehavior)ComboSystemUpdateMode.SelectedIndex;
    }

    private void ComboSystemUpdateBranch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;

        // 四档都是包含式的，越往下越激进：0 稳定版（只正式）、1 预览体验（+ pre）、
        // 2 快照尝鲜（+ snapshot）、3 始终最新（+ nightly）。只有后两档需要警告。
        var isCancelled = false;
        switch (ComboSystemUpdateChannel.SelectedIndex)
        {
            case 0:
            case 1:
            {
                break;
            }
            case 2:
            {
                if (ModMain.MyMsgBox(Lang.Text("Setup.Update.Channel.Snapshot.Warning.Message"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Confirm"),
                        Lang.Text("Common.Action.Cancel"), isWarn: true) == 2)
                    isCancelled = true;
                break;
            }
            case 3:
            {
                if (ModMain.MyMsgBox(Lang.Text("Setup.Update.Channel.Latest.Warning.Message"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Confirm"),
                        Lang.Text("Common.Action.Cancel"), isWarn: true) == 2)
                {
                    isCancelled = true;
                    break;
                }

                var confirmText = Lang.Text("Setup.Update.Channel.Latest.FinalConfirm.ExpectedInput");
                var ret = ModMain.MyMsgBoxInput(
                    Lang.Text("Setup.Update.Channel.Latest.FinalConfirm.Title"),
                    Lang.Text("Setup.Update.Channel.Latest.FinalConfirm.Message", confirmText),
                    button1: Lang.Text("Setup.Update.Channel.Latest.FinalConfirm.Submit"),
                    button2: Lang.Text("Common.Action.Cancel"), isWarn: true);

                if (ret != confirmText)
                {
                    HintService.Hint(Lang.Text("Setup.Update.Channel.Latest.FinalConfirm.WrongInput"));
                    isCancelled = true;
                }

                break;
            }
        }

        if (isCancelled)
        {
            ModAnimation.AniControlEnabled += 1;
            ComboSystemUpdateChannel.SelectedItem = e.RemovedItems[0];
            ModAnimation.AniControlEnabled -= 1;
            return;
        }

        // 先把通道写进配置，再检查。反过来做的话，切换后的第一次检查用的还是旧通道，
        // 用户会看到「已是最新」——而那个结论是按旧通道算的。
        Config.Update.UpdateChannel = (Core.App.UpdateChannel)ComboSystemUpdateChannel.SelectedIndex;
        _ = CheckUpdateAsync(true);
    }

    private void BtnChangelog_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenWebsite(ProjectLinks.Releases + "/tag/v" + ModBase.versionBaseName);
    }

    public string VersionNameFormat(string str)
    {
        str = str.Replace("v", "");
        if (!str.Contains("-"))
            return str;
        var add = str.AfterLast("-");
        str = str.BeforeLast("-");
        return $"{str} {add.Replace(".", " ").Replace("beta", "Beta").Replace("rc", "RC")}";
    }

    private void BtnCheckAgain_OnClick(object sender, MouseButtonEventArgs e)
    {
        // 用户主动点了「重新检查」，就必须真的去问一次服务器，不能拿缓存糊弄
        _ = CheckUpdateAsync(true);
    }

    private enum UpdateStatus
    {
        Checking = 0,
        Available = 1,
        Error = 2,
        Latest = 3
    }
}
