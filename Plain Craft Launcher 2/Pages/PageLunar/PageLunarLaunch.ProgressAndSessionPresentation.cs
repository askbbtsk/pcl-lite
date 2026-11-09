using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;
using PCL.Core.App.Lunar.Social;
using PCL.Core.UI;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageLunarLaunch
{

    /// <summary>
    ///     物料准备进度条：<see cref="ModLunar.InstallPercent" /> 负数表示没有确定进度，
    ///     整条隐藏；非负就按百分比铺填充条。
    /// </summary>
    private void RefreshInstallProgress()
    {
        var percent = ModLunar.InstallPercent;
        if (percent < 0)
        {
            PanInstallProgress.Visibility = Visibility.Collapsed;
            return;
        }

        PanInstallProgress.Visibility = Visibility.Visible;
        RectInstallProgress.Width = Math.Clamp(percent, 0, 100) / 100 * PanInstallProgress.ActualWidth;
    }


    private void Subscribe()
    {
        if (_subscribed) return;
        ModLunar.StateChanged += RefreshPage;
        ModLunar.LogReceived += AppendLog;
        _subscribed = true;
        SubscribeSocial();
        _ = ConnectSocialOnPageOpenedAsync();

        _gameDirPreview = PreviewGameDirectory();
        ModLunar.Refresh();
        RefreshPage();

        // 版本列表可能还是空的（第一次进这个页面），后台拉一次；
        // 拉完会触发 StateChanged，界面自己会更新。
        // 没装 Lunar 也要拉 —— 物料由 PCL 自己下，版本列表是启动前就要用的
        if (ModLunar.Versions.Count == 0)
            _ = ModLunar.RefreshVersionsAsync();
    }


    private async Task ConnectSocialOnPageOpenedAsync()
    {
        if (ModLunar.IsSocialConnected || _socialBusy || ModLunar.IsSocialBusy) return;

        _socialBusy = true;
        RefreshSocial();
        try
        {
            // PageLunarLeft starts the token preheat on the same navigation. Wait for it so
            // refresh-token rotation cannot race the Lunar social login.
            await ModLunar.PrepareSessionAsync();
            if (!IsLoaded || ModLunar.IsSocialConnected || ModLunar.IsSocialBusy) return;
            await ModLunar.ConnectSocialAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "打开 Lunar 页面时自动连接社交服务失败", ModBase.LogLevel.Debug);
            if (IsLoaded) HintService.Hint(ex.Message, HintType.Warning);
        }
        finally
        {
            _socialBusy = false;
            RefreshSocial();
        }
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
        RefreshInstallProgress();

        _brushReady ??= (Brush)FindResource("ColorBrush3");
        _brushIdle ??= (Brush)FindResource("ColorBrushGray4");
        ShapeDot.Fill = ModLunar.IsInstalled ? _brushReady : _brushIdle;

        RefreshVersionCombo();
        RefreshModuleChips();
        RefreshAccountCombo();
        RefreshAccountHint();

        // 运行中显示进程真正用的目录；没运行就显示「下次会用哪个」
        // 这三行是直接赋路径（没走 Lang.Text），显示前同样要按设置打码
        LabGameDir.Text = UserPathMasker.Apply(ModLunar.IsRunning && !string.IsNullOrWhiteSpace(ModLunar.GameDirectory)
            ? ModLunar.GameDirectory
            : _gameDirPreview);
        LabCacheDir.Text = UserPathMasker.Apply(LunarClientService.MultiverDirectory(ModLunar.LunarRoot));
        LabJava.Text = string.IsNullOrWhiteSpace(ModLunar.JavaPath)
            ? Lang.Text("Lunar.Field.JavaAuto")
            : UserPathMasker.Apply(ModLunar.JavaPath);

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
    ///     账号下拉框：分「Lunar 身份账号」和「Lunar 启动器账号」两组。
    ///     <para>
    ///         分组头是禁用项，点不动也选不上，只用来在列表里画一条分界。真正能选的那些，
    ///         <c>Tag</c> 带着 <c>identity:</c> / <c>lunar:</c> 前缀，选完原样存进配置。
    ///     </para>
    ///     <para>
    ///         每一项都是两行：上面账号名，下面 UUID。同名的号一眼分不清，
    ///         UUID 才是一对一能对上的那个。
    ///     </para>
    /// </summary>
    private void RefreshAccountCombo()
    {
        var identities = ModLunar.LunarIdentityAccounts;
        var accounts = ModLunar.Accounts;

        // 配置里存的是带前缀的 Tag。历史上有两种残留要归一化，否则下拉框一项都对不上，
        // 看起来就是「选中的账号没了」：pcl:<ProfileId>（已取消的那组）和裸 localId（1.5.0 之前）
        var selectedTag = Config.Preference.Lunar.AccountLocalId ?? "";
        if (selectedTag.StartsWith("pcl:", StringComparison.OrdinalIgnoreCase))
            selectedTag = "";
        else if (!string.IsNullOrWhiteSpace(selectedTag)
                 && !selectedTag.StartsWith(ModLunar.IdentitySelectionPrefix, StringComparison.OrdinalIgnoreCase)
                 && !selectedTag.StartsWith("lunar:", StringComparison.OrdinalIgnoreCase))
            selectedTag = "lunar:" + selectedTag;

        // 选中项也要进签名：换账号时要重建，否则下拉框显示的还是旧的
        var signature = string.Join(",", identities.Select(x => x.Uuid + (x.IsExpired ? "!" : "")))
                        + "|" + string.Join(",", accounts.Select(x => x.LocalId + (x.IsExpired ? "!" : "")))
                        + "#" + selectedTag;
        if (signature == _accountSignature) return;
        _accountSignature = signature;

        _suppressComboEvents = true;
        try
        {
            ComboAccount.Items.Clear();

            // 第一组：Lunar 身份账号。只来自本页的「登录」，和 PCL 主页面的账号列表无关
            ComboAccount.Items.Add(MakeGroupHeader(Lang.Text("Lunar.Account.Group.Identity")));
            if (identities.Count == 0)
            {
                ComboAccount.Items.Add(new MyComboBoxItem
                {
                    Content = Lang.Text("Lunar.Account.Identity.Empty"),
                    Tag = "",
                    IsEnabled = false,
                    IsSelected = string.IsNullOrEmpty(selectedTag)
                });
            }
            else
            {
                foreach (var identity in identities)
                    ComboAccount.Items.Add(MakeAccountItem(
                        identity.DisplayName,
                        FormatUuid(identity.Uuid),
                        ModLunar.SelectionTag(identity),
                        identity.IsExpired
                            ? Lang.Text("Lunar.Account.Tip.IdentityExpired", identity.Uuid,
                                identity.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "")
                            : Lang.Text("Lunar.Account.Tip.Identity", identity.Uuid),
                        selectedTag == ModLunar.SelectionTag(identity)));
            }

            // 第二组：Lunar 启动器自己的账户库
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
                    ComboAccount.Items.Add(MakeAccountItem(
                        account.DisplayName,
                        FormatUuid(account.MinecraftUuid),
                        ModLunar.SelectionTag(account),
                        account.IsExpired && account.ExpiresAt is { } expires
                            ? Lang.Text("Lunar.Account.Tip.LunarExpired", account.Type, account.MinecraftUuid,
                                expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                            : Lang.Text("Lunar.Account.Tip.Lunar", account.Type, account.MinecraftUuid),
                        selectedTag == ModLunar.SelectionTag(account)));
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


    /// <summary>
    ///     下拉框里的一个账号项：上面账号名，下面 UUID。
    ///     <para>
    ///         内容是 <see cref="AccountComboEntry" /> 而不是现做的控件 —— 控件只能有一个父级，
    ///         下拉列表和收起后的显示格是两处，共用同一个控件会有一边空掉。
    ///         模板见 XAML 里的 <c>LunarAccountItemTemplate</c>。
    ///     </para>
    ///     <para>
    ///         <c>Tag</c> 必须留成字符串 —— 选中处理靠它区分 <c>identity:</c> / <c>lunar:</c>。
    ///     </para>
    /// </summary>
    private MyComboBoxItem MakeAccountItem(string title, string subtitle, string tag, string? toolTip,
        bool selected) => new()
    {
        Content = new AccountComboEntry(title, subtitle),
        ContentTemplate = (DataTemplate)FindResource("LunarAccountItemTemplate"),
        Tag = tag,
        ToolTip = toolTip,
        IsSelected = selected
    };


    /// <summary>把 32 位裸 UUID 补成带横线的形式，和别处看到的 UUID 对得上。</summary>
    private static string FormatUuid(string? uuid)
    {
        var raw = (uuid ?? "").Replace("-", string.Empty);
        return raw.Length == 32
            ? $"{raw[..8]}-{raw[8..12]}-{raw[12..16]}-{raw[16..20]}-{raw[20..]}"
            : uuid ?? "";
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
        var identity = ModLunar.SelectedIdentityAccount;
        var account = ModLunar.SelectedAccount;

        string text;
        if (identity is { IsExpired: true } expiredIdentity)
        {
            text = Lang.Text("Lunar.Account.Hint.IdentityExpired", expiredIdentity.DisplayName,
                expiredIdentity.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "");
        }
        else if (identity is not null)
        {
            text = Lang.Text("Lunar.Account.Hint.UsingIdentity", identity.DisplayName);
        }
        else if (account is { IsExpired: true } expired)
        {
            var pcl = ModLunar.MatchPclProfile(expired);
            text = Lang.Text("Lunar.Account.Hint.LunarExpired",
                       expired.ExpiresAt!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                   + (pcl is not null
                       ? Lang.Text("Lunar.Account.Hint.WillRefresh", pcl.UserName)
                       : Lang.Text("Lunar.Account.Hint.NoPcl"));
        }
        else
        {
            text = "";
        }

        LabAccountHint.Text = text;
        LabAccountHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }


    /// <summary>
    ///     用 Lunar 的 client id 登录一个「Lunar 身份账号」。
    ///     <para>
    ///         这是 Lunar 页面专属的登录，<b>和 PCL 主页面的账号列表完全无关</b>：
    ///         产出的账号只写进身份库，主页面看不到；反过来主页面的账号也不会出现在这里。
    ///         必须走 Lunar 的应用身份，游戏端才认得出来（它按 JWT 的 aid 选刷新实现）。
    ///     </para>
    /// </summary>
    private void BtnLoginIdentity_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModLunar.IsBusy || ModLunar.IsRunning)
        {
            HintService.Hint(Lang.Text("Lunar.Account.Login.Busy"), HintType.Warning);
            return;
        }

        BtnLoginIdentity.IsEnabled = false;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var account = ModLunar.LoginLunarIdentityAsync().GetAwaiter().GetResult();
                if (account is null) return; // 用户取消，ModLunar 里已经记过日志
                ModBase.RunInUi(() =>
                {
                    _accountSignature = "";
                    RefreshAccountCombo();
                    RefreshAccountHint();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Lunar] 登录 Lunar 身份账号失败", ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Lunar.Account.Login.Failed"));
            }
            finally
            {
                ModBase.RunInUi(() => BtnLoginIdentity.IsEnabled = true);
            }
        }, "Lunar Identity Login");
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
        BtnLoginIdentity.IsEnabled = !running && !busy;
        PanLoaders.IsEnabled = !running && !busy;
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
        // 左栏显示的是「这次启动用哪个号」，换选中就得让它跟着变，
        // 否则右边选完、左边还写着上一个（或者写着「未找到」）
        ModLunar.Refresh();
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


    /// <summary>
    /// 打开 GitHub 上的 Lunar 说明文档。
    /// <para>
    /// 页面里只留一句概述，细节全在 docs/LUNAR.md —— 那块逻辑改得勤，
    /// 写在界面上必然过时，指向文档才能「改一次全端一致」。
    /// </para>
    /// </summary>
    private void BtnLunarDocs_Click(object sender, EventArgs e) => ModBase.OpenWebsite(ProjectLinks.LunarDocs);


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

}
