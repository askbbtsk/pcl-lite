using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App.Localization;
using PCL.Core.Link.Ipv6Direct;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.UI;

namespace PCL;

public partial class PageToolsDirectLink
{
    private static readonly TimeSpan LanWorldExpireTimeout = TimeSpan.FromSeconds(4.5);

    private IReadOnlyList<Ipv6Candidate> _ipv6Candidates = [];
    private Ipv6HostSession? _hostSession;
    private Ipv6GuestSession? _guestSession;
    private CancellationTokenSource? _lanListenCts;
    private DispatcherTimer? _hostPunchUiTimer;
    private DispatcherTimer? _lanExpireTimer;
    private FormMain.PageSubType _currentMode = FormMain.PageSubType.LinkHost;
    private LanWorldInfo? _lastLanWorld;
    private ushort _lastAutoFilledLanPort;
    private bool _hasUserEditedHostPort;
    private bool _isProgrammaticHostPortUpdate;

    public PageToolsDirectLink()
    {
        InitializeComponent();
        Loaded += PageToolsDirectLink_Loaded;
        Unloaded += PageToolsDirectLink_Unloaded;
    }

    public void SetMode(FormMain.PageSubType mode)
    {
        TextGuestInviteInput.Text = string.Empty;
        TextHostAckInput.Text = string.Empty;
        _currentMode = mode == FormMain.PageSubType.LinkGuest
            ? FormMain.PageSubType.LinkGuest
            : FormMain.PageSubType.LinkHost;
        PanHostMode.Visibility = _currentMode == FormMain.PageSubType.LinkHost ? Visibility.Visible : Visibility.Collapsed;
        PanGuestMode.Visibility = _currentMode == FormMain.PageSubType.LinkGuest ? Visibility.Visible : Visibility.Collapsed;
        PushSidebarStatus();
    }

    private void PageToolsDirectLink_Loaded(object sender, RoutedEventArgs e)
    {
        SetMode(_currentMode);
        RefreshIpv6Status();
        if (_hostSession is null)
            ClearTransientHostPortState();
        else
            TextInviteCode.Text = _hostSession.InviteCode;
        if (_guestSession is not null)
            TextGuestAckCode.Text = _guestSession.AckCode;
        StartLanListener();
        UpdateHostPeersUi();
        UpdateGuestUi();
    }

    private void PageToolsDirectLink_Unloaded(object sender, RoutedEventArgs e)
    {
        TextGuestInviteInput.Text = string.Empty;
        TextHostAckInput.Text = string.Empty;
        TextGuestAckCode.Text = string.Empty;
        // Keep watching the host's LAN announcement while the room is active so
        // guests learn when the Minecraft world closes, even if this page is hidden.
        if (_hostSession is null)
        {
            _lanListenCts?.Cancel();
            _lanListenCts?.Dispose();
            _lanListenCts = null;
            StopLanExpireTimer();
        }
        StopHostPunchTimer();
        if (_hostSession is null)
            ClearTransientHostPortState();
    }

    public void RefreshIpv6ByUser()
    {
        RefreshIpv6Status();
        HintService.Hint(
            _ipv6Candidates.Count > 0 ? "当前网络可以连接，可以直接开房或加房。" : "还是没检测到公网 IPv6，检查下路由器或试试手机热点。",
            _ipv6Candidates.Count > 0 ? HintType.Success : HintType.Error);
    }

    private void RefreshIpv6Status()
    {
        _ipv6Candidates = Ipv6AddressDetector.GetPublicIpv6Addresses();
        CardNoIpv6.Visibility = _ipv6Candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PushSidebarStatus();
    }

    public void PushSidebarStatus()
    {
        if (ModMain.frmLinkLeft is null)
            return;

        if (_hostSession is not null)
        {
            var connectedCount = _hostSession.GetPeers().Count(p => p.State == DirectPeerState.Connected);
            var detail = connectedCount > 0
                ? Lang.Text("Link.Sidebar.HostPeersCount", connectedCount, _hostSession.McPort)
                : Lang.Text("Link.Sidebar.HostWaiting", _hostSession.McPort);
            ModMain.frmLinkLeft.UpdateSidebarStatus(
                true,
                Lang.Text("Link.Sidebar.HostRunning"),
                $"{detail}\n房主 PCL 账户：{(_hostSession.RequireOnlineAuth ? "在线/外置" : "离线")}");
            return;
        }

        if (_guestSession is not null)
        {
            if (_guestSession.IsConnected)
            {
                ModMain.frmLinkLeft.UpdateSidebarStatus(
                    true,
                    Lang.Text("Link.Sidebar.GuestConnected"),
                    $"127.0.0.1:{_guestSession.LocalProxyPort} · {_guestSession.RttMs} ms");
            }
            else if (_guestSession.HasConnected)
            {
                ModMain.frmLinkLeft.UpdateSidebarStatus(
                    false,
                    Lang.Text("Link.Sidebar.GuestDisconnected"),
                    Lang.Text("Link.Sidebar.GuestDisconnectedDetail"));
            }
            else
            {
                ModMain.frmLinkLeft.UpdateSidebarStatus(
                    false,
                    Lang.Text("Link.Sidebar.GuestConnecting"),
                    "房主 #1 · 正在建立直连通道");
            }
            return;
        }

        if (_ipv6Candidates.Count > 0)
        {
            ModMain.frmLinkLeft.UpdateSidebarStatus(
                true,
                Lang.Text("Link.Sidebar.Ipv6Ready"),
                Lang.Text("Link.Sidebar.Ipv6ReadyDetail"));
        }
        else
        {
            ModMain.frmLinkLeft.UpdateSidebarStatus(
                false,
                Lang.Text("Link.Sidebar.NoIpv6"),
                Lang.Text("Link.Sidebar.NoIpv6Detail"));
        }
    }

    private void StartLanListener()
    {
        EnsureLanExpireTimerRunning();
        if (_lanListenCts is not null)
            return;

        _lanListenCts = new CancellationTokenSource();
        var ct = _lanListenCts.Token;
        _ = Task.Run(() => LanBroadcastHelper.RunListenerAsync(info =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _lastLanWorld = info;
                LabLanDetected.Text = Lang.Text("Link.Host.LanCaptured", info.Motd, info.Port);
                if (_hostSession is not null)
                {
                    _lastAutoFilledLanPort = info.Port;
                    SetHostPortProgrammatic(info.Port.ToString());
                    _hostSession.McPort = info.Port;
                    _hostSession.SetLanServerAvailability(info.Port, available: true);
                    PushSidebarStatus();
                }
                else if (!_hasUserEditedHostPort && _lastAutoFilledLanPort != info.Port)
                {
                    _lastAutoFilledLanPort = info.Port;
                    SetHostPortProgrammatic(info.Port.ToString());
                }
            });
        }, ct), ct);
    }

    private void EnsureLanExpireTimerRunning()
    {
        if (_lanExpireTimer is null)
        {
            _lanExpireTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _lanExpireTimer.Tick += (_, _) => ExpireStaleLanWorldIfNeeded();
        }
        if (!_lanExpireTimer.IsEnabled)
            _lanExpireTimer.Start();
    }

    private void StopLanExpireTimer()
    {
        _lanExpireTimer?.Stop();
    }

    private void ExpireStaleLanWorldIfNeeded()
    {
        if (_lastLanWorld is null)
            return;
        if (DateTimeOffset.UtcNow - _lastLanWorld.LastSeenUtc <= LanWorldExpireTimeout)
            return;

        var expiredPort = _lastLanWorld.Port;
        _lastLanWorld = null;
        _lastAutoFilledLanPort = 0;
        LabLanDetected.Text = Lang.Text("Link.Host.LanWaiting");
        _hostSession?.SetLanServerAvailability(expiredPort, available: false);
        if (_hostSession is null && !_hasUserEditedHostPort)
            SetHostPortProgrammatic(string.Empty);
    }

    private void ClearTransientHostPortState()
    {
        _lastLanWorld = null;
        _lastAutoFilledLanPort = 0;
        _hasUserEditedHostPort = false;
        LabLanDetected.Text = Lang.Text("Link.Host.LanWaiting");
        SetHostPortProgrammatic(string.Empty);
    }

    private void SetHostPortProgrammatic(string value)
    {
        _isProgrammaticHostPortUpdate = true;
        try
        {
            TextHostPort.Text = value;
        }
        finally
        {
            _isProgrammaticHostPortUpdate = false;
        }
    }

    private void BtnRetryIpv6_Click(object sender, MouseButtonEventArgs e)
    {
        RefreshIpv6ByUser();
    }

    private void TextHostPort_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isProgrammaticHostPortUpdate)
            _hasUserEditedHostPort = !string.IsNullOrWhiteSpace(TextHostPort.Text);

        if (_hostSession is not null && ushort.TryParse(TextHostPort.Text.Trim(), out var port) && port > 0)
        {
            _hostSession.McPort = port;
            if (_lastLanWorld is { } detectedWorld &&
                detectedWorld.Port == port &&
                DateTimeOffset.UtcNow - detectedWorld.LastSeenUtc <= LanWorldExpireTimeout)
                _hostSession.SetLanServerAvailability(port, available: true);
            else
                _hostSession.ClearLanServerAvailability();
            PushSidebarStatus();
        }
    }

    private async void BtnHostToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_hostSession is not null)
        {
            await StopHostAsync();
            return;
        }

        RefreshIpv6Status();
        if (_ipv6Candidates.Count == 0)
        {
            HintService.Hint("当前网络没拿到公网 IPv6，开不了直连房间。", HintType.Error);
            return;
        }

        if (!ushort.TryParse(TextHostPort.Text.Trim(), out var mcPort) || mcPort == 0)
        {
            HintService.Hint("端口号不对，得填 1 到 65535 之间的数字。", HintType.Error);
            return;
        }

        try
        {
            // Mirror the currently selected PCL profile for compatibility metadata.
            // The tunnel does not change Minecraft's authentication policy.
            var profileType = ProfileService.Current?.ProfileType;
            var requireOnlineAuth = profileType is ProfileType.Microsoft or ProfileType.Authlib or ProfileType.YggdrasilConnect;
            _hostSession = Ipv6HostSession.Start(_ipv6Candidates[0].Address, mcPort, requireOnlineAuth);
            _hostSession.StateChanged += () => Dispatcher.BeginInvoke(() =>
            {
                UpdateHostPeersUi();
                PushSidebarStatus();
            });
            EnsureLanExpireTimerRunning();
            if (_lanListenCts is null)
                StartLanListener();
            if (_lastLanWorld is { } detectedWorld &&
                detectedWorld.Port == mcPort &&
                DateTimeOffset.UtcNow - detectedWorld.LastSeenUtc <= LanWorldExpireTimeout)
                _hostSession.SetLanServerAvailability(mcPort, available: true);

            LabHostAuthMode.Text = (requireOnlineAuth
                ? "房主 PCL 当前选择在线/外置账户。"
                : "房主 PCL 当前选择离线账户。") +
                " 是否允许离线玩家进入仍由 Minecraft 游戏端决定。";
            TextInviteCode.Text = _hostSession.InviteCode;
            PanHostInviteRow.Visibility = Visibility.Visible;
            CardHostAck.Visibility = Visibility.Visible;
            CardHostPeers.Visibility = Visibility.Visible;
            BtnHostToggle.Text = Lang.Text("Link.Host.StopBtn");
            BtnHostToggle.ColorType = MyButton.ColorState.Red;
            UpdateHostPeersUi();
            PushSidebarStatus();

            HintService.Hint("房间已开启。需要分享邀请码时，请点“复制邀请码”。", HintType.Success);
        }
        catch (Exception ex)
        {
            HintService.Hint($"开房失败：{ex.Message}", HintType.Error);
        }
    }

    private async Task StopHostAsync()
    {
        StopHostPunchTimer();
        TextHostAckInput.Text = string.Empty;
        if (_hostSession is null)
            return;
        var session = _hostSession;
        _hostSession = null;
        await session.DisposeAsync();
        PanHostInviteRow.Visibility = Visibility.Collapsed;
        CardHostAck.Visibility = Visibility.Collapsed;
        CardHostPeers.Visibility = Visibility.Collapsed;
        LabHostAuthMode.Text = string.Empty;
        TextInviteCode.Text = string.Empty;
        TextHostAckInput.Text = string.Empty;
        PanelHostPeers.Children.Clear();
        PanelHostPeers.Children.Add(new TextBlock
        {
            Text = Lang.Text("Link.Host.NoPeers"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22
        });
        BtnHostToggle.Text = Lang.Text("Link.Host.StartBtn");
        BtnHostToggle.ColorType = MyButton.ColorState.Highlight;
        ClearTransientHostPortState();
        PushSidebarStatus();
        HintService.Hint("已关闭直连房间。");
    }

    private void UpdateHostPeersUi()
    {
        if (_hostSession is null)
        {
            StopHostPunchTimer();
            return;
        }
        var peers = _hostSession.GetPeers();
        PanelHostPeers.Children.Clear();
        if (peers.Count == 0)
        {
            StopHostPunchTimer();
            PanelHostPeers.Children.Add(new TextBlock
            {
                Text = Lang.Text("Link.Host.NoPeers"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22
            });
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var hasPunching = false;
        foreach (var peer in peers)
        {
            var label = peer.VirtualId > 0 ? $"好友 #{peer.VirtualId}" : "好友";
            string status;
            if (peer.State == DirectPeerState.Connected)
            {
                status = $"已连上 · {peer.RttMs} ms{(peer.IsDirectNoAck ? "（直连）" : "")}";
            }
            else
            {
                hasPunching = true;
                var elapsedSec = peer.AddedUtc == default ? 0 : Math.Max(0, (int)(now - peer.AddedUtc).TotalSeconds);
                status = elapsedSec >= 8
                    ? $"仍在打洞（已尝试 {elapsedSec}s，请检查双方 Windows 防火墙及路由器 IPv6 入站放行，并在日志查看报错详情）"
                    : $"正在打洞，已尝试 {elapsedSec}s…";
            }

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = $"• {label}  {status}",
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                VerticalAlignment = VerticalAlignment.Center
            });
            var disconnectButton = new MyButton
            {
                Text = Lang.Text("Link.Host.DisconnectPeer"),
                Tag = peer.VirtualId,
                MinWidth = 105,
                Height = 34,
                Margin = new Thickness(12, 0, 0, 0),
                ColorType = MyButton.ColorState.Red
            };
            disconnectButton.Click += BtnDisconnectPeer_Click;
            Grid.SetColumn(disconnectButton, 1);
            row.Children.Add(disconnectButton);
            PanelHostPeers.Children.Add(row);
        }

        if (hasPunching)
            EnsureHostPunchTimerRunning();
        else
            StopHostPunchTimer();
    }

    private async void BtnDisconnectPeer_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MyButton button ||
            button.Tag is not int virtualId ||
            _hostSession is not { } session)
            return;

        if (!await session.DisconnectPeerAsync(virtualId))
        {
            HintService.Hint("该好友已断开或房间已关闭。", HintType.Error);
            UpdateHostPeersUi();
            return;
        }

        UpdateHostPeersUi();
        PushSidebarStatus();
        HintService.Hint($"已断开好友 #{virtualId}。", HintType.Success);
    }

    private void EnsureHostPunchTimerRunning()
    {
        if (_hostPunchUiTimer is null)
        {
            _hostPunchUiTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _hostPunchUiTimer.Tick += (_, _) => UpdateHostPeersUi();
        }
        if (!_hostPunchUiTimer.IsEnabled)
            _hostPunchUiTimer.Start();
    }

    private void StopHostPunchTimer()
    {
        _hostPunchUiTimer?.Stop();
    }

    private void BtnCopyInvite_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextInviteCode.Text)) return;
        TrySetClipboard(TextInviteCode.Text);
        HintService.Hint("邀请码已复制！", HintType.Success);
    }

    private void BtnAcceptAck_Click(object sender, MouseButtonEventArgs e)
    {
        if (_hostSession is null)
        {
            HintService.Hint("请先点上面的按钮创建房间。", HintType.Error);
            return;
        }

        var raw = TextHostAckInput.Text.Trim();
        TextHostAckInput.Text = string.Empty;
        var ok = _hostSession.TryAcceptGuestAck(raw, out var msg);
        if (ok)
        {
            TextHostAckInput.Focus();
            UpdateHostPeersUi();
            PushSidebarStatus();
            HintService.Hint(msg, HintType.Success);
        }
        else
        {
            HintService.Hint(msg, HintType.Error);
        }
    }

    private async void BtnGuestToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_guestSession is not null)
        {
            await StopGuestAsync();
            return;
        }

        RefreshIpv6Status();
        if (_ipv6Candidates.Count == 0)
        {
            HintService.Hint("当前网络没拿到公网 IPv6，连不上纯 IPv6 房间。", HintType.Error);
            return;
        }

        var input = TextGuestInviteInput.Text.Trim();
        TextGuestInviteInput.Text = string.Empty;
        if (!InviteCodeCodec.TryDecode(input, out var invite, out var error))
        {
            HintService.Hint(error, HintType.Error);
            return;
        }
        if (invite.Kind != InviteKind.HostInvite)
        {
            HintService.Hint("这个看起来是回执码，这里要填房主发来的 BSK- 邀请码。", HintType.Error);
            return;
        }

        try
        {
            _guestSession = Ipv6GuestSession.Start(_ipv6Candidates[0].Address, invite);
            _guestSession.StateChanged += () => Dispatcher.BeginInvoke(() =>
            {
                UpdateGuestUi();
                PushSidebarStatus();
            });

            TextGuestAckCode.Text = _guestSession.AckCode;
            TextLocalAddr.Text = $"127.0.0.1:{_guestSession.LocalProxyPort}";
            CardGuestProgress.Visibility = Visibility.Visible;
            PanGuestAck.Visibility = Visibility.Visible;
            PanGuestConnected.Visibility = Visibility.Collapsed;
            LabGuestStatus.Text = Lang.Text("Link.Guest.NeedAckStatus");

            BtnGuestToggle.Text = Lang.Text("Link.Guest.DisconnectBtn");
            BtnGuestToggle.ColorType = MyButton.ColorState.Red;
            PushSidebarStatus();

            HintService.Hint("连接请求已发出。请点“复制回传码”并通过聊天平台发送给房主。", HintType.Success);
        }
        catch (Exception ex)
        {
            HintService.Hint($"连接失败：{ex.Message}", HintType.Error);
        }
    }

    private async Task StopGuestAsync()
    {
        TextGuestInviteInput.Text = string.Empty;
        TextGuestAckCode.Text = string.Empty;
        if (_guestSession is null)
            return;

        var session = _guestSession;
        _guestSession = null;
        await session.DisposeAsync();
        CardGuestProgress.Visibility = Visibility.Collapsed;
        PanGuestAck.Visibility = Visibility.Collapsed;
        PanGuestConnected.Visibility = Visibility.Collapsed;
        TextLocalAddr.Text = string.Empty;
        BtnGuestToggle.Text = Lang.Text("Link.Guest.ConnectBtn");
        BtnGuestToggle.ColorType = MyButton.ColorState.Highlight;
        PushSidebarStatus();
        HintService.Hint("已断开联机。");
    }

    private void UpdateGuestUi()
    {
        if (_guestSession is null)
            return;

        if (_guestSession.IsConnected)
        {
            PanGuestAck.Visibility = Visibility.Collapsed;
            PanGuestConnected.Visibility = Visibility.Visible;

            var statusText = Lang.Text("Link.Guest.ConnectedStatus", _guestSession.RttMs);
            var profileHint = _guestSession.HostRequiresOnlineAuth
                ? "房主 PCL 当前选择在线/外置账户。"
                : "房主 PCL 当前选择离线账户。";
            LabGuestStatus.Text = statusText + "\n" + profileHint +
                " 能否进入游戏仍由 Minecraft 游戏端决定。";
        }
        else if (_guestSession.HasConnected)
        {
            PanGuestAck.Visibility = Visibility.Collapsed;
            PanGuestConnected.Visibility = Visibility.Collapsed;
            LabGuestStatus.Text = Lang.Text("Link.Guest.ConnectionLost");
        }
        else
        {
            PanGuestAck.Visibility = Visibility.Visible;
            PanGuestConnected.Visibility = Visibility.Collapsed;
            LabGuestStatus.Text = Lang.Text("Link.Guest.NeedAckStatus");
        }
    }

    private void BtnCopyGuestAck_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextGuestAckCode.Text)) return;
        TrySetClipboard(TextGuestAckCode.Text);
        HintService.Hint("回执码已复制，发给房主放行就行！", HintType.Success);
    }

    private void BtnCopyLocalAddr_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextLocalAddr.Text)) return;
        TrySetClipboard(TextLocalAddr.Text);
        HintService.Hint("已复制本地地址！", HintType.Success);
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // 部分剪贴板锁定场景忽略异常
        }
    }

}
