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
        // 拉完会触发 StateChanged，界面自己会更新。
        // 没装 Lunar 也要拉 —— 物料由 PCL 自己下，版本列表是启动前就要用的
        if (ModLunar.Versions.Count == 0)
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
        RefreshAccountHint();

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
    ///     账号下拉框：分「PCL Lite 登录账号」和「Lunar 启动器账号」两组。
    ///     <para>
    ///         分组头是禁用项（<see cref="UIElement.IsEnabled" /> = false），点不动也选不上，
    ///         只用来在列表里画一条分界。真正能选的是下面那些，<c>Tag</c> 带着
    ///         <c>pcl:</c> / <c>lunar:</c> 前缀，选完原样存进配置。
    ///     </para>
    /// </summary>
    private void RefreshAccountCombo()
    {
        var accounts = ModLunar.Accounts;
        var pclProfiles = ModLunar.PclProfiles;

        // 1.5.0 存的是裸的 localId，没有前缀。这里补上，否则老用户的选中项
        // 会对不上任何一项，下拉框看起来像「没选账号」
        var selectedTag = Config.Preference.Lunar.AccountLocalId ?? "";
        if (!string.IsNullOrEmpty(selectedTag)
            && !selectedTag.StartsWith("pcl:", StringComparison.OrdinalIgnoreCase)
            && !selectedTag.StartsWith("lunar:", StringComparison.OrdinalIgnoreCase))
            selectedTag = "lunar:" + selectedTag;

        // 选中项也要进签名：换账号时要重建，否则下拉框显示的还是旧的
        var signature = string.Join(",", accounts.Select(x => x.LocalId + (x.IsExpired ? "!" : "")))
                        + "|" + string.Join(",", pclProfiles.Select(x => x.ProfileId + ":" + x.UserName +
                            (x.IsExpired ? "!" : "")))
                        + "#" + selectedTag;
        if (signature == _accountSignature) return;
        _accountSignature = signature;

        _suppressComboEvents = true;
        try
        {
            ComboAccount.Items.Clear();

            if (pclProfiles.Count > 0)
            {
                ComboAccount.Items.Add(MakeGroupHeader("PCL Lite 登录账号"));
                foreach (var profile in pclProfiles)
                    ComboAccount.Items.Add(new MyComboBoxItem
                    {
                        // 下拉框里只写名字，过期状态放到下面那行说明里，免得把右侧挤歪
                        Content = profile.UserName,
                        Tag = ModLunar.SelectionTag(profile),
                        ToolTip = profile.IsExpired
                            ? $"PCL Lite 登录　{profile.Uuid}\n令牌已于 {profile.ExpiresAt?.ToLocalTime():yyyy-MM-dd HH:mm} 过期，启动前会尝试刷新"
                            : $"PCL Lite 登录　{profile.Uuid}",
                        IsSelected = selectedTag == ModLunar.SelectionTag(profile)
                    });
            }

            ComboAccount.Items.Add(MakeGroupHeader("Lunar 启动器账号"));

            if (accounts.Count == 0)
            {
                ComboAccount.Items.Add(new MyComboBoxItem
                {
                    Content = "（Lunar 账户库是空的）",
                    Tag = "",
                    IsEnabled = false,
                    IsSelected = string.IsNullOrEmpty(selectedTag)
                });
            }
            else
            {
                foreach (var account in accounts)
                    ComboAccount.Items.Add(new MyComboBoxItem
                    {
                        Content = account.DisplayName,
                        Tag = ModLunar.SelectionTag(account),
                        ToolTip = account.IsExpired && account.ExpiresAt is { } expires
                            ? $"{account.Type}　{account.MinecraftUuid}\nLunar 会话已于 {expires.ToLocalTime():yyyy-MM-dd HH:mm} 过期，进游戏后会通过 IPC 续期"
                            : $"{account.Type}　{account.MinecraftUuid}",
                        IsSelected = selectedTag == ModLunar.SelectionTag(account)
                    });
            }

            // 没有任何一项被选中时（第一次进页面、或者配置里那个账号已经不在库里）
            // 落到第一个可选账号上，不然下拉框是空白，用户会以为读不到账号
            if (ComboAccount.SelectedItem is null)
            {
                var first = ComboAccount.Items.OfType<MyComboBoxItem>()
                                .FirstOrDefault(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.Tag as string))
                            ?? ComboAccount.Items.OfType<MyComboBoxItem>().LastOrDefault();
                if (first is not null) first.IsSelected = true;
            }
        }
        finally
        {
            _suppressComboEvents = false;
        }
    }

    /// <summary>分组头：禁用状态，点不动、也选不上。</summary>
    private static MyComboBoxItem MakeGroupHeader(string text) => new()
    {
        Content = text,
        Tag = "",
        IsEnabled = false
    };

    /// <summary>
    ///     账号状态那行说明。会话过期、PCL 里找不到对应登录，都写在这里 ——
    ///     这块是自动换行的，写多长都不会挤到右边的按钮和日志。
    /// </summary>
    private void RefreshAccountHint()
    {
        var account = ModLunar.SelectedAccount;
        var pclProfile = ModLunar.SelectedPclProfile;

        string text;
        if (account is { IsExpired: true } expired)
        {
            var pcl = ModLunar.MatchPclProfile(expired);
            text = $"Lunar 会话已于 {expired.ExpiresAt!.Value.ToLocalTime():yyyy-MM-dd HH:mm} 过期。" +
                   (pcl is not null
                       ? $"启动前会先用 PCL 里的 {pcl.UserName} 刷新一次，不需要打开 Lunar 启动器。"
                       : "PCL 里没有这个账号的登录，没法自动续期 —— 请先在主页面登录它。");
        }
        else if (pclProfile is { IsExpired: true } pcl)
        {
            text = $"PCL 里 {pcl.UserName} 的令牌已于 {pcl.ExpiresAt?.ToLocalTime():yyyy-MM-dd HH:mm} 过期，启动前会先刷新一次。";
        }
        else if (pclProfile is not null)
        {
            text = $"用 PCL Lite 的登录启动：{pclProfile.UserName}。游戏内身份仍取 Lunar 账户库里同一个 UUID 的账号。";
        }
        else
        {
            text = "";
        }

        LabAccountHint.Text = text;
        LabAccountHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshButtons()
    {
        var running = ModLunar.IsRunning;
        var busy = ModLunar.IsBusy;

        // 运行中这颗按钮直接变「强制停止」，位置不动。
        // 没装过 Lunar 也能点 —— 物料由 PCL 自己从服务端清单补齐
        BtnLaunch.Text = running ? "强制停止" : busy ? "运行中…" : "启动 Lunar";
        BtnLaunch.IsEnabled = running || !busy;
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
        if (ComboAccount.SelectedItem is not MyComboBoxItem { Tag: string tag }) return;

        // 分组头的 Tag 是空串，选中它什么都不做（正常情况下也选不上，它是禁用项）
        if (string.IsNullOrWhiteSpace(tag)) return;

        // 只记下来，真正切 Lunar 的激活账号放在启动前做：
        // 光点下拉框就改别人的配置文件太粗暴了
        Config.Preference.Lunar.AccountLocalId = tag;
        RefreshAccountHint();
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
            ModMain.MyMsgBox("物料目录还不存在，先启动一次游戏，PCL 会把物料下到这里。", "打开物料目录");
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

    private void BtnCopyLog_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(TextLog.Text))
        {
            HintService.Hint("日志是空的，没有可复制的内容");
            return;
        }

        try
        {
            Clipboard.SetText(TextLog.Text);
            HintService.Hint("已复制全部日志");
        }
        catch (Exception ex)
        {
            // 剪贴板偶尔会被别的程序占住，失败要说出来而不是静默
            ModBase.Log(ex, "[Lunar] 复制日志失败", ModBase.LogLevel.Debug);
            HintService.Hint("复制失败：" + ex.Message);
        }
    }

    private void BtnExportLog_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(TextLog.Text))
        {
            HintService.Hint("日志是空的，没有可导出的内容");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出 Lunar 运行日志",
            FileName = $"Lunar-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            DefaultExt = ".log",
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, TextLog.Text);
            HintService.Hint("日志已导出到 " + dialog.FileName);
        }
        catch (Exception ex)
        {
            ModMain.MyMsgBox("写文件失败：" + ex.Message, "导出日志");
        }
    }

    private void AppendLog(string line)
    {
        ModBase.RunInUi(() =>
        {
            // 用户往上翻着看的时候别把他拽回底部，只有本来就在底部才跟着滚
            var atBottom = TextLog.VerticalOffset >= TextLog.ExtentHeight - TextLog.ViewportHeight - 4;
            TextLog.AppendText(line + Environment.NewLine);
            if (atBottom) TextLog.ScrollToEnd();
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
