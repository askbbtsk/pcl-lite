using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Lunar;

namespace PCL;

/// <summary>
///     Lunar Client 板块的右栏：版本与账号选择、状态、启动入口、说明与运行日志。
/// </summary>
public partial class PageLunarLaunch
{
    private bool _subscribed;
    private bool _suppressComboEvents;
    private string _versionSignature = "";
    private string _accountSignature = "";
    private string _gameDirPreview = "";
    private Brush? _brushReady;
    private Brush? _brushIdle;

    public PageLunarLaunch()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        ModLunar.StateChanged += RefreshPage;
        ModLunar.LogReceived += AppendLog;
        _subscribed = true;

        _gameDirPreview = PreviewGameDirectory();
        ModLunar.Refresh();
        RefreshPage();

        // 版本列表可能还是空的（第一次进这个页面），后台拉一次；
        // 拉完会触发 StateChanged，界面自己会更新
        if (ModLunar.Versions.Count == 0 && ModLunar.IsInstalled)
            _ = ModLunar.RefreshVersionsAsync();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        ModLunar.StateChanged -= RefreshPage;
        ModLunar.LogReceived -= AppendLog;
        _subscribed = false;
    }

    /// <summary>
    ///     把 <see cref="ModLunar" /> 的当前状态铺到界面上。
    ///     刻意不叫 PageOnEnter —— 基类有同名方法，重名会把它藏掉。
    /// </summary>
    private void RefreshPage()
    {
        if (!IsLoaded) return;

        LabStatusLine.Text = ModLunar.StatusText;
        LabStatusDetail.Text = ModLunar.DetailText;

        _brushReady ??= (Brush)FindResource("ColorBrush3");
        _brushIdle ??= (Brush)FindResource("ColorBrushGray4");
        ShapeDot.Fill = ModLunar.IsInstalled ? _brushReady : _brushIdle;

        RefreshVersionCombo();
        RefreshAccountCombo();

        LabGameDir.Text = string.IsNullOrWhiteSpace(ModLunar.GameDirectory)
            ? _gameDirPreview
            : ModLunar.GameDirectory;
        LabCacheDir.Text = LunarClientService.MultiverDirectory(ModLunar.LunarRoot);
        LabJava.Text = string.IsNullOrWhiteSpace(ModLunar.JavaPath) ? "启动后自动定位" : ModLunar.JavaPath;

        RefreshButtons();
    }

    /// <summary>
    ///     版本下拉框。只在列表内容真的变了才重建 —— 启动过程中状态每秒变好几次，
    ///     每次都重建会把用户的选中状态和展开状态一起冲掉。
    /// </summary>
    private void RefreshVersionCombo()
    {
        var versions = ModLunar.Versions;
        var signature = versions.Count == 0
            ? "single:" + ModLunar.SelectedSubVersion
            : string.Join(",", versions.Select(x => x.Id));
        if (signature == _versionSignature) return;
        _versionSignature = signature;

        _suppressComboEvents = true;
        try
        {
            ComboVersion.Items.Clear();
            if (versions.Count == 0)
            {
                // 版本列表还没拉到：先显示配置里的那个，保证界面不空
                ComboVersion.Items.Add(new MyComboBoxItem
                {
                    Content = ModLunar.SelectedSubVersion,
                    Tag = ModLunar.SelectedSubVersion,
                    IsSelected = true
                });
            }
            else
            {
                foreach (var version in versions)
                    ComboVersion.Items.Add(new MyComboBoxItem
                    {
                        Content = version.DisplayName,
                        Tag = version.Id,
                        ToolTip = version.ParentName,
                        IsSelected = version.Id == ModLunar.SelectedSubVersion
                    });

                if (ComboVersion.SelectedItem is null && ComboVersion.Items.Count > 0)
                    ComboVersion.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressComboEvents = false;
        }
    }

    /// <summary>
    ///     账号下拉框，内容来自 Lunar 自己的账户库。
    /// </summary>
    private void RefreshAccountCombo()
    {
        var accounts = ModLunar.Accounts;
        var selected = ModLunar.SelectedAccount;
        var signature = string.Join(",", accounts.Select(x => x.LocalId)) + "#" + (selected?.LocalId ?? "");
        if (signature == _accountSignature) return;
        _accountSignature = signature;

        _suppressComboEvents = true;
        try
        {
            ComboAccount.Items.Clear();
            if (accounts.Count == 0)
            {
                ComboAccount.Items.Add(new MyComboBoxItem
                {
                    Content = "未找到 Lunar 账号（回落 PCL 登录）",
                    Tag = "",
                    IsSelected = true
                });
            }
            else
            {
                foreach (var account in accounts)
                    ComboAccount.Items.Add(new MyComboBoxItem
                    {
                        Content = account.DisplayName,
                        Tag = account.LocalId,
                        ToolTip = $"{account.Type}　{account.MinecraftUuid}",
                        IsSelected = account.LocalId == selected?.LocalId
                    });

                if (ComboAccount.SelectedItem is null && ComboAccount.Items.Count > 0)
                    ComboAccount.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressComboEvents = false;
        }
    }

    private void RefreshButtons()
    {
        var running = ModLunar.IsRunning;
        var busy = ModLunar.IsBusy;

        // 运行中这颗按钮直接变「强制停止」，位置不动
        BtnLaunch.Text = running ? "强制停止" : busy ? "运行中…" : "启动 Lunar";
        BtnLaunch.IsEnabled = running || (!busy && ModLunar.IsInstalled);
        BtnLaunch.ColorType = running ? MyButton.ColorState.Red : MyButton.ColorState.Highlight;

        ComboVersion.IsEnabled = !running && !busy;
        ComboAccount.IsEnabled = !running && !busy;
        BtnRefreshVersions.IsEnabled = !running && !busy;
    }

    private void ComboVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (ComboVersion.SelectedItem is not MyComboBoxItem { Tag: string id } || string.IsNullOrWhiteSpace(id))
            return;

        ModLunar.SelectedSubVersion = id;
    }

    private void ComboAccount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (ComboAccount.SelectedItem is not MyComboBoxItem { Tag: string localId }) return;

        // 只记下来，真正切 Lunar 的激活账号放在启动前做：
        // 光点下拉框就改别人的配置文件太粗暴了
        Config.Preference.Lunar.AccountLocalId = localId;
    }

    private async void BtnRefreshVersions_Click(object sender, MouseButtonEventArgs e)
    {
        BtnRefreshVersions.IsEnabled = false;
        HintService.Hint("正在拉取 Lunar 版本列表…");
        try
        {
            await ModLunar.RefreshVersionsAsync();
            _versionSignature = ""; // 强制重建下拉框
            RefreshPage();
            HintService.Hint($"已获取 {ModLunar.Versions.Count} 个 Lunar 版本");
        }
        finally
        {
            BtnRefreshVersions.IsEnabled = true;
        }
    }

    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModLunar.IsRunning)
        {
            ModLunar.Stop();
            RefreshButtons();
            return;
        }

        if (ModLunar.IsBusy) return;
        ModLunar.Launch();
        RefreshButtons();
    }

    private void BtnOpenRoot_Click(object sender, MouseButtonEventArgs e)
    {
        var directory = LunarClientService.MultiverDirectory(ModLunar.LunarRoot);
        if (!Directory.Exists(directory))
        {
            ModMain.MyMsgBox("物料目录还不存在，先启动一次游戏再试。", "打开物料目录");
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private void BtnCopyCommand_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ModLunar.LastCommandLine))
        {
            ModMain.MyMsgBox("还没有启动过，没有命令行可复制。", "复制启动命令");
            return;
        }

        // 命令行里有 accessToken，复制出去前先抹掉，免得被顺手贴到群里
        var redacted = RedactToken(ModLunar.LastCommandLine);
        Clipboard.SetText(redacted);
        HintService.Hint("启动命令已复制（accessToken 已打码）");
    }

    private void BtnClearLog_Click(object sender, MouseButtonEventArgs e)
    {
        TextLog.Clear();
    }

    private void AppendLog(string line)
    {
        ModBase.RunInUi(() =>
        {
            TextLog.AppendText(line + Environment.NewLine);
            TextLog.ScrollToEnd();
        });
    }

    /// <summary>
    ///     预览这次启动会写进哪个目录：和 <see cref="ModLunar" /> 的取值规则保持一致。
    ///     只在进入页面时算一次，避免每次状态刷新都碰实例加载。
    /// </summary>
    private static string PreviewGameDirectory()
    {
        try
        {
            var instance = ModInstanceList.McMcInstanceSelected;
            if (instance is not null && Directory.Exists(instance.PathIndie))
                return instance.PathIndie;
        }
        catch (Exception)
        {
            // 实例还没加载完，退回根目录
        }

        return ModFolder.mcFolderSelected;
    }

    private static string RedactToken(string commandLine)
    {
        const string flag = "--accessToken";
        var index = commandLine.IndexOf(flag, StringComparison.Ordinal);
        if (index < 0) return commandLine;

        var start = index + flag.Length;
        while (start < commandLine.Length && commandLine[start] == ' ') start++;
        var end = commandLine.IndexOf(' ', start);
        if (end < 0) end = commandLine.Length;

        return commandLine[..start] + "[REDACTED]" + commandLine[end..];
    }
}
