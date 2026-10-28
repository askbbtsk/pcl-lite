using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     Lunar IPC 端口协商的回归测试。
///     <para>
///         重点验证 1.5.4 新增的「主端口被占就退到备用端口」——PCL 能自己续期时不能轻易
///         把端口让给 Lunar 启动器（它开着但没登录就续不了）。用真实 TcpListener 占端口，
///         端口号都取系统分配的临时端口，不写死。
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
    public async Task StartAsync_FallsBackToAlternatePort()
    {
        var busyPort = OccupyPort();
        var fallbackPort = FindFreePort();

        var result = await LunarIpcServer.StartAsync(
            busyPort, "test-installation", NoRefresh, _ => { }, [fallbackPort]);

        using (result.Server)
        {
            Assert.AreEqual(LunarIpcPortState.Served, result.State,
                "主端口被占但备用端口可用时，应当由 PCL 接管：" + result.Message);
            Assert.AreEqual(fallbackPort, result.GamePort, "交给游戏的端口应当是备用端口");
            Assert.IsNotNull(result.Server);
            Assert.AreEqual(fallbackPort, result.Server!.Port);
        }
    }

    [TestMethod]
    public async Task StartAsync_WithoutFallbackYieldsToOccupier()
    {
        var busyPort = OccupyPort();

        // 占用方不响应 WebSocket 握手，探测超时后应当判定为「被别的程序占了」
        var result = await LunarIpcServer.StartAsync(
            busyPort, "test-installation", NoRefresh, _ => { });

        Assert.IsNull(result.Server);
        Assert.AreEqual(LunarIpcPortState.TakenByOther, result.State, result.Message);
        Assert.AreEqual(0, result.GamePort, "不该把被占的端口交给游戏");
    }
}
