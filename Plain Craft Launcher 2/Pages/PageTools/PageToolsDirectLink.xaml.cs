using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App.Localization;
using PCL.Core.Link.Ipv6Direct;
using PCL.Core.UI;

namespace PCL;

public partial class PageToolsDirectLink
{
    private IReadOnlyList<Ipv6Candidate> _ipv6Candidates = [];
    private Ipv6HostSession? _hostSession;
    private Ipv6GuestSession? _guestSession;
    private CancellationTokenSource? _lanListenCts;
    private CancellationTokenSource? _guestAckRevealCts;
    private FormMain.PageSubType _currentMode = FormMain.PageSubType.LinkHost;
    private bool _hasAutoFilledLanPort;

    public PageToolsDirectLink()
    {
        InitializeComponent();
        Loaded += PageToolsDirectLink_Loaded;
        Unloaded += PageToolsDirectLink_Unloaded;
    }

    public void SetMode(FormMain.PageSubType mode)
    {
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
        StartLanListener();
        UpdateHostPeersUi();
        UpdateGuestUi();
    }

    private void PageToolsDirectLink_Unloaded(object sender, RoutedEventArgs e)
    {
        // 只停止 LAN 多播扫描；若房主/房客联机正在进行，切换去启动游戏时保持隧道不断开。
        _lanListenCts?.Cancel();
        _lanListenCts?.Dispose();
        _lanListenCts = null;
    }

    public void RefreshIpv6ByUser()
    {
        RefreshIpv6Status();
        HintService.Hint(
            _ipv6Candidates.Count > 0 ? "公网 IPv6 检测通过，可以直接开房或加房。" : "还是没检测到公网 IPv6，检查下路由器或试试手机热点。",
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
            ModMain.frmLinkLeft.UpdateSidebarStatus(
                true,
                Lang.Text("Link.Sidebar.HostRunning"),
                connectedCount > 0
                    ? Lang.Text("Link.Sidebar.HostPeersCount", connectedCount, _hostSession.McPort)
                    : Lang.Text("Link.Sidebar.HostWaiting", _hostSession.McPort));
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
            else
            {
                ModMain.frmLinkLeft.UpdateSidebarStatus(
                    false,
                    Lang.Text("Link.Sidebar.GuestConnecting"),
                    $"[{_guestSession.HostEndpoint.Address}]:{_guestSession.HostEndpoint.Port}");
            }
            return;
        }

        if (_ipv6Candidates.Count > 0)
        {
            var primary = _ipv6Candidates[0];
            ModMain.frmLinkLeft.UpdateSidebarStatus(
                true,
                Lang.Text("Link.Sidebar.Ipv6Ready"),
                $"{primary.InterfaceName}\n{primary.Address}");
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
        if (_lanListenCts is not null)
            return;

        _lanListenCts = new CancellationTokenSource();
        var ct = _lanListenCts.Token;
        _ = Task.Run(() => LanBroadcastHelper.RunListenerAsync(info =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                LabLanDetected.Text = Lang.Text("Link.Host.LanCaptured", info.Motd, info.Port);
                if (!_hasAutoFilledLanPort || _hostSession is not null)
                {
                    _hasAutoFilledLanPort = true;
                    TextHostPort.Text = info.Port.ToString();
                }
                if (_hostSession is not null)
                {
                    _hostSession.McPort = info.Port;
                    PushSidebarStatus();
                }
            });
        }, ct), ct);
    }

    private void BtnRetryIpv6_Click(object sender, RoutedEventArgs e)
    {
        RefreshIpv6ByUser();
    }

    private void TextHostPort_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_hostSession is not null && ushort.TryParse(TextHostPort.Text.Trim(), out var port) && port > 0)
        {
            _hostSession.McPort = port;
            PushSidebarStatus();
        }
    }

    private async void BtnHostToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_hostSession is not null)
        {
            await StopHostAsync();
            return;
        }

        if (_ipv6Candidates.Count == 0)
        {
            RefreshIpv6Status();
            if (_ipv6Candidates.Count == 0)
            {
                HintService.Hint("当前网络没拿到公网 IPv6，开不了直连房间。", HintType.Error);
                return;
            }
        }

        if (!ushort.TryParse(TextHostPort.Text.Trim(), out var mcPort) || mcPort == 0)
        {
            HintService.Hint("端口号不对，得填 1 到 65535 之间的数字。", HintType.Error);
            return;
        }

        try
        {
            _hostSession = Ipv6HostSession.Start(_ipv6Candidates[0].Address, mcPort);
            _hostSession.StateChanged += () => Dispatcher.BeginInvoke(() =>
            {
                UpdateHostPeersUi();
                PushSidebarStatus();
            });

            TextInviteCode.Text = _hostSession.InviteCode;
            PanHostInviteRow.Visibility = Visibility.Visible;
            CardHostAck.Visibility = Visibility.Visible;
            CardHostPeers.Visibility = Visibility.Visible;
            BtnHostToggle.Text = Lang.Text("Link.Host.StopBtn");
            BtnHostToggle.ColorType = MyButton.ColorState.Red;
            UpdateHostPeersUi();
            PushSidebarStatus();

            TrySetClipboard(_hostSession.InviteCode);
            HintService.Hint("房间开好了，邀请码已经自动复制，直接去聊天框粘贴发给好友。", HintType.Success);
        }
        catch (Exception ex)
        {
            HintService.Hint($"开房失败：{ex.Message}", HintType.Error);
        }
    }

    private async Task StopHostAsync()
    {
        if (_hostSession is null)
            return;
        var session = _hostSession;
        _hostSession = null;
        await session.DisposeAsync();
        PanHostInviteRow.Visibility = Visibility.Collapsed;
        CardHostAck.Visibility = Visibility.Collapsed;
        CardHostPeers.Visibility = Visibility.Collapsed;
        TextInviteCode.Text = string.Empty;
        TextHostAckInput.Text = string.Empty;
        LabHostPeers.Text = Lang.Text("Link.Host.NoPeers");
        BtnHostToggle.Text = Lang.Text("Link.Host.StartBtn");
        BtnHostToggle.ColorType = MyButton.ColorState.Highlight;
        PushSidebarStatus();
        HintService.Hint("已关闭直连房间。");
    }

    private void UpdateHostPeersUi()
    {
        if (_hostSession is null)
            return;
        var peers = _hostSession.GetPeers();
        if (peers.Count == 0)
        {
            LabHostPeers.Text = Lang.Text("Link.Host.NoPeers");
            return;
        }

        var lines = peers.Select(p =>
        {
            var status = p.State == DirectPeerState.Connected
                ? $"已连上 · {p.RttMs} ms{(p.IsDirectNoAck ? "（直连）" : "")}"
                : "正在打洞，等两秒…";
            return $"• [{p.Endpoint.Address}]:{p.Endpoint.Port}  {status}";
        });
        LabHostPeers.Text = string.Join(Environment.NewLine, lines);
    }

    private void BtnCopyInvite_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextInviteCode.Text)) return;
        TrySetClipboard(TextInviteCode.Text);
        HintService.Hint("邀请码已复制！", HintType.Success);
    }

    private void BtnAcceptAck_Click(object sender, RoutedEventArgs e)
    {
        if (_hostSession is null)
        {
            HintService.Hint("请先点上面的按钮创建房间。", HintType.Error);
            return;
        }

        // 输入框为空时自动从剪贴板读，省去手动 Ctrl+V 的一步
        var raw = TextHostAckInput.Text.Trim();
        if (string.IsNullOrEmpty(raw) && TryGetClipboardText(out var clip) &&
            clip.Trim().StartsWith(InviteCodeCodec.GuestAckPrefix, StringComparison.OrdinalIgnoreCase))
        {
            raw = clip.Trim();
            TextHostAckInput.Text = raw;
        }

        var ok = _hostSession.TryAcceptGuestAck(raw, out var msg);
        if (ok)
        {
            TextHostAckInput.Text = string.Empty;
            UpdateHostPeersUi();
            PushSidebarStatus();
            HintService.Hint(msg, HintType.Success);
        }
        else
        {
            HintService.Hint(msg, HintType.Error);
        }
    }

    private async void BtnGuestToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_guestSession is not null)
        {
            await StopGuestAsync();
            return;
        }

        if (_ipv6Candidates.Count == 0)
        {
            RefreshIpv6Status();
            if (_ipv6Candidates.Count == 0)
            {
                HintService.Hint("当前网络没拿到公网 IPv6，连不上纯 IPv6 房间。", HintType.Error);
                return;
            }
        }

        // 若输入框为空或无效，尝试直接从剪贴板读 PCL6- 邀请码
        var input = TextGuestInviteInput.Text.Trim();
        if ((string.IsNullOrEmpty(input) || !input.StartsWith(InviteCodeCodec.HostPrefix, StringComparison.OrdinalIgnoreCase)) &&
            TryGetClipboardText(out var clip) &&
            clip.Trim().StartsWith(InviteCodeCodec.HostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            input = clip.Trim();
            TextGuestInviteInput.Text = input;
        }

        if (!InviteCodeCodec.TryDecode(input, out var invite, out var error))
        {
            HintService.Hint(error, HintType.Error);
            return;
        }
        if (invite.Kind != InviteKind.HostInvite)
        {
            HintService.Hint("这个看起来是回执码，这里要填房主发来的 PCL6- 邀请码。", HintType.Error);
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
            PanGuestAck.Visibility = Visibility.Collapsed;
            PanGuestConnected.Visibility = Visibility.Collapsed;
            LabGuestStatus.Text = Lang.Text("Link.Guest.Connecting");

            BtnGuestToggle.Text = Lang.Text("Link.Guest.DisconnectBtn");
            BtnGuestToggle.ColorType = MyButton.ColorState.Red;
            PushSidebarStatus();

            _guestAckRevealCts?.Cancel();
            _guestAckRevealCts = new CancellationTokenSource();
            var revealCt = _guestAckRevealCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1200, revealCt);
                    await Dispatcher.BeginInvoke(() =>
                    {
                        if (_guestSession is { IsConnected: false })
                        {
                            PanGuestAck.Visibility = Visibility.Visible;
                            LabGuestStatus.Text = Lang.Text("Link.Guest.NeedAckStatus");
                            TrySetClipboard(_guestSession.AckCode);
                            HintService.Hint("未直接连通，已自动复制回执码！快发给房主点放行。", HintType.Success);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                }
            }, revealCt);
        }
        catch (Exception ex)
        {
            HintService.Hint($"连接失败：{ex.Message}", HintType.Error);
        }
    }

    private async Task StopGuestAsync()
    {
        _guestAckRevealCts?.Cancel();
        _guestAckRevealCts?.Dispose();
        _guestAckRevealCts = null;

        if (_guestSession is null)
            return;

        var session = _guestSession;
        _guestSession = null;
        await session.DisposeAsync();
        CardGuestProgress.Visibility = Visibility.Collapsed;
        PanGuestAck.Visibility = Visibility.Collapsed;
        PanGuestConnected.Visibility = Visibility.Collapsed;
        BtnGuestToggle.Text = Lang.Text("Link.Guest.PasteAndConnect");
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
            _guestAckRevealCts?.Cancel();
            PanGuestAck.Visibility = Visibility.Collapsed;
            PanGuestConnected.Visibility = Visibility.Visible;
            LabGuestStatus.Text = Lang.Text("Link.Guest.ConnectedStatus", _guestSession.RttMs);
        }
    }

    private void BtnCopyGuestAck_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextGuestAckCode.Text)) return;
        TrySetClipboard(TextGuestAckCode.Text);
        HintService.Hint("回执码已复制，发给房主放行就行！", HintType.Success);
    }

    private void BtnCopyLocalAddr_Click(object sender, RoutedEventArgs e)
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

    private static bool TryGetClipboardText(out string text)
    {
        text = string.Empty;
        try
        {
            if (!Clipboard.ContainsText())
                return false;
            text = Clipboard.GetText();
            return !string.IsNullOrWhiteSpace(text);
        }
        catch
        {
            return false;
        }
    }
}
