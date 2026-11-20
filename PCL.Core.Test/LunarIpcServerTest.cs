using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     Lunar IPC 端口协商的回归测试。
///     <para>
///         验证固定 IPC 端口被占用时不接管、不换端口。用真实 TcpListener 占端口，
///         端口号取系统分配的临时端口。
///     </para>
/// </summary>
[TestClass]
public class LunarIpcServerTest
{
    private readonly List<TcpListener> _blockers = [];

    [TestCleanup]
    public void TearDown()
    {
        foreach (var blocker in _blockers)
        {
            try
            {
                blocker.Stop();
            }
            catch (SocketException)
            {
                // 已经停了
            }
        }

        _blockers.Clear();
    }

    private int OccupyPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _blockers.Add(listener);
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static Task<LunarRefreshResult> NoRefresh(Guid uuid, CancellationToken token) =>
        Task.FromResult(LunarRefreshResult.Ok("测试"));

    [TestMethod]
    public async Task RemoteWebpagesCannotOpenLunarIpc()
    {
        var port = FindFreePort();
        var result = await LunarIpcServer.StartAsync(port, "test-installation", NoRefresh, _ => { });
        using var server = result.Server;
        Assert.IsNotNull(server);
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", "https://untrusted.invalid");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<WebSocketException>(() =>
            client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), timeout.Token));
    }

    [TestMethod]
    public async Task StartAsync_WhenPortIsOccupiedDoesNotTakeOverOrFallback()
    {
        var busyPort = OccupyPort();
        var fallbackPort = FindFreePort();

        var result = await LunarIpcServer.StartAsync(
            busyPort, "test-installation", NoRefresh, _ => { }, [fallbackPort]);

        Assert.IsNull(result.Server);
        Assert.AreEqual(LunarIpcPortState.TakenByLauncher, result.State, result.Message);
        Assert.AreEqual(busyPort, result.GamePort, "由 Lunar 启动器占用时应把原端口交给游戏");
        Assert.AreEqual($"端口 {busyPort} 被占用，不接管 Lunar IPC", result.Message);

        using var fallbackProbe = new TcpListener(IPAddress.Loopback, fallbackPort);
        fallbackProbe.Start();
    }

    [TestMethod]
    public async Task StartAsync_WhenPortIsOccupiedTreatsItAsLunarLauncher()
    {
        var busyPort = OccupyPort();

        var result = await LunarIpcServer.StartAsync(
            busyPort, "test-installation", NoRefresh, _ => { });

        Assert.IsNull(result.Server);
        Assert.AreEqual(LunarIpcPortState.TakenByLauncher, result.State, result.Message);
        Assert.AreEqual(busyPort, result.GamePort, "固定端口被占时不改用其他端口");
    }
}
