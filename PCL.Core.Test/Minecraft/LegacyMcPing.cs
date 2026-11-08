using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.McPing;

namespace PCL.Core.Test.Minecraft;

/// <summary>
/// 旧版协议（1.6 及以下）服务器探测的 loopback 夹具测试。
/// 夹具完全由本地 <see cref="TcpListener"/> 扮演服务器，不访问外网。
/// </summary>
[TestClass]
public class LegacyMcPingTest
{
    [TestMethod]
    public async Task PingTest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var serverTask = _RespondAsync(listener, protocol: 51, version: "1.6.4",
                motd: "PCL legacy test", online: 3, max: 20, TestContext.CancellationToken);
            using var ping = new LegacyMcPingService(endpoint, timeout: 3000);

            var result = await ping.PingAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await serverTask.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsNotNull(result);
            Assert.AreEqual("1.6.4", result.Version.Name);
            Assert.AreEqual(51, result.Version.Protocol);
            Assert.AreEqual("PCL legacy test", result.Description);
            Assert.AreEqual(3, result.Players.Online);
            Assert.AreEqual(20, result.Players.Max);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 服务端收到 0xFE 0x01 后回 0xFF 开头的 UTF-16BE 应答；
    /// 客户端一直读到连接关闭，所以写完后必须断开。
    /// </summary>
    [TestMethod]
    public async Task PingReturnsNullWhenResponseIsGarbage()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var probe = new byte[2];
                await stream.ReadExactlyAsync(probe, TestContext.CancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("not a legacy response at all"), TestContext.CancellationToken).ConfigureAwait(false);
            }, TestContext.CancellationToken);

            using var ping = new LegacyMcPingService(endpoint, timeout: 3000);
            var result = await ping.PingAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await serverTask.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsNull(result, "首字节不是 0xFF 时应返回 null 而不是抛异常。");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void FactoryCreatesLegacyService()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 25565);
        using var byEndpoint = McPingServiceFactory.CreateLegacyService(endpoint, timeout: 1234);
        Assert.IsInstanceOfType<LegacyMcPingService>(byEndpoint);
        Assert.AreEqual(1234, byEndpoint.Timeout);

        using var byAddress = McPingServiceFactory.CreateLegacyService("127.0.0.1", 25566, timeout: 5678);
        Assert.IsInstanceOfType<LegacyMcPingService>(byAddress);
        Assert.AreEqual(25566, byAddress.Endpoint.Port);
        Assert.AreEqual("127.0.0.1", byAddress.Host);
    }

    private static async Task _RespondAsync(
        TcpListener listener,
        int protocol,
        string version,
        string motd,
        int online,
        int max,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var probe = new byte[2];
        await stream.ReadExactlyAsync(probe, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual((byte)0xFE, probe[0], "期望旧版探测包以 0xFE 开头。");
        Assert.AreEqual((byte)0x01, probe[1], "期望 0xFE 0x01（1.4+ 的旧版探测）。");

        await stream.WriteAsync(_BuildLegacyResponse(protocol, version, motd, online, max), cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 组装经典（1.4–1.6）应答：0xFF + 2 字节字符数（大端）+ UTF-16BE 的
    /// "§1\0协议号\0版本名\0MOTD\0在线数\0上限"。
    /// </summary>
    private static byte[] _BuildLegacyResponse(int protocol, string version, string motd, int online, int max)
    {
        var payload = string.Join("\0", ["§1", protocol.ToString(), version, motd, online.ToString(), max.ToString()]);
        var body = Encoding.BigEndianUnicode.GetBytes(payload);

        var bytes = new List<byte>(body.Length + 3) { 0xFF };
        bytes.Add((byte)(payload.Length >> 8));
        bytes.Add((byte)(payload.Length & 0xFF));
        bytes.AddRange(body);
        return bytes.ToArray();
    }

    public TestContext TestContext { get; set; } = null!;
}
