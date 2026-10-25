using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageSetupUpdate
{
    public VersionDataModel updateInfo;

    public PageSetupUpdate()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        ModAnimation.AniControlEnabled += 1;

        // 静默更新已移除：老配置若还停在 DownloadAndInstall，就地改成「下载并提示」，
        // 否则下拉框会选中一个已经被隐藏的项，显示成空白。
        if (Config.Update.UpdateMode == LauncherAutoUpdateBehavior.DownloadAndInstall)
            Config.Update.UpdateMode = LauncherAutoUpdateBehavior.DownloadAndAnnounce;

        ComboSystemUpdateChannel.SelectedIndex = (int)Config.Update.UpdateChannel;
        ComboSystemUpdateMode.SelectedIndex = (int)Config.Update.UpdateMode;

        TextCurrentVersion.Text = "PCL Lite " + VersionNameFormat(ModBase.versionBaseName);
        ModAnimation.AniControlEnabled -= 1;
        CheckUpdate();
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
                    updateInfo = UpdateManager.remoteServer.GetLatestVersion(
                        UpdateManager.TargetChannel, SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
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
                    BtnUpdate_Timer();
                    BtnUpdate.IsEnabled = false;
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

    public void BtnUpdate_Timer()
    {
        while (UpdateManager.updateLoader is not null && UpdateManager.updateLoader.State == ModBase.LoadState.Loading)
        {
            ModBase.RunInUi(() => BtnUpdate.Text = Lang.Number(UpdateManager.updateLoader.Progress, "P2"));
            Thread.Sleep(200);
        }
    }

    private void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        if (UpdateManager.isUpdateWaitingRestart) UpdateManager.UpdateRestart(true);
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

        // 三档都是包含式的，越往下越激进：0 预览体验（正式 + pre）、
        // 1 快照尝鲜（+ snapshot）、2 始终最新（+ nightly）。只有后两档需要警告。
        var isCancelled = false;
        switch (ComboSystemUpdateChannel.SelectedIndex)
        {
            case 0:
            {
                break;
            }
            case 1:
            {
                if (ModMain.MyMsgBox(Lang.Text("Setup.Update.Channel.Snapshot.Warning.Message"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Title"),
                        Lang.Text("Setup.Update.Channel.Common.Warning.Confirm"),
                        Lang.Text("Common.Action.Cancel"), isWarn: true) == 2)
                    isCancelled = true;
                break;
            }
            case 2:
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
