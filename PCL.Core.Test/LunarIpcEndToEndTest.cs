using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     IPC 的端到端测试：起真实服务、按游戏的方式握手、发真帧、解响应。
///     <para>
///         单测 <see cref="LunarIpcDispatcherTest" /> 只覆盖「分派 + 编码」，
///         这里补上「WebSocket 帧 + 信封 + request_id 回带」那一段 ——
///         以前出过 request_id 拼错导致游戏侧永远等超时的问题，光靠单测抓不到。
///     </para>
/// </summary>
[TestClass]
public class LunarIpcEndToEndTest
{
    private sealed class RecordingHost : ILunarIpcHost
    {
        public List<string> Opened { get; } = [];

        public bool OpenUrl(string url, LunarUrlInitiator initiator, LunarOpenType openType)
        {
            Opened.Add(url);
            return true;
        }

        public void RequestAddAccount() { }

        public void RequestLaunchGame(string version, string module, string serverIp, int launchSource) { }
    }

    private static Task<LunarRefreshResult> NoRefresh(Guid uuid, CancellationToken token) =>
        Task.FromResult(LunarRefreshResult.Ok("测试"));

    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>客户端发给服务端的帧必须加掩码（RFC 6455）。</summary>
    private static async Task WriteMaskedFrameAsync(NetworkStream stream, byte[] payload)
    {
        var mask = RandomNumberGenerator.GetBytes(4);
        var header = new List<byte> { 0x82 };

        switch (payload.Length)
        {
            case <= 125:
                header.Add((byte)(0x80 | payload.Length));
                break;
            case <= ushort.MaxValue:
                header.Add(0x80 | 126);
                header.Add((byte)(payload.Length >> 8));
                header.Add((byte)payload.Length);
                break;
            default:
                header.Add(0x80 | 127);
                for (var i = 7; i >= 0; i--) header.Add((byte)((long)payload.Length >> (8 * i)));
                break;
        }

        header.AddRange(mask);
        await stream.WriteAsync(header.ToArray());
        for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    private static async Task<byte[]?> ReadServerFrameAsync(NetworkStream stream)
    {
        var header = new byte[2];
        if (!await ReadExactlyAsync(stream, header)) return null;

        var opcode = header[0] & 0x0F;
        long length = header[1] & 0x7F;

        if (length == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactlyAsync(stream, ext)) return null;
            length = (ext[0] << 8) | ext[1];
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactlyAsync(stream, ext)) return null;
            length = 0;
            foreach (var b in ext) length = (length << 8) | b;
        }

        if (length < 0 || length > 1 << 20) return null;

        var payload = new byte[length];
        if (length > 0 && !await ReadExactlyAsync(stream, payload)) return null;

        // 服务端不会加掩码；ping / pong 之类直接跳过
        return opcode is 0x1 or 0x2 or 0x0 ? payload : [];
    }

    private static async Task<NetworkStream> HandshakeAsync(TcpClient client, int port)
    {
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                      "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                      $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        // 读到 \r\n\r\n 为止
        var buffer = new MemoryStream();
        var single = new byte[1];
        while (buffer.Length < 8192)
        {
            if (await stream.ReadAsync(single) == 0) break;
            buffer.WriteByte(single[0]);
            if (buffer.Length < 4) continue;
            var data = buffer.GetBuffer();
            var tail = (int)buffer.Length - 4;
            if (data[tail] == '\r' && data[tail + 1] == '\n' &&
                data[tail + 2] == '\r' && data[tail + 3] == '\n') break;
        }

        var text = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        Assert.IsTrue(text.StartsWith("HTTP/1.1 101", StringComparison.Ordinal), "握手失败：" + text);
        return stream;
    }

    private static byte[] BuildRequest(byte[] requestId, string service, string method, byte[] input)
    {
        using var stream = new MemoryStream();
        LunarIpcProtocol.WriteMessageField(stream, 1, requestId);
        LunarIpcProtocol.WriteStringField(stream, 2, service);
        LunarIpcProtocol.WriteStringField(stream, 3, method);
        LunarIpcProtocol.WriteMessageField(stream, 4, input);
        return stream.ToArray();
    }

    [TestMethod]
    public async Task OpenUrl_ShouldRoundTripThroughRealWebSocket()
    {
        var host = new RecordingHost();
        var port = FindFreePort();

        var result = await LunarIpcServer.StartAsync(port, "test-installation", NoRefresh, _ => { },
            host: host);

        using (result.Server)
        {
            Assert.AreEqual(LunarIpcPortState.Served, result.State, result.Message);

            using var client = new TcpClient();
            var stream = await HandshakeAsync(client, port);

            var requestId = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            var input = new MemoryStream();
            LunarIpcProtocol.WriteStringField(input, 1, "https://example.com/help");
            LunarIpcProtocol.WriteInt32Field(input, 2, (int)LunarUrlInitiator.Blog);

            var payload = BuildRequest(requestId, LunarIpcProtocol.BrowserService, "OpenUrl",
                input.ToArray());
            await WriteMaskedFrameAsync(stream, payload);

            var response = await ReadServerFrameAsync(stream);
            Assert.IsNotNull(response, "服务端没有回帧");

            // GameboundIPCMessage { rpc_response = 1 { request_id = 1, output = 2 } }
            Assert.IsTrue(LunarIpcProtocol.TryGetMessage(response!, 1, out var rpcResponse),
                "响应里没有 rpc_response");
            Assert.IsTrue(LunarIpcProtocol.TryGetMessage(rpcResponse, 1, out var echoed),
                "响应里没有 request_id");
            CollectionAssert.AreEqual(requestId, echoed, "request_id 必须原样带回");

            Assert.AreEqual(1, host.Opened.Count);
            Assert.AreEqual("https://example.com/help", host.Opened[0]);
        }
    }

    [TestMethod]
    public async Task RefreshAccount_ShouldStillBeHandledByServer()
    {
        var refreshCalls = 0;
        var port = FindFreePort();
        var uuid = Guid.Parse("069a79f4-44e9-4726-a5be-fca90e38aaf5");

        var result = await LunarIpcServer.StartAsync(port, "test-installation",
            (id, token) =>
            {
                refreshCalls++;
                Assert.AreEqual(uuid, id);
                return Task.FromResult(LunarRefreshResult.Ok("测试续期"));
            }, _ => { });

        using (result.Server)
        {
            using var client = new TcpClient();
            var stream = await HandshakeAsync(client, port);

            // RefreshAccountRequest { Uuid uuid = 1 }，Uuid 是两个 fixed64
            // Guid.ToByteArray 的前三段是小端，这里按 Java 的大端顺序手工拼
            var hex = uuid.ToString("N");
            var high = Convert.ToUInt64(hex[..16], 16);
            var low = Convert.ToUInt64(hex[16..], 16);

            var inner = new MemoryStream();
            var tagHigh = new byte[] { 0x09 };
            var tagLow = new byte[] { 0x11 };
            inner.Write(tagHigh);
            inner.Write(BitConverter.GetBytes(high));
            inner.Write(tagLow);
            inner.Write(BitConverter.GetBytes(low));

            var input = new MemoryStream();
            LunarIpcProtocol.WriteMessageField(input, 1, inner.ToArray());

            var requestId = new byte[] { 1, 1, 2, 2 };
            await WriteMaskedFrameAsync(stream,
                BuildRequest(requestId, LunarIpcProtocol.AuthService, "RefreshAccount", input.ToArray()));

            var response = await ReadServerFrameAsync(stream);
            Assert.IsNotNull(response);
            Assert.IsTrue(LunarIpcProtocol.TryGetMessage(response!, 1, out var rpc));
            Assert.IsTrue(LunarIpcProtocol.TryGetMessage(rpc, 1, out var echoed));
            CollectionAssert.AreEqual(requestId, echoed);

            // RefreshAccountResponse { success = 1 }
            Assert.IsTrue(LunarIpcProtocol.TryGetMessage(rpc, 2, out var output));
            Assert.IsTrue(output.Length > 0, "续期响应应当带 success 字段");
            Assert.AreEqual(1, refreshCalls);
        }
    }

    [TestMethod]
    public async Task SendPush_ShouldReachConnectedGame()
    {
        var port = FindFreePort();
        var result = await LunarIpcServer.StartAsync(port, "test-installation", NoRefresh, _ => { });

        using (result.Server)
        {
            Assert.IsNotNull(result.Server);
            using var client = new TcpClient();
            var stream = await HandshakeAsync(client, port);

            // 服务端是在写完 101 之后才把连接登记进表的，握手刚返回时可能还差一拍
            for (var i = 0; i < 100 && result.Server!.ConnectionCount == 0; i++)
                await Task.Delay(10);

            var sent = await result.Server!.SendPushAsync(LunarIpcPush.ExitGame);
            Assert.AreEqual(1, sent, "应当送到唯一那条连接上");

            var frame = await ReadServerFrameAsync(stream);
            Assert.IsNotNull(frame, "客户端没有收到推送帧");

            // GameboundIPCMessage { push_notification = 2 { type_url = 1, value = 2 } }
            Assert.IsTrue(LunarIpcProtocol.TryGetMessage(frame!, 2, out var any),
                "推送必须放在 push_notification 里，不能借用 rpc_response");
            Assert.IsTrue(LunarIpcProtocol.TryGetString(any, 1, out var typeUrl));
            Assert.AreEqual("type.googleapis.com/lunarclient.gameipc.launch.v1.ExitGamePush", typeUrl);
        }
    }

    [TestMethod]
    public async Task SendPush_ShouldReturnZeroWhenNobodyConnected()
    {
        var port = FindFreePort();
        var result = await LunarIpcServer.StartAsync(port, "test-installation", NoRefresh, _ => { });

        using (result.Server)
        {
            Assert.AreEqual(0, await result.Server!.SendPushAsync(LunarIpcPush.ExitGame),
                "没有连接时应当老实返回 0，而不是假装送到了");
        }
    }
}
