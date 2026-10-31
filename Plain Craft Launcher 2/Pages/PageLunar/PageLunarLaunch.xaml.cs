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
using PCL.Core.App.Lunar.Social;
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
        SubscribeSocial();

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
        UnsubscribeSocial();
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

    #region 好友与私聊

    private IReadOnlyList<LunarFriend> _friends = [];
    private LunarFriend? _selectedFriend;
    private string _selectedConversation = "";
    private bool _socialSubscribed;
    private bool _socialBusy;

    /// <summary>
    ///     好友头像的接口。这是 Lunar 官方启动器自己用的那个（<c>skins.mcstats.com</c>），
    ///     直连可通、无皮肤时回默认皮肤，不会 404。
    /// </summary>
    private const string AvatarEndpoint = "https://skins.mcstats.com/face/";

    /// <summary>UUID → 本地缓存路径。<c>null</c> 表示下过但失败了，别再试。</summary>
    private readonly System.Collections.Generic.Dictionary<string, string?> _avatars =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     上一条消息的日期，用来决定要不要插一条日期分隔。
    ///     <para>默认值（<c>0001-01-01</c>）表示「还一条都没画」，第一条消息必定插分隔。</para>
    /// </summary>
    private DateOnly _lastMessageDate;

    /// <summary>订阅 <see cref="ModLunar" /> 的社交事件。页面每次显示都要重新挂。</summary>
    private void SubscribeSocial()
    {
        if (_socialSubscribed) return;
        ModLunar.SocialStateChanged += RefreshSocial;
        ModLunar.SocialFriendsChanged += RefreshSocial;
        ModLunar.SocialMessageReceived += OnSocialMessage;
        _socialSubscribed = true;
        RefreshSocial();
    }

    private void UnsubscribeSocial()
    {
        if (!_socialSubscribed) return;
        ModLunar.SocialStateChanged -= RefreshSocial;
        ModLunar.SocialFriendsChanged -= RefreshSocial;
        ModLunar.SocialMessageReceived -= OnSocialMessage;
        _socialSubscribed = false;
    }

    private void RefreshSocial()
    {
        // 事件是从连接线程上抛出来的，必须切回 UI 线程再动控件
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshSocial));
            return;
        }

        if (!IsLoaded) return;

        var client = ModLunar.Social;
        var connected = ModLunar.IsSocialConnected;

        LabSocialStatus.Text = _socialBusy || ModLunar.IsSocialBusy
            ? Lang.Text("Lunar.Social.Connecting")
            : connected
                ? Lang.Text("Lunar.Social.Online")
                : Lang.Text("Lunar.Social.Offline");

        LabSocialDetail.Text = connected
            ? Lang.Text("Lunar.Social.OnlineDesc", client?.Friends.Count ?? 0)
            : Lang.Text("Lunar.Social.OfflineDesc");

        BtnSocialConnect.Text = connected
            ? Lang.Text("Lunar.Social.Disconnect")
            : Lang.Text("Lunar.Social.Connect");
        BtnSocialConnect.IsEnabled = !(_socialBusy || ModLunar.IsSocialBusy);
        BtnSocialRefresh.IsEnabled = connected;

        _friends = client?.Friends ?? [];
        RebuildFriendRows();
    }

    private void RebuildFriendRows()
    {
        PanFriends.Children.Clear();
        LabFriendsEmpty.Visibility = _friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var friend in _friends.OrderByDescending(f => f.IsOnline).ThenBy(f => f.Name))
        {
            var detail = friend.IsOnline
                ? string.IsNullOrEmpty(friend.Where)
                    ? Lang.Text("Lunar.Social.WhereOnline")
                    : friend.Where
                : Lang.Text("Lunar.Social.WhereOffline");

            var item = new MyListItem
            {
                Title = friend.Name,
                Info = detail,
                Type = MyListItem.CheckType.RadioBox,
                Height = 38,
                Margin = new Thickness(0, 0, 0, 2),
                Tag = friend,
                LogoCornerRadius = new CornerRadius(5),
                Checked = _selectedFriend?.Uuid == friend.Uuid
            };
            ApplyAvatar(item, friend);
            item.Check += FriendItem_Check;
            PanFriends.Children.Add(item);
        }
    }

    /// <summary>
    ///     给一行塞头像：有缓存就直出，没有就先挂 SVG 图标并排队下载。
    ///     <para>
    ///         头像走 <see cref="MyImage" /> 那套缓存（<c>Cache/Images/&lt;md5&gt;.png</c>），
    ///         所以第二次进页面是本地读，不会每次都打网络。
    ///     </para>
    /// </summary>
    private void ApplyAvatar(MyListItem item, LunarFriend friend)
    {
        var path = ResolveAvatar(friend);
        if (path is null)
        {
            item.SvgIcon = friend.IsOnline ? "lucide/user-round" : "lucide/user";
            item.Logo = string.Empty;
            return;
        }

        item.SvgIcon = string.Empty;
        item.Logo = path;
    }

    /// <summary>拿本地头像路径；没有就返回 <see langword="null" /> 并顺手排一个下载。</summary>
    private string? ResolveAvatar(LunarFriend friend)
    {
        if (friend.Uuid.Length == 0) return null;
        if (_avatars.TryGetValue(friend.Uuid, out var known)) return known;

        var url = AvatarUrl(friend);
        var path = MyImage.GetTempPath(url);
        if (File.Exists(path) && new FileInfo(path).Length > 0L)
        {
            _avatars[friend.Uuid] = path;
            return path;
        }

        // 先记成「没有」，免得同一轮重建里反复排队
        _avatars[friend.Uuid] = null;
        _ = DownloadAvatarAsync(friend.Uuid, url);
        return null;
    }

    private async Task DownloadAvatarAsync(string uuid, string url)
    {
        string path;
        try
        {
            path = await MyImage.DownloadImageAsync(url).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Lunar] 下载好友头像失败（{uuid}）", ModBase.LogLevel.Debug);
            path = string.Empty;
        }

        if (string.IsNullOrEmpty(path))
        {
            // 失败就把「没有」这条记录清掉，下次刷新还能再试一次；
            // 留着的话会一直显示图标，直到重启 PCL
            await Dispatcher.BeginInvoke(new Action(() => _avatars.Remove(uuid)));
            return;
        }

        await Dispatcher.BeginInvoke(new Action(() =>
        {
            _avatars[uuid] = path;

            // 只换那一行的图，不整表重建 —— 重建会把用户正在点的选中态一起刷掉
            foreach (var row in PanFriends.Children.OfType<MyListItem>())
            {
                if (row.Tag is not LunarFriend friend || friend.Uuid != uuid) continue;
                row.SvgIcon = string.Empty;
                row.Logo = path;
                break;
            }
        }));
    }

    private static string AvatarUrl(LunarFriend friend) =>
        AvatarEndpoint + friend.Uuid.Replace("-", "", StringComparison.Ordinal) + "?size=64";

    private async void FriendItem_Check(object sender, ModBase.RouteEventArgs e)
    {
        if (sender is not MyListItem { Tag: LunarFriend friend }) return;
        await OpenChatAsync(friend);
    }

    private async Task OpenChatAsync(LunarFriend friend)
    {
        _selectedFriend = friend;
        _selectedConversation = "";
        PanChat.Visibility = Visibility.Visible;
        LabChatTitle.Text = friend.Name;
        PanMessages.Children.Clear();
        _lastMessageDate = default;

        var client = ModLunar.Social;
        if (client is null || !client.IsConnected)
        {
            AddSystemLine(Lang.Text("Lunar.Social.NeedConnect"));
            return;
        }

        AddSystemLine(Lang.Text("Lunar.Social.Loading"));

        try
        {
            _selectedConversation = await client.OpenConversationAsync(friend.Uuid);
            var messages = await client.LoadMessagesAsync(friend.Uuid, _selectedConversation);
            PanMessages.Children.Clear();
            _lastMessageDate = default;
            foreach (var message in messages) AddMessageRow(message);
            if (messages.Count == 0) AddSystemLine(Lang.Text("Lunar.Social.NoMessages"));
            PanChatScroll.ScrollToEnd();
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException)
        {
            PanMessages.Children.Clear();
            _lastMessageDate = default;
            AddSystemLine(ex.Message);
        }
    }

    private void OnSocialMessage(string conversationId, LunarChatMessage message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSocialMessage(conversationId, message)));
            return;
        }

        if (conversationId != _selectedConversation) return;
        AddMessageRow(message);
        PanChatScroll.ScrollToEnd();
    }

    private void AddSystemLine(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Opacity = 0.55,
            Margin = new Thickness(2, 2, 2, 6)
        };
        PanMessages.Children.Add(block);
    }

    /// <summary>
    ///     一行消息。自己发的靠右、用主题色底 + 反白字；别人的靠左、用透明底。
    ///     <para>文字颜色都显式给，深色主题下才不会出现黑字黑底。</para>
    ///     <para>
    ///         跨天的第一条消息前面插一条日期分隔。以前只显示 <c>HH:mm</c>，
    ///         昨天的消息和上个月的消息看起来一模一样，完全分不出是哪天说的。
    ///     </para>
    /// </summary>
    private void AddMessageRow(LunarChatMessage message)
    {
        var local = message.SentAt.ToLocalTime();
        var date = DateOnly.FromDateTime(local.DateTime);

        if (date != _lastMessageDate)
        {
            AddDateSeparator(date);
            _lastMessageDate = date;
        }

        // 当天只留时分，别的日子把日期带上 —— 隔天往上翻的时候不用再找分隔条
        var today = DateOnly.FromDateTime(DateTime.Now);
        var stamp = date == today
            ? local.ToString("HH:mm")
            : date.Year == today.Year
                ? local.ToString("M/d HH:mm")
                : local.ToString("yyyy/M/d HH:mm");

        var caption = message.Mine
            ? Lang.Text("Lunar.Social.Me") + " · " + stamp
            : message.SenderName + " · " + stamp;

        var stack = new StackPanel();
        var captionBlock = new TextBlock
        {
            Text = caption,
            FontSize = 10.5,
            Opacity = 0.55,
            Margin = new Thickness(2, 0, 2, 2),
            HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        captionBlock.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        stack.Children.Add(captionBlock);

        var text = new TextBlock
        {
            Text = message.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, message.Mine ? "ColorBrush8" : "ColorBrush1");

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 6, 10, 6),
            MaxWidth = 360,
            HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = text
        };
        bubble.SetResourceReference(Border.BackgroundProperty,
            message.Mine ? "ColorBrush3" : "ColorBrushTransparentBackground");

        // 对方的气泡原来是「透明底 + 透明面板」，等于没有气泡。改成描一圈细边，
        // 深浅主题下都看得见，也不用再赌一个底色跟面板撞不撞。
        if (!message.Mine)
        {
            bubble.BorderThickness = new Thickness(1);
            bubble.SetResourceReference(Border.BorderBrushProperty, "ColorBrush5");
        }

        stack.Children.Add(bubble);

        stack.Margin = new Thickness(0, 0, 0, 8);
        PanMessages.Children.Add(stack);
    }

    /// <summary>
    ///     居中一条日期分隔，形如「2026/10/31」。
    ///     <para>用 <c>yyyy/M/d</c> 而不是本地化长日期：它在四种语言下都读得懂，也省一个翻译键。</para>
    /// </summary>
    private void AddDateSeparator(DateOnly date)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lineLeft = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        lineLeft.SetResourceReference(Border.BackgroundProperty, "ColorBrush5");
        Grid.SetColumn(lineLeft, 0);

        var label = new TextBlock
        {
            Text = date.ToString("yyyy/M/d"),
            FontSize = 10.5,
            Opacity = 0.45
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        Grid.SetColumn(label, 1);

        var lineRight = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        lineRight.SetResourceReference(Border.BackgroundProperty, "ColorBrush5");
        Grid.SetColumn(lineRight, 2);

        grid.Children.Add(lineLeft);
        grid.Children.Add(label);
        grid.Children.Add(lineRight);
        PanMessages.Children.Add(grid);
    }

    private async void BtnSocialConnect_Click(object sender, MouseButtonEventArgs e)
    {
        if (_socialBusy) return;

        if (ModLunar.IsSocialConnected)
        {
            _socialBusy = true;
            RefreshSocial();
            try
            {
                await ModLunar.DisconnectSocialAsync();
            }
            finally
            {
                _socialBusy = false;
                PanChat.Visibility = Visibility.Collapsed;
                RefreshSocial();
            }

            return;
        }

        _socialBusy = true;
        RefreshSocial();
        try
        {
            await ModLunar.ConnectSocialAsync();
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException)
        {
            HintService.Hint(ex.Message, HintType.Warning);
        }
        finally
        {
            _socialBusy = false;
            RefreshSocial();
        }
    }

    private async void BtnSocialRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        var client = ModLunar.Social;
        if (client is null || !client.IsConnected) return;

        BtnSocialRefresh.IsEnabled = false;
        try
        {
            await client.LoadFriendsAsync();
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException)
        {
            HintService.Hint(ex.Message, HintType.Warning);
        }
        finally
        {
            BtnSocialRefresh.IsEnabled = true;
            RefreshSocial();
        }
    }

    private async void BtnChatSend_Click(object sender, MouseButtonEventArgs e) => await SendChatAsync();

    private async void TxtChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        var text = TxtChatInput.Text.Trim();
        if (text.Length == 0) return;
        if (_selectedFriend is null || _selectedConversation.Length == 0) return;

        var client = ModLunar.Social;
        if (client is null || !client.IsConnected)
        {
            HintService.Hint(Lang.Text("Lunar.Social.NeedConnect"), HintType.Warning);
            return;
        }

        TxtChatInput.Text = "";
        BtnChatSend.IsEnabled = false;
        try
        {
            await client.SendMessageAsync(_selectedFriend.Uuid, _selectedConversation, text);
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException)
        {
            // 发失败就把内容放回去，别让用户白打一遍
            TxtChatInput.Text = text;
            HintService.Hint(ex.Message, HintType.Warning);
        }
        finally
        {
            BtnChatSend.IsEnabled = true;
        }
    }

    #endregion
}
