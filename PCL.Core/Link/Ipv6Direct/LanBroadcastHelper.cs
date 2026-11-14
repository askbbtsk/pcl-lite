using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Link.Ipv6Direct;

public sealed record LanWorldInfo(string Motd, ushort Port, DateTimeOffset LastSeenUtc);

/// <summary>
/// 监听 Minecraft Java 版“对局域网开放”的 UDP 多播（224.0.2.60:4445），以及向本地回环/多播伪造房间广播。
/// </summary>
public static partial class LanBroadcastHelper
{
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.2.60");
    public const int MulticastPort = 4445;

    [GeneratedRegex(@"\[MOTD\](?<motd>.*?)\[/MOTD\]\s*\[AD\](?<port>\d+)\[/AD\]", RegexOptions.Singleline)]
    private static partial Regex LanRegex();

    public static bool TryParseAnnouncement(string message, out string motd, out ushort port)
    {
        motd = string.Empty;
        port = 0;
        var match = LanRegex().Match(message);
        if (!match.Success)
            return false;
        if (!ushort.TryParse(match.Groups["port"].Value, out port) || port == 0)
            return false;
        motd = match.Groups["motd"].Value;
        return true;
    }

    public static async Task RunListenerAsync(
        Action<LanWorldInfo> onDetected,
        CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        try
        {
            udp.JoinMulticastGroup(MulticastAddress);
        }
        catch
        {
            // 部分无 IPv4 默认网关的环境可能 JoinMulticastGroup 失败，仍尽量监听本地广播
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (TryParseAnnouncement(text, out var motd, out var port))
                {
                    // 忽略自己发出的 IPv6 直连转发广播，避免房客端误识别成房主端口
                    if (motd.Contains("[IPv6直连]", StringComparison.Ordinal))
                        continue;
                    onDetected(new LanWorldInfo(motd, port, DateTimeOffset.UtcNow));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public static async Task RunAnnouncerAsync(
        Func<(bool Enabled, string Motd, ushort Port)> stateProvider,
        CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(MulticastAddress, MulticastPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (enabled, motd, port) = stateProvider();
                if (enabled && port > 0)
                {
                    var payload = $"[MOTD]§b[IPv6直连] §r{motd}[/MOTD][AD]{port}[/AD]";
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await udp.SendAsync(bytes, endpoint, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 忽略单次广播异常
            }

            try
            {
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
