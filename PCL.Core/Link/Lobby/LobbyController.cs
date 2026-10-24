using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.Scaffolding;
using PCL.Core.Link.Scaffolding.Client.Models;
using PCL.Core.Link.Scaffolding.Client.Requests;
using PCL.Core.Logging;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Secret;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net;
using static PCL.Core.Link.Lobby.LobbyInfoProvider;
using static PCL.Core.Link.Natayark.NatayarkProfileManager;
using LobbyType = PCL.Core.Link.Scaffolding.Client.Models.LobbyType;
using PCL.Core.Link.McPing;
using PCL.Core.IO.Net.Http;
using PCL.Core.IO.Net.SocketForward;

namespace PCL.Core.Link.Lobby;

/// <summary>
/// The controller of lobby that used for creating Scaffolding entity.
/// </summary>
public sealed class LobbyController
{
    /// <summary>
    /// Demonstrate the current lobby is host or joiner.
    /// </summary>
    public bool IsHost = false;

    /// <summary>
    /// Scaffolding client entity.
    /// </summary>
    public ScaffoldingClientEntity? ScfClientEntity;

    /// <summary>
    /// Scaffolding server entity.
    /// </summary>
    public ScaffoldingServerEntity? ScfServerEntity;

    /// <summary>
    /// Launch a Scaffolding Client.
    /// </summary>
    /// <param name="username">Join user name.</param>
    /// <param name="code">Lobby share code.</param>
    /// <returns>Created <see cref="ScaffoldingClientEntity"/>.</returns>
    public async Task<ScaffoldingClientEntity?> LaunchClientAsync(string username, string code, CancellationToken ct = default)
    {
        await _RegisterLobbyAsync(false).ConfigureAwait(false);

        try
        {
            var scfEntity = await ScaffoldingFactory
                .CreateClientAsync(username, code, LobbyType.Scaffolding, ct).ConfigureAwait(false);

            ScfClientEntity = scfEntity;

            await scfEntity.Client.ConnectAsync().ConfigureAwait(false);

            var port = await scfEntity.Client.SendRequestAsync(new GetServerPortRequest()).ConfigureAwait(false);

            var hostname = string.Empty;

            while (scfEntity.Client.PlayerList is null)
            {
                await Task.Delay(800).ConfigureAwait(false);
            }

            foreach (var profile in scfEntity.Client.PlayerList)
            {
                if (profile.Kind == PlayerKind.HOST)
                {
                    hostname = profile.Name;
                    LogWrapper.Debug($"大厅创建者的用户名: {hostname}");
                }
            }

            var localPort = await scfEntity.EasyTier
                .AddPortForwardAsync(scfEntity.HostInfo.Ip, port)
                .ConfigureAwait(false);
            var desc = hostname.IsNullOrWhiteSpace()
                ? string.Empty
                : Lang.Text("Link.Lobby.MotdDesc", hostname);

            var tcpPortForForward = NetworkHelper.NewTcpPort();
            McForward = new TcpForwardBuilder()
                .BindLocal((ushort)tcpPortForForward)
                .SetRemote(IPAddress.Loopback, (ushort)localPort)
                .Build();
            McForward.Start();
            McBroadcast = new BroadcastLocal(Lang.Text("Link.Lobby.MotdFormat", desc), tcpPortForForward);
            McBroadcast.Start();

            return scfEntity;
        }
        catch (ArgumentNullException e)
        {
            LogWrapper.Error(e, "大厅创建者的用户名为空");
        }
        catch (ArgumentException e)
        {
            if (e.Message.Contains("lobby code"))
            {
                LogWrapper.Error(e, "大厅编号无效");
            }
            else if (e.Message.Contains("hostname"))
            {
                LogWrapper.Error(e, "大厅创建者的用户名无效");
            }
            else
            {
                LogWrapper.Error(e, "在加入大厅时出现意外的无效参数");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            LogWrapper.Error(e, "在加入大厅时发生意外错误");
        }

        return null;
    }

    /// <summary>
    /// Launch a Scaffolding Server.
    /// </summary>
    /// <param name="username">Host user name.</param>
    /// <param name="port">Minecraft port.</param>
    /// <returns>Created <see cref="ScaffoldingServerEntity"/>.</returns>
    /// <remarks>
    /// Because of event handling of Scaffolding Server. You SHOULD start the server on your own.
    /// </remarks>
    public async Task<ScaffoldingServerEntity?> LaunchServerAsync(string username, int port)
    {
        await _RegisterLobbyAsync(true).ConfigureAwait(false);

        try
        {
            var scfEntity = ScaffoldingFactory.CreateServer(port, username);
            ScfServerEntity = scfEntity;
            IsHost = true;

            LogWrapper.Info("LobbyController", "Successfully to launch Scaffolding Server.");

            return scfEntity;
        }
        catch (Exception e)
        {
            LogWrapper.Error(e, "Occurred error when launching Scafolding Server.");
        }

        return null;
    }

    /// <summary>
    /// 检查主机的 MC 实例是否可用。
    /// </summary>
    public static async Task<bool> IsHostInstanceAvailableAsync(int port)
    {
        using var ping = McPingServiceFactory.CreateService("127.0.0.1", port);
        var info = await ping.PingAsync().ConfigureAwait(false);

        if (info is not null) return true;

        LogWrapper.Warn("Link", $"本地 MC 局域网实例 ({port}) 疑似已关闭");

        return false;
    }

    /// <summary>
    /// 退出大厅。这将同时关闭 EasyTier 和 MC 端口转发，需要自行清理 UI。
    /// </summary>
    public async Task<int> CloseAsync()
    {
        McForward?.Stop();
        McBroadcast?.Stop();
        try
        {
            if (ScfClientEntity is not null)
            {
                await ScfClientEntity.EasyTier.StopAsync().ConfigureAwait(false);
                await ScfClientEntity.Client.DisposeAsync().ConfigureAwait(false);
            }
            else if (ScfServerEntity is not null)
            {
                await ScfServerEntity.EasyTier.StopAsync().ConfigureAwait(false);
                await ScfServerEntity.Server.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            ScfClientEntity = null;
            ScfServerEntity = null;
        }
        return 0;
    }

    /// <summary>
    /// 把房间登记到联机大厅，让其他人能在列表里看到。
    /// <para>
    /// 上游这个方法叫 <c>_SendTelemetryAsync</c>，除了房间信息还会把账号邮箱、最近登录 IP、
    /// 机器识别码一并上报到 <c>pcl2ce.pysio.online</c>。PCL Lite 只保留房间发现必需的最小字段，
    /// 并且发送失败不会阻断联机——Scaffolding 与 EasyTier 是 P2P 的，登记只影响房间是否出现在列表里。
    /// </para>
    /// </summary>
    private static async Task _RegisterLobbyAsync(bool isHost)
    {
        var key = EnvironmentInterop.GetSecret("TelemetryKey");
        if (key is null)
        {
            LogWrapper.Warn("Link", "未配置大厅登记密钥，跳过房间登记（联机本身不受影响）");
            return;
        }

        var servers = Config.Link.CustomRelayServer;
        var serverType = Config.Link.ServerType;

        if (Config.Link.ServerType != 2)
        {
            servers = (
                from relay in ETRelay.RelayList
                where (relay.Type == ETRelayType.Selfhosted && serverType != 2) || (relay.Type == ETRelayType.Community && serverType == 1)
                select relay
            ).Aggregate(servers, (current, relay) => current + $"{relay.Url};");
        }

        // 只上报房间发现所需的字段：不再包含邮箱、最近 IP 与机器识别码
        JsonObject data = new()
        {
            ["Tag"] = "Link",
            ["NaidId"] = NaidProfile.Id,
            ["CustomName"] = Config.Link.Username,
            ["Servers"] = servers,
            ["IsHost"] = isHost
        };
        JsonObject sendData = new() { ["data"] = data };

        try
        {
            HttpContent httpContent = new StringContent(sendData.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await HttpRequest
                .CreatePost("https://pcl2ce.pysio.online/post")
                .WithContent(httpContent)
                .WithBearerToken(key)
                .SendAsync()
                .ConfigureAwait(false);

            if (response.IsSuccess)
                LogWrapper.Info("Link", "房间登记已提交");
            else
                LogWrapper.Warn("Link", "房间登记失败，跳过（联机本身不受影响）");
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Link", "房间登记失败，跳过（联机本身不受影响）");
        }
    }
}
