using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;
using PCL.Core.UI;

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
    private string _moduleSignature = "";
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
        RefreshModuleChips();
        RefreshAccountCombo();
        RefreshAccountHint();

        // 运行中显示进程真正用的目录；没运行就显示「下次会用哪个」
        LabGameDir.Text = ModLunar.IsRunning && !string.IsNullOrWhiteSpace(ModLunar.GameDirectory)
            ? ModLunar.GameDirectory
            : _gameDirPreview;
        LabCacheDir.Text = LunarClientService.MultiverDirectory(ModLunar.LunarRoot);
        LabJava.Text = string.IsNullOrWhiteSpace(ModLunar.JavaPath)
            ? Lang.Text("Lunar.Field.JavaAuto")
            : ModLunar.JavaPath;

        RefreshButtons();
    }

    /// <summary>
    ///     版本下拉框。只在列表内容真的变了才重建 —— 启动过程中状态每秒变好几次，
    ///     每次都重建会把用户的选中状态和展开状态一起冲掉。
    ///     <para>
    ///         选中项则每次都对齐：设置页里也能改版本，改完回到这里不能还显示旧的。
    ///         对齐只改 <c>SelectedItem</c>、不重建列表，所以不会打断展开中的下拉面板。
    ///     </para>
    /// </summary>
    private void RefreshVersionCombo()
    {
        var versions = ModLunar.Versions;
        var signature = versions.Count == 0
            ? "single:" + ModLunar.SelectedSubVersion
            : string.Join(",", versions.Select(x => x.Id));

        if (signature != _versionSignature)
        {
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
                        Tag = ModLunar.SelectedSubVersion
                    });
                }
                else
                {
                    foreach (var version in versions)
                        ComboVersion.Items.Add(new MyComboBoxItem
                        {
                            Content = version.DisplayName,
                            Tag = version.Id,
                            ToolTip = ModLunar.DescribeVersion(version)
                        });
                }
            }
            finally
            {
                _suppressComboEvents = false;
            }
        }

        SyncVersionSelection();
    }

    /// <summary>
    ///     让下拉框的选中项等于 <see cref="ModLunar.SelectedSubVersion" />。
    ///     <para>
    ///         必须在**列表加完之后**显式指定：往空 ComboBox 里逐项 Add 时，WPF 的 Selector
    ///         会自己把第一项选上，而这里的列表是版本号降序的 —— 第一项正好是最新的 drop
    ///         （26.3）。以前靠「Add 之前先设 IsSelected」压不住它，于是界面显示 26.3、
    ///         点启动却按配置里的 1.8.9 走，看起来就是「默认选了预览版」。
    ///     </para>
    /// </summary>
    private void SyncVersionSelection()
    {
        if (ComboVersion.Items.Count == 0) return;

        var wanted = ModLunar.SelectedSubVersion;
        var match = ComboVersion.Items.OfType<MyComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, wanted, StringComparison.OrdinalIgnoreCase));
        var target = match ?? ComboVersion.Items[0];
        if (ReferenceEquals(ComboVersion.SelectedItem, target)) return;

        _suppressComboEvents = true;
        try
        {
            ComboVersion.SelectedItem = target;
        }
        finally
        {
            _suppressComboEvents = false;
        }
    }

    /// <summary>
    ///     Loader 选择：每个版本能选的注入方案不一样（OptiFine / 原版 / Fabric / Forge），
    ///     按服务端下发的 <c>modules</c> 动态生成一排可点击的胶囊按钮 —— 通常只有两三个，
    ///     点一下就换，比下拉框少一次点击。
    ///     <para>
    ///         只在「可选项本身变了」时重建。选中状态变化不重建，否则每次点击都会把刚点中的
    ///         按钮销毁重造，动画和焦点一起丢。
    ///     </para>
    /// </summary>
    private void RefreshModuleChips()
    {
        var modules = ModLunar.Modules;
        // 版本也要进签名：两个版本的 module id 可能一模一样但默认值不同
        // （1.17.1 默认 fabric，别的版本默认 lunar），不带上版本就会沿用上一个版本的高亮
        var signature = ModLunar.SelectedSubVersion + "|" + string.Join(",", modules.Select(x => x.Id));
        if (signature == _moduleSignature) return;
        _moduleSignature = signature;

        PanLoaders.Children.Clear();
        PanLoaders.Visibility = modules.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (modules.Count == 0) return;

        var selected = ModLunar.SelectedModuleId;
        foreach (var module in modules)
        {
            var chip = new MyRadioButton
            {
                Text = module.DisplayName,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = module.Id,
                ToolTip = BuildModuleTooltip(module),
                // 先设 Checked 再挂进面板：Parent 还是空的时候 SetChecked 只记状态、
                // 不广播事件，重建过程不会把用户的配置又改一遍
                Checked = string.Equals(module.Id, selected, StringComparison.OrdinalIgnoreCase)
            };
            chip.Check += ModuleChip_Check;
            PanLoaders.Children.Add(chip);
        }
    }

    private static string BuildModuleTooltip(LunarModule module)
    {
        var text = module.DisplayName;
        if (!string.IsNullOrWhiteSpace(module.Description)) text += "\n" + module.Description;
        if (module.IsDefault) text += "\n" + Lang.Text("Lunar.Module.Default");
        return text + "\nmodule = " + module.Id;
    }

    private void ModuleChip_Check(MyRadioButton sender, bool raiseByMouse)
    {
        // 程序化设置 Checked 时 raiseByMouse 是 false，不能当成用户操作
        if (!raiseByMouse) return;
        if (sender.Tag is not string id || string.IsNullOrWhiteSpace(id)) return;

        ModLunar.SelectModule(id);
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
                ComboAccount.Items.Add(MakeGroupHeader(Lang.Text("Lunar.Account.Group.Pcl")));
                foreach (var profile in pclProfiles)
                    ComboAccount.Items.Add(new MyComboBoxItem
                    {
                        // 下拉框里只写名字，过期状态放到下面那行说明里，免得把右侧挤歪
                        Content = profile.UserName,
                        Tag = ModLunar.SelectionTag(profile),
                        ToolTip = profile.IsExpired
                            ? Lang.Text("Lunar.Account.Tip.PclExpired", profile.Uuid,
                                profile.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "")
                            : Lang.Text("Lunar.Account.Tip.Pcl", profile.Uuid),
                        IsSelected = selectedTag == ModLunar.SelectionTag(profile)
                    });
            }

            ComboAccount.Items.Add(MakeGroupHeader(Lang.Text("Lunar.Account.Group.Lunar")));

            if (accounts.Count == 0)
            {
                ComboAccount.Items.Add(new MyComboBoxItem
                {
                    Content = Lang.Text("Lunar.Account.Empty"),
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
                            ? Lang.Text("Lunar.Account.Tip.LunarExpired", account.Type, account.MinecraftUuid,
                                expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                            : Lang.Text("Lunar.Account.Tip.Lunar", account.Type, account.MinecraftUuid),
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
            text = Lang.Text("Lunar.Account.Hint.LunarExpired",
                       expired.ExpiresAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                   + (pcl is not null
                       ? Lang.Text("Lunar.Account.Hint.WillRefresh", pcl.UserName)
                       : Lang.Text("Lunar.Account.Hint.NoPcl"));
        }
        else if (pclProfile is { IsExpired: true } pcl)
        {
            text = Lang.Text("Lunar.Account.Hint.PclExpired", pcl.UserName,
                pcl.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "");
        }
        else if (pclProfile is not null)
        {
            text = Lang.Text("Lunar.Account.Hint.UsingPcl", pclProfile.UserName);
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
        BtnLaunch.Text = running
            ? Lang.Text("Lunar.Action.Stop")
            : busy
                ? Lang.Text("Lunar.Action.Busy")
                : Lang.Text("Lunar.Launch");
        BtnLaunch.IsEnabled = running || !busy;
        BtnLaunch.ColorType = running ? MyButton.ColorState.Red : MyButton.ColorState.Highlight;

        ComboVersion.IsEnabled = !running && !busy;
        ComboAccount.IsEnabled = !running && !busy;
        PanLoaders.IsEnabled = !running && !busy;
        BtnRefreshVersions.IsEnabled = !running && !busy;
        BtnFocusGame.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnFocusGame_Click(object sender, MouseButtonEventArgs e)
    {
        BtnFocusGame.IsEnabled = false;
        try
        {
            if (await ModLunar.FocusGameAsync())
            {
                HintService.Hint(Lang.Text("Lunar.Hint.Focused"));
                return;
            }

            HintService.Hint(Lang.Text("Lunar.Hint.FocusFailed"), HintType.Warning);
        }
        finally
        {
            BtnFocusGame.IsEnabled = true;
        }
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
        HintService.Hint(Lang.Text("Lunar.Hint.Fetching"));
        try
        {
            await ModLunar.RefreshVersionsAsync();
            _versionSignature = ""; // 强制重建下拉框
            RefreshPage();
            HintService.Hint(Lang.Text("Lunar.Hint.Fetched", ModLunar.Versions.Count));
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

            // 停止是异步的（先请游戏存盘退出，超时才强杀），期间这颗按钮不该还能点
            if (ModLunar.IsRunning) BtnLaunch.IsEnabled = false;
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
            ModMain.MyMsgBox(Lang.Text("Lunar.Msg.NoAssets"), Lang.Text("Lunar.Msg.NoAssets.Title"));
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private void BtnCopyCommand_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ModLunar.LastCommandLine))
        {
            ModMain.MyMsgBox(Lang.Text("Lunar.Msg.NoCommand"), Lang.Text("Lunar.Msg.NoCommand.Title"));
            return;
        }

        // 命令行里有 accessToken，复制出去前先抹掉，免得被顺手贴到群里
        var redacted = RedactToken(ModLunar.LastCommandLine);
        Clipboard.SetText(redacted);
        HintService.Hint(Lang.Text("Lunar.Hint.CommandCopied"));
    }

    private void BtnClearLog_Click(object sender, MouseButtonEventArgs e)
    {
        TextLog.Clear();
    }

    private void BtnCopyLog_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(TextLog.Text))
        {
            HintService.Hint(Lang.Text("Lunar.Hint.LogEmpty"));
            return;
        }

        try
        {
            Clipboard.SetText(TextLog.Text);
            HintService.Hint(Lang.Text("Lunar.Hint.LogCopied"));
        }
        catch (Exception ex)
        {
            // 剪贴板偶尔会被别的程序占住，失败要说出来而不是静默
            ModBase.Log(ex, "[Lunar] 复制日志失败", ModBase.LogLevel.Debug);
            HintService.Hint(Lang.Text("Lunar.Hint.CopyFailed", ex.Message));
        }
    }

    private void BtnExportLog_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(TextLog.Text))
        {
            HintService.Hint(Lang.Text("Lunar.Hint.LogEmpty"));
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Lang.Text("Lunar.Log.ExportTitle"),
            FileName = $"Lunar-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            DefaultExt = ".log",
            Filter = Lang.Text("Lunar.Log.Filter")
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, TextLog.Text);
            HintService.Hint(Lang.Text("Lunar.Hint.LogSaved", dialog.FileName));
        }
        catch (Exception ex)
        {
            ModMain.MyMsgBox(Lang.Text("Lunar.Msg.WriteFailed", ex.Message),
                Lang.Text("Lunar.Msg.ExportTitle"));
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
    ///     预览这次启动会写进哪个目录：和 <see cref="ModLunar" /> 的取值规则保持一致
    ///     （设置里指定过就用指定的，否则用当前 MC 根目录）—— Lunar 不跟实例隔离走。
    /// </summary>
    private static string PreviewGameDirectory()
    {
        return ModLunar.ResolveGameDirectory();
    }

    /// <summary>换启动目录。选完立刻生效，下次启动就用它。</summary>
    private void BtnPickGameDir_Click(object sender, EventArgs e)
    {
        var folder = SystemDialogs.SelectFolder(Lang.Text("Lunar.Tip.PickGameDir"), PreviewGameDirectory());
        if (string.IsNullOrWhiteSpace(folder)) return;

        Config.Preference.Lunar.GameDir = folder;
        _gameDirPreview = folder;
        RefreshPage();
    }

    /// <summary>启动目录恢复默认：跟随当前选中的 MC 根目录。</summary>
    private void BtnResetGameDir_Click(object sender, EventArgs e)
    {
        Config.Preference.Lunar.GameDir = "";
        _gameDirPreview = ModLunar.DefaultGameDirectory;
        RefreshPage();
    }

    /// <summary>
    ///     换物料根目录（相当于官方启动器的 <c>~/.lunarclient</c>）。
    ///     物料、JRE、账户库都挂在这下面，所以换完要把版本列表和账号重新读一遍。
    /// </summary>
    private void BtnPickRoot_Click(object sender, EventArgs e)
    {
        var folder = SystemDialogs.SelectFolder(Lang.Text("Lunar.Tip.PickRoot"), ModLunar.LunarRoot);
        if (string.IsNullOrWhiteSpace(folder)) return;

        Config.Preference.Lunar.Root = folder;
        ResetAfterRootChanged();
    }

    /// <summary>物料目录恢复默认：<c>~/.lunarclient</c>。</summary>
    private void BtnResetRoot_Click(object sender, EventArgs e)
    {
        Config.Preference.Lunar.Root = "";
        ResetAfterRootChanged();
    }

    private void ResetAfterRootChanged()
    {
        // 缓存签名清空，让下拉框按新目录里的内容重建
        _versionSignature = "";
        _accountSignature = "";
        ModLunar.Refresh();
        RefreshPage();
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
