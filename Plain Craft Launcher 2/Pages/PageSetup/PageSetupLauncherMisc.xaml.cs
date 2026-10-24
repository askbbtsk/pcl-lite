using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.IoC;
using PCL.Core.IO.Net.Consent;
using PCL.Core.Link.Discord;
using PCL.Core.Mcp;
using PCL.Core.UI;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageSetupLauncherMisc
{
    private bool isFirstLoad = true;

    private new bool isLoaded;

    public PageSetupLauncherMisc()
    {
        // XAML 解析期间控件的默认值会触发 Change 并写回配置（复选框默认勾选），
        // 这会把「隐私模式」等开关在用户没动过的情况下改成开启。
        // 因此整个初始化阶段屏蔽控件回写。
        ModAnimation.AniControlEnabled += 1;
        try
        {
            InitializeComponent();
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }

        Loaded += PageSetupLink_Loaded;
        Unloaded += (_, _) => _StopDiscordStatusTimer();
        Loaded += (_, _) =>
        {
            ModAnimation.AniControlEnabled += 1;
            try
            {
                Reload();
            }
            finally
            {
                ModAnimation.AniControlEnabled -= 1;
            }
        };
    }

    private void PageSetupLink_Loaded(object sender, RoutedEventArgs e)
    {
        // 重复加载部分
        PanBack.ScrollToHome();

        // 非重复加载部分
        if (isLoaded)
            return;
        isLoaded = true;

        ModAnimation.AniControlEnabled += 1;
        SliderLoad();
        Reload();
        ModAnimation.AniControlEnabled -= 1;
    }

    public void Reload()
    {
        // 系统设置
        ComboSystemActivity.SelectedIndex = States.System.AnnounceSolution;
        CheckSystemDisableHardwareAcceleration.Checked = Config.System.DisableHardwareAcceleration;
        SliderAniFPS.Value = Config.System.AnimationFpsLimit;
        SliderMaxLog.Value = Config.System.MaxGameLog;

        // 网络
        TextSystemHttpProxy.Text = Config.Network.HttpProxy.CustomAddress;
        TextSystemHttpProxyCustomUsername.Text = Config.Network.HttpProxy.CustomUsername;
        TextSystemHttpProxyCustomPassword.Text = Config.Network.HttpProxy.CustomPassword;
        ((MyRadioBox)FindName($"RadioHttpProxyType{Config.Network.HttpProxy.Type}")).SetChecked(true, false);
        CheckNetDohEnable.Checked = Config.Network.EnableDoH;
        CheckSystemPrivacyMode.Checked = Config.System.PrivacyMode;
        CheckAllowAllConsent.Checked = Config.Network.ConsentAllowAll;
        BuildConsentList();

        // 调试选项
        SliderDebugAnim.Value = Config.Debug.AnimationSpeed;
        CheckDebugSkipCopy.Checked = Config.Debug.DontCopy;
        CheckDebugMode.Checked = Config.Debug.Enabled;
        CheckDebugDelay.Checked = Config.Debug.AddRandomDelay;

        // MCP 服务
        CheckMcpEnabled.Checked = Config.Mcp.Enabled;
        TextMcpPort.Text = Config.Mcp.Port.ToString();
        TextMcpToken.Text = Config.Mcp.Token;
        CheckMcpAllowWrite.Checked = Config.Mcp.AllowWrite;
        CheckMcpAllowGame.Checked = Config.Mcp.AllowGameControl;
        CheckMcpAllowConfig.Checked = Config.Mcp.AllowLauncherConfig;
        UpdateMcpVisibility();

        // Discord 状态显示
        CheckDiscordEnabled.Checked = Config.Discord.Enabled;
        TextDiscordAppId.Text = Config.Discord.AppId;
        var discordFields = (Config.Discord.Fields ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        CheckDiscordLauncherVersion.Checked = discordFields.Contains(DiscordRpcFields.LauncherVersion);
        CheckDiscordLauncherMemory.Checked = discordFields.Contains(DiscordRpcFields.LauncherMemory);
        CheckDiscordGameVersion.Checked = discordFields.Contains(DiscordRpcFields.GameVersion);
        CheckDiscordGameMemory.Checked = discordFields.Contains(DiscordRpcFields.GameMemory);
        CheckDiscordPlayTime.Checked = discordFields.Contains(DiscordRpcFields.PlayTime);
        CheckDiscordServer.Checked = discordFields.Contains(DiscordRpcFields.ServerAddress);
        UpdateDiscordVisibility();
    }

    // 初始化
    public void Reset()
    {
        try
        {
            Config.Network.Reset();
            Config.Debug.Reset();
            Config.System.Reset();
            ModBase.Log("[Setup] 已初始化启动器-杂项页设置");
            HintService.Hint(Lang.Text("Setup.Misc.Initialized"), HintType.Success, false);
            Reload();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Setup.Misc.Error.InitFailed"),
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Setup.Misc.Error.InitFailed"));
        }

        Reload();
    }

    // 将控件改变路由到设置改变
    private void ComboChange(object senderRaw, SelectionChangedEventArgs e)
    {
        var sender = (MyComboBox)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.SelectedIndex);
    }

    private void RadioBoxChange(object senderRaw, ModBase.RouteEventArgs e)
    {
        var sender = (MyRadioBox)senderRaw;
        var gotCfg = sender.Tag?.ToString()?.Split("/") ?? Array.Empty<string>();
        if (ModAnimation.AniControlEnabled == 0 && gotCfg.Length >= 2)
            SetByTag(gotCfg[0], int.Parse(gotCfg[1]));
    }

    private void CheckBoxChange(object senderRaw, bool user)
    {
        var sender = (MyCheckBox)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.Checked);
    }

    private void SliderChange(object senderRaw, bool user)
    {
        var sender = (MySlider)senderRaw;
        if (ModAnimation.AniControlEnabled == 0)
            SetByTag(sender.Tag?.ToString(), sender.Value);
    }

    private static void SetByTag(string tag, object value)
        => ConfigService.TrySetValue(tag, value);

    // 网络
    private void ApplyHttpProxyBtn_OnClicked(object sender, MouseButtonEventArgs e)
    {
        Config.Network.HttpProxy.CustomAddress = TextSystemHttpProxy.Text;
        Config.Network.HttpProxy.CustomUsername = TextSystemHttpProxyCustomUsername.Text;
        Config.Network.HttpProxy.CustomPassword = TextSystemHttpProxyCustomPassword.Text;
    }

    #region 网络白名单

    /// <summary>
    ///     重建网络白名单列表。每个用途一行，显示依赖域名数量与永久授权状态。
    /// </summary>
    private void BuildConsentList()
    {
        PanConsentList.Children.Clear();
        var allowed = NetworkConsentService.GetPersistedAllowed();

        foreach (var info in NetworkPurposeCatalog.All.Where(x => x.Purpose != NetworkPurpose.Unknown))
        {
            var isAllowed = allowed.Contains(info.Id);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Text = $"{info.TitleZh}　{info.Hosts.Length} 个域名"
            };
            label.SetResourceReference(TextBlock.ForegroundProperty,
                isAllowed ? "ColorBrush1" : "ColorBrushGray2");

            var toggle = new MyTextButton
            {
                Text = isAllowed ? "撤销" : "允许",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            toggle.Click += (_, _) =>
            {
                if (isAllowed)
                    NetworkConsentService.Revoke(info.Purpose);
                else
                    NetworkConsentService.AllowPermanently(info.Purpose);
                BuildConsentList();
                HintService.Hint(
                    $"已{(isAllowed ? "撤销" : "允许")}「{info.TitleZh}」的网络访问",
                    HintType.Success, false);
            };

            Grid.SetColumn(toggle, 1);
            row.Children.Add(label);
            row.Children.Add(toggle);
            PanConsentList.Children.Add(row);
        }

        UpdateConsentVisibility();
    }

    /// <summary>
    ///     授权全部连接。
    ///     <para>
    ///     不走 <c>SetByTag</c>：该路径经 <c>ConfigService.TrySetValue</c> 写入会静默失败，
    ///     因此直接赋值给强类型配置项。
    ///     </para>
    /// </summary>
    private void AllowAllConsent_Change(object senderRaw, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        Config.Network.ConsentAllowAll = ((MyCheckBox)senderRaw).Checked == true;
        UpdateConsentVisibility();
    }

    /// <summary>
    ///     全部放行时，逐个用途的清单没有意义，直接收起来。
    /// </summary>
    private void UpdateConsentVisibility()
    {
        var allowAll = Config.Network.ConsentAllowAll;
        var granular = allowAll ? Visibility.Collapsed : Visibility.Visible;
        PanConsentList.Visibility = granular;
        PanConsentActions.Visibility = granular;
        LabConsentGranularHint.Visibility = granular;
        LabConsentAllowAllHint.Visibility = allowAll ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetConsentBtn_OnClicked(object sender, MouseButtonEventArgs e)
    {
        if (ModMain.MyMsgBox("确定要清空全部网络白名单吗？\n清空后，每个用途都会在下一次联网前重新询问。",
                "清空网络白名单", "清空", "取消", isWarn: true) != 1)
            return;

        NetworkConsentService.ResetAll();
        BuildConsentList();
        HintService.Hint("网络白名单已清空", HintType.Success, false);
    }

    #endregion

    // 滑动条
    private void SliderLoad()
    {
        SliderDebugAnim.getHintText = new Func<object, object>(v =>
            (int)v > 29
                ? Lang.Text("Common.Action.Close")
                : Lang.Number(Math.Round(Convert.ToDouble(v) / 10 + 0.1d, 1), "N1") + "x");
        SliderAniFPS.getHintText = new Func<object, string>(v => Lang.Number(Convert.ToInt32(v) + 1, "N0") + " FPS");
        // y = 10x + 50 (0 <= x <= 5, 50 <= y <= 100)
        // y = 50x - 150 (5 < x <= 13, 100 < y <= 500)
        // y = 100x - 800 (13 < x <= 28, 500 < y <= 2000)
        SliderMaxLog.getHintText = new Func<object, object>(v =>
        {
            var val = Convert.ToInt32(v);
            return val switch
            {
                <= 5 => val * 10 + 50,
                <= 13 => val * 50 - 150,
                <= 28 => val * 100 - 800,
                _ => Lang.Text("Setup.Misc.System.MaxLogLines.Unlimited")
            };
        });
    }

    // 硬件加速
    private void Check_DisableHardwareAcceleration(object _, bool __)
    {
        HintService.Hint(Lang.Text("Setup.Misc.HardwareAcceleration.RestartNotice"));
    }

    // 调试模式
    private void CheckDebugMode_Change(object _, bool __)
    {
        if (ModAnimation.AniControlEnabled == 0)
            HintService.Hint(Lang.Text("Setup.Misc.Debug.Mode.Hint"), log: false);
    }

    // 自动更新
    private void ComboSystemActivity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;
        if (ComboSystemActivity.SelectedIndex != 2)
            return;
        if (ModMain.MyMsgBox(
                Lang.Text("Setup.Misc.System.Announcement.Disabled.Warning.Message"),
                Lang.Text("Common.Dialog.Warning"),
                Lang.Text("Setup.Misc.System.Announcement.Disabled.Warning.Confirm"),
                Lang.Text("Common.Action.Cancel"), isWarn: true) ==
            2) ComboSystemActivity.SelectedItem = e.RemovedItems[0];
    }

    private void CheckDebugMode_OnChange(object sender, bool user)
    {
        CheckBoxChange(sender, user);
        CheckDebugMode_Change(sender, user);
    }

    private void CheckSystemDisableHardwareAcceleration_OnChange(object sender, bool user)
    {
        CheckBoxChange(sender, user);
        Check_DisableHardwareAcceleration(sender, user);
    }

    private void ComboSystemActivity_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ComboChange(sender, e);
        ComboSystemActivity_SelectionChanged(sender, e);
    }

    #region MCP 服务

    /// <summary>
    ///     总开关。关掉时把下面的端口、口令、权限全部收起来，避免误以为还在生效。
    /// </summary>
    private void McpEnabled_Change(object senderRaw, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        Config.Mcp.Enabled = ((MyCheckBox)senderRaw).Checked == true;
        UpdateMcpVisibility();
    }

    private void UpdateMcpVisibility()
    {
        var enabled = Config.Mcp.Enabled;
        PanMcpOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        LabMcpDisabledHint.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (enabled) UpdateMcpStatus();
    }

    /// <summary>
    ///     显示服务真实状态。服务在后台线程启停，所以这里只做快照读取，不主动等待。
    /// </summary>
    private void UpdateMcpStatus()
    {
        var options = McpServerService.Snapshot();

        if (McpServerService.IsRunning)
        {
            var auth = options.AuthRequired ? "需要口令" : "无口令，本机任意程序可连";
            var started = McpServerService.StartedAt?.ToString("HH:mm:ss") ?? "";
            LabMcpStatus.Text =
                $"状态：运行中 · {McpServerService.EndpointUrl(options)}（{auth}）"
                + (started.Length > 0 ? $"\n启动于 {started}" : "");
            LabMcpStatus.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
            return;
        }

        if (!string.IsNullOrWhiteSpace(McpServerService.LastError))
        {
            LabMcpStatus.Text = $"状态：启动失败 —— {McpServerService.LastError}";
            LabMcpStatus.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushRedLight");
            return;
        }

        LabMcpStatus.Text = Config.Mcp.Enabled ? "状态：正在启动…" : "状态：未启动";
        LabMcpStatus.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
    }

    private void McpPort_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        if (!int.TryParse(TextMcpPort.Text.Trim(), out var port)) return;
        if (port is < 1024 or > 65535) return;
        if (Config.Mcp.Port == port) return;
        Config.Mcp.Port = port;
        UpdateMcpStatus();
    }

    /// <summary>
    ///     口令即时生效：服务端每次请求都会重新读一次配置，所以不需要重启。
    /// </summary>
    private void McpToken_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        var token = TextMcpToken.Text.Trim();
        if (Config.Mcp.Token == token) return;
        Config.Mcp.Token = token;
        UpdateMcpStatus();
    }

    private void McpPermission_Change(object sender, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        Config.Mcp.AllowWrite = CheckMcpAllowWrite.Checked == true;
        Config.Mcp.AllowGameControl = CheckMcpAllowGame.Checked == true;
        Config.Mcp.AllowLauncherConfig = CheckMcpAllowConfig.Checked == true;
    }

    private void BtnMcpRandomToken_Click(object sender, MouseButtonEventArgs e)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        TextMcpToken.Text = token;
        Config.Mcp.Token = token;
        UpdateMcpStatus();
        HintService.Hint("已生成新口令，记得同步更新 AI 客户端的配置", HintType.Success, false);
    }

    private void BtnCopyMcpConfig_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(McpServerService.BuildClientConfig());
            HintService.Hint("连接配置已复制，粘进 AI 客户端的 mcpServers 即可", HintType.Success, false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[MCP] 复制连接配置失败");
            HintService.Hint("复制失败，请手动复制", HintType.Error, false);
        }
    }

    private void BtnMcpRestart_Click(object sender, MouseButtonEventArgs e)
    {
        McpServerService.Restart();
        UpdateMcpStatus();
        if (McpServerService.IsRunning)
            HintService.Hint("MCP 服务已重启", HintType.Success, false);
        else
            HintService.Hint($"MCP 服务启动失败：{McpServerService.LastError}", HintType.Error, false);
    }

    private void BtnMcpOpenFolder_Click(object sender, MouseButtonEventArgs e)
        => ModBase.OpenExplorer(Basics.ExecutableDirectory);

    #endregion

    #region 导出 / 导入设置

    private void BtnSystemSettingExp_Click(object sender, MouseButtonEventArgs e)
    {
        var savePath =
            SystemDialogs.SelectSaveFile(Lang.Text("Setup.Misc.System.ExportSettings.SaveTitle"), "PCL 全局配置.json", Lang.Text("Setup.Misc.System.ExportSettings.Filter"), ModBase.exePath);
        if (string.IsNullOrWhiteSpace(savePath))
            return;
        File.Copy(ConfigService.SharedConfigPath, savePath, true);
        HintService.Hint(Lang.Text("Setup.Misc.System.ExportSettings.Success"), HintType.Success);
        ModBase.OpenExplorer(savePath);
    }

    private void BtnSystemSettingImp_Click(object sender, MouseButtonEventArgs e)
    {
        var sourcePath = SystemDialogs.SelectFile(Lang.Text("Setup.Misc.System.ExportSettings.Filter"), Lang.Text("Setup.Misc.System.ImportSettings.SelectTitle"));
        if (string.IsNullOrWhiteSpace(sourcePath))
            return;
        File.Copy(sourcePath, ConfigService.SharedConfigPath, true);
        ModMain.MyMsgBox(Lang.Text("Setup.Misc.System.ImportSettings.Success.Message"), button1: Lang.Text("Setup.Misc.System.ImportSettings.Success.Restart"), forceWait: true);
        Process.Start(new ProcessStartInfo(Basics.ExecutablePath));
        FormMain.EndProgramForce();
    }

    #endregion

    #region 停止使用 PCL Lite

    private void BtnSystemStopUsingPclCe_Click(object sender, MouseButtonEventArgs e)
    {
        var result = ModMain.MyMsgBox(
            Lang.Text("Setup.Misc.System.StopUsingPclCe.Message"),
            Lang.Text("Setup.Misc.System.StopUsingPclCe.Title"),
            Lang.Text("Common.Action.Continue"),
            Lang.Text("Setup.Misc.System.StopUsingPclCe.ContinueAndRemove"),
            Lang.Text("Common.Action.Cancel"),
            isWarn: true);

        if (result < 3)
        {
            if (ModMain.MyMsgBox(
                    Lang.Text("Setup.Misc.System.StopUsingPclCe.Message.Final"),
                    Lang.Text("Common.Dialog.Warning"),
                    Lang.Text("Common.Action.Continue"),
                    Lang.Text("Common.Action.Cancel"),
                    isWarn: true) == 1)
            {
                StopUsingPClCeCore(result == 2);
            }
        }
    }

    private void StopUsingPClCeCore(bool removeMcResources)
    {
        // 删除 MC 文件夹内的 PCL Lite 配置
        if (removeMcResources && States.Game.Folders != "")
        {
            foreach (var path in States.Game.Folders.Split('|'))
            {
                var realPath = path.Split('>')[1];
                Delete([Path.Combine(realPath, "PCL.ini")]);

                var versionsPath = Path.Combine(realPath, "versions");
                if (!Directory.Exists(versionsPath)) continue;
                
                Delete(
                    Directory.EnumerateDirectories(versionsPath)
                        .Select(p => Path.Combine(p, "PCL", "config.v1.yml"))
                );
            }
        }
        
        // 由于 CE 文件夹正在使用，使用延迟调用 CMD 的方法删除
        List<string> foldersToDelete =
        [
            Paths.Data,
            Paths.OldSharedData,
            Paths.SharedData,
            Paths.SharedLocalData,
            Paths.Temp
        ];

        var sb = new StringBuilder();
        sb.Append("/c timeout /t 5 /nobreak >nul & ");
        foreach (var folder in foldersToDelete)
        {
            sb.Append($"rmdir /s /q \"{folder}\" & ");
        }
        
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = sb.ToString(),
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        });

        // 强制退出
        KernelInterop.ExitProcess();
            
        void Delete(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                }
                catch (Exception)
                {
                    //
                }
            }
        }
    }

    #endregion

    #region Discord 状态显示

    private System.Windows.Threading.DispatcherTimer? _discordStatusTimer;

    /// <summary>
    ///     总开关。关掉时把下面的配置项全部收起来。
    /// </summary>
    private void DiscordEnabled_Change(object senderRaw, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        Config.Discord.Enabled = ((MyCheckBox)senderRaw).Checked == true;
        UpdateDiscordVisibility();
        DiscordRichPresenceService.RequestRefresh();
    }

    private void UpdateDiscordVisibility()
    {
        var enabled = Config.Discord.Enabled;
        PanDiscordOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        LabDiscordDisabledHint.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;

        if (!enabled)
        {
            _StopDiscordStatusTimer();
            return;
        }

        UpdateDiscordStatus();
        _StartDiscordStatusTimer();
    }

    private void _StartDiscordStatusTimer()
    {
        _discordStatusTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _discordStatusTimer.Tick -= _DiscordStatusTick;
        _discordStatusTimer.Tick += _DiscordStatusTick;
        _discordStatusTimer.Start();
    }

    private void _StopDiscordStatusTimer()
    {
        _discordStatusTimer?.Stop();
    }

    private void _DiscordStatusTick(object? sender, EventArgs e) => UpdateDiscordStatus();

    /// <summary>
    ///     状态由后台线程更新，这里只读快照。
    /// </summary>
    private void UpdateDiscordStatus()
    {
        var text = DiscordRichPresenceService.StatusText;
        LabDiscordStatus.Text = $"状态：{text}";

        var brush = text.StartsWith("已连接", StringComparison.Ordinal) ? "ColorBrush1"
            : text.StartsWith("异常", StringComparison.Ordinal) ? "ColorBrushRedLight"
            : "ColorBrushGray2";
        LabDiscordStatus.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private void DiscordAppId_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        var text = TextDiscordAppId.Text.Trim();
        if (Config.Discord.AppId == text) return;
        Config.Discord.AppId = text;
        DiscordRichPresenceService.RequestRefresh();
    }

    /// <summary>
    ///     任意一个显示项变化都重算整串配置。
    /// </summary>
    private void DiscordField_Change(object senderRaw, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0) return;

        var selected = new List<string>(6);
        if (CheckDiscordLauncherVersion.Checked == true) selected.Add(DiscordRpcFields.LauncherVersion);
        if (CheckDiscordLauncherMemory.Checked == true) selected.Add(DiscordRpcFields.LauncherMemory);
        if (CheckDiscordGameVersion.Checked == true) selected.Add(DiscordRpcFields.GameVersion);
        if (CheckDiscordGameMemory.Checked == true) selected.Add(DiscordRpcFields.GameMemory);
        if (CheckDiscordPlayTime.Checked == true) selected.Add(DiscordRpcFields.PlayTime);
        if (CheckDiscordServer.Checked == true) selected.Add(DiscordRpcFields.ServerAddress);

        var value = string.Join(",", selected);
        if (Config.Discord.Fields == value) return;
        Config.Discord.Fields = value;
        DiscordRichPresenceService.RequestRefresh();
    }

    private void BtnDiscordRefresh_Click(object sender, EventArgs e)
    {
        DiscordRichPresenceService.RequestRefresh();
        UpdateDiscordStatus();
    }

    private void BtnDiscordClear_Click(object sender, EventArgs e)
    {
        DiscordRichPresenceService.ClearActivity();
        UpdateDiscordStatus();
    }

    private void BtnDiscordPortal_Click(object sender, EventArgs e)
    {
        ModBase.OpenWebsite("https://discord.com/developers/applications");
    }

    #endregion
}
