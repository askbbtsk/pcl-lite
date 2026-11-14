using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    private bool _hasAutoFilledLanPort;

    public PageToolsDirectLink()
    {
        InitializeComponent();
        Loaded += PageToolsDirectLink_Loaded;
        Unloaded += PageToolsDirectLink_Unloaded;
    }

    private void PageToolsDirectLink_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshIpv6Status();
        StartLanListener();
    }

    private void PageToolsDirectLink_Unloaded(object sender, RoutedEventArgs e)
    {
        _lanListenCts?.Cancel();
        _lanListenCts?.Dispose();
        _lanListenCts = null;
    }

    private void RefreshIpv6Status()
    {
        _ipv6Candidates = Ipv6AddressDetector.GetPublicIpv6Addresses();
        if (_ipv6Candidates.Count == 0)
        {
            LabIpv6Status.Text = "未检测到可用的公网 IPv6 地址（校园网/公司内网或路由器未开启 IPv6 时无法使用纯 IPv6 直连）。";
        }
        else
        {
            var primary = _ipv6Candidates[0];
            LabIpv6Status.Text = $"已检测到可用公网 IPv6：{primary.Address}（网卡：{primary.InterfaceName}）";
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
                LabLanDetected.Text = $"已捕获到 MC 局域网世界：{info.Motd}（端口 {info.Port}）";
                if (!_hasAutoFilledLanPort || _hostSession is not null)
                {
                    _hasAutoFilledLanPort = true;
                    TextHostPort.Text = info.Port.ToString();
                }
                if (_hostSession is not null)
                    _hostSession.McPort = info.Port;
            });
        }, ct), ct);
    }

    private void BtnRefreshIpv6_Click(object sender, RoutedEventArgs e)
    {
        RefreshIpv6Status();
        HintService.Hint(
            _ipv6Candidates.Count > 0 ? "已刷新本机公网 IPv6 地址。" : "仍未检测到可用公网 IPv6。",
            _ipv6Candidates.Count > 0 ? HintType.Success : HintType.Error);
    }

    private void TextHostPort_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_hostSession is not null && ushort.TryParse(TextHostPort.Text.Trim(), out var port) && port > 0)
            _hostSession.McPort = port;
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
                HintService.Hint("当前网络没有可用公网 IPv6，无法创建 IPv6 直连房间。", HintType.Error);
                return;
            }
        }

        if (!ushort.TryParse(TextHostPort.Text.Trim(), out var mcPort) || mcPort == 0)
        {
            HintService.Hint("请填写有效的游戏端口（1~65535）。", HintType.Error);
            return;
        }

        try
        {
            _hostSession = Ipv6HostSession.Start(_ipv6Candidates[0].Address, mcPort);
            _hostSession.StateChanged += () => Dispatcher.BeginInvoke(UpdateHostPeersUi);
            TextInviteCode.Text = _hostSession.InviteCode;
            PanHostActive.Visibility = Visibility.Visible;
            BtnHostToggle.Text = Lang.Text("Tools.DirectLink.HostStop");
            BtnHostToggle.ColorType = MyButton.ColorState.Red;
            UpdateHostPeersUi();
            HintService.Hint("IPv6 直连房间已创建，请复制邀请码发给好友。", HintType.Success);
        }
        catch (Exception ex)
        {
            HintService.Hint($"创建房间失败：{ex.Message}", HintType.Error);
        }
    }

    private async Task StopHostAsync()
    {
        if (_hostSession is null)
            return;
        var session = _hostSession;
        _hostSession = null;
        await session.DisposeAsync();
        PanHostActive.Visibility = Visibility.Collapsed;
        TextInviteCode.Text = string.Empty;
        TextHostAckInput.Text = string.Empty;
        LabHostPeers.Text = "尚未有好友连接。";
        BtnHostToggle.Text = Lang.Text("Tools.DirectLink.HostStart");
        BtnHostToggle.ColorType = MyButton.ColorState.Highlight;
        HintService.Hint("已关闭 IPv6 直连房间。");
    }

    private void UpdateHostPeersUi()
    {
        if (_hostSession is null)
            return;
        var peers = _hostSession.GetPeers();
        if (peers.Count == 0)
        {
            LabHostPeers.Text = "等待好友连接…（若双方均无防火墙拦截可直接免回执连入；否则请让好友发回 ACK6- 回执码）";
            return;
        }

        var lines = peers.Select(p =>
        {
            var status = p.State == DirectPeerState.Connected
                ? $"已连通（延迟 {p.RttMs} ms{(p.IsDirectNoAck ? "，免回执直连" : "")}）"
                : "正在双向 UDP 打洞…";
            return $"• [{p.Endpoint.Address}]:{p.Endpoint.Port} — {status}";
        });
        LabHostPeers.Text = string.Join(Environment.NewLine, lines);
    }

    private void BtnCopyInvite_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextInviteCode.Text)) return;
        Clipboard.SetText(TextInviteCode.Text);
        HintService.Hint("已复制邀请码！", HintType.Success);
    }

    private void BtnAcceptAck_Click(object sender, RoutedEventArgs e)
    {
        if (_hostSession is null)
        {
            HintService.Hint("请先创建房间。", HintType.Error);
            return;
        }

        var ok = _hostSession.TryAcceptGuestAck(TextHostAckInput.Text, out var msg);
        if (ok)
        {
            TextHostAckInput.Text = string.Empty;
            UpdateHostPeersUi();
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
                HintService.Hint("当前网络没有可用公网 IPv6，无法加入 IPv6 直连房间。", HintType.Error);
                return;
            }
        }

        if (!InviteCodeCodec.TryDecode(TextGuestInviteInput.Text, out var invite, out var error))
        {
            HintService.Hint(error, HintType.Error);
            return;
        }
        if (invite.Kind != InviteKind.HostInvite)
        {
            HintService.Hint("请填写房主发来的 PCL6- 邀请码，而不是回执码。", HintType.Error);
            return;
        }

        try
        {
            _guestSession = Ipv6GuestSession.Start(_ipv6Candidates[0].Address, invite);
            _guestSession.StateChanged += () => Dispatcher.BeginInvoke(UpdateGuestUi);

            TextGuestAckCode.Text = _guestSession.AckCode;
            TextLocalAddr.Text = $"127.0.0.1:{_guestSession.LocalProxyPort}";
            PanGuestActive.Visibility = Visibility.Visible;
            PanGuestAck.Visibility = Visibility.Collapsed;
            PanGuestConnected.Visibility = Visibility.Collapsed;
            LabGuestStatus.Text = $"正在尝试直接连接房主 [{invite.Address}]:{invite.Port}…";

            BtnGuestToggle.Text = Lang.Text("Tools.DirectLink.GuestDisconnect");
            BtnGuestToggle.ColorType = MyButton.ColorState.Red;

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
                            LabGuestStatus.Text = "房主端可能开启了光猫/系统防火墙：请复制下方 ACK6- 回执码发给房主放行，正在后台持续保活打洞…";
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
            HintService.Hint($"加入房间失败：{ex.Message}", HintType.Error);
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
        PanGuestActive.Visibility = Visibility.Collapsed;
        PanGuestAck.Visibility = Visibility.Collapsed;
        PanGuestConnected.Visibility = Visibility.Collapsed;
        BtnGuestToggle.Text = Lang.Text("Tools.DirectLink.GuestConnect");
        BtnGuestToggle.ColorType = MyButton.ColorState.Highlight;
        HintService.Hint("已断开 IPv6 直连。");
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
            LabGuestStatus.Text = $"已连通房主！（延迟 {_guestSession.RttMs} ms）现在可以直接在 Minecraft“多人游戏”最下方进入房间。";
        }
    }

    private void BtnCopyGuestAck_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextGuestAckCode.Text)) return;
        Clipboard.SetText(TextGuestAckCode.Text);
        HintService.Hint("已复制回执码，请发给房主粘贴放行！", HintType.Success);
    }

    private void BtnCopyLocalAddr_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextLocalAddr.Text)) return;
        Clipboard.SetText(TextLocalAddr.Text);
        HintService.Hint("已复制本地连接地址！", HintType.Success);
    }
}
