using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.App.Lunar;

/// <summary>
///     PCL 自己充当 Lunar 的本地 IPC 服务端。
///     <para>
///         游戏进程里 <c>lunar.jar</c> 的 AccountManager 在令牌过期时会通过
///         <c>AuthService/RefreshAccount</c> 找启动器要续期，而这条链路只走
///         <c>ws://127.0.0.1:&lt;--ipcPort&gt;</c>。PCL 不经过 Lunar 启动器启动游戏时
///         没人应答，游戏就会弹 <c>Invalid session</c>。
///     </para>
///     <para>
///         这里的做法是：启动前先抢 <see cref="DefaultPort" />。抢到了就自己当服务端；
///         抢不到说明 Lunar 启动器正开着（它固定占 28190），那就让位，把端口原样交给游戏，
///         由启动器去处理。协议细节见 <c>docs/LUNAR.md</c>。
///     </para>
/// </summary>
public sealed class LunarIpcServer : IDisposable
{
    /// <summary>Lunar 启动器写死的 IPC 端口。</summary>
    public const int DefaultPort = 28190;

    /// <summary>RFC 6455 规定的握手魔术串。</summary>
    private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>单帧上限。协议里的消息都是几百字节，超过这个数一定是别的东西。</summary>
    private const int MaxFrameBytes = 1 << 20;

    private const int OpcodeText = 0x1;
    private const int OpcodeBinary = 0x2;
    private const int OpcodeClose = 0x8;
    private const int OpcodePing = 0x9;
    private const int OpcodePong = 0xA;

    private readonly Func<Guid, CancellationToken, Task<LunarRefreshResult>> _refresh;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly List<TcpClient> _connections = [];
    private readonly TcpListener _listener;

    private LunarIpcServer(TcpListener listener, int port, string installationId,
        Func<Guid, CancellationToken, Task<LunarRefreshResult>> refresh, Action<string> log)
    {
        _listener = listener;
        _refresh = refresh;
        _log = log;
        Port = port;
        InstallationId = installationId;
    }

    /// <summary>实际监听的端口。</summary>
    public int Port { get; }

    /// <summary>PCL 这次启动用的 <c>--installationId</c>，用来核对握手里的值。</summary>
    public string InstallationId { get; }

    /// <summary>当前连着的游戏端数量。</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_connections) return _connections.Count;
        }
    }

    /// <summary>
    ///     抢占 <paramref name="port" /> 并开始监听。
    ///     <para>
    ///         端口被占用时不算错误：先探一下对方是不是 WebSocket 服务（Lunar 启动器就是），
    ///         是的话把端口原样交给游戏，由启动器负责续期；不是的话让游戏别连，
    ///         免得连到不相干的东西上。
    ///     </para>
    /// </summary>
    public static async Task<LunarIpcStartResult> StartAsync(
        int port,
        string installationId,
        Func<Guid, CancellationToken, Task<LunarRefreshResult>> refresh,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            var isWebSocket = await LooksLikeWebSocketAsync(port, cancellationToken).ConfigureAwait(false);
            return isWebSocket
                ? new LunarIpcStartResult(null, port, LunarIpcPortState.TakenByLauncher,
                    $"端口 {port} 已被占用，且对方是 WebSocket 服务 —— 应该是 Lunar 启动器在运行，" +
                    "续期交给它处理")
                : new LunarIpcStartResult(null, 0, LunarIpcPortState.TakenByOther,
                    $"端口 {port} 被其他程序占用，且不响应 WebSocket，本次不启用 IPC");
        }
        catch (Exception ex) when (ex is SocketException or UnauthorizedAccessException)
        {
            return new LunarIpcStartResult(null, 0, LunarIpcPortState.TakenByOther,
                $"无法监听端口 {port}：{ex.Message}");
        }

        var server = new LunarIpcServer(listener, port, installationId, refresh, log);
        server.StartAcceptLoop();
        return new LunarIpcStartResult(server, port, LunarIpcPortState.Served,
            $"已在 127.0.0.1:{port} 接管 Lunar IPC");
    }

    private void StartAcceptLoop() => _ = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            lock (_connections) _connections.Add(client);
            _ = Task.Run(() => HandleConnectionAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken token)
    {
        var name = "未知客户端";
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();

            var headers = await ReadHttpRequestAsync(stream, token).ConfigureAwait(false);
            if (headers is null)
            {
                Log("连接在握手阶段就断了，忽略");
                return;
            }

            if (!headers.TryGetValue("sec-websocket-key", out var key) || string.IsNullOrWhiteSpace(key))
            {
                await WriteHttpErrorAsync(stream, "400 Bad Request", "缺少 Sec-WebSocket-Key", token)
                    .ConfigureAwait(false);
                return;
            }

            // 握手里的安装 ID 只做核对：PCL 和 Lunar 启动器读的是同一个
            // ~/.lunarclient/launcher-cache/installation-id，正常情况下一定相等
            if (headers.TryGetValue("lc-handshake", out var handshakeBase64) &&
                TryDecodeBase64(handshakeBase64, out var handshakeBytes) &&
                LunarIpcProtocol.TryDecodeHandshake(handshakeBytes, out var launchId,
                    out var processId, out var installationId))
            {
                name = $"launch {launchId[..Math.Min(8, launchId.Length)]} / pid {processId}";
                if (!string.IsNullOrEmpty(installationId) &&
                    !string.Equals(installationId, InstallationId, StringComparison.OrdinalIgnoreCase))
                    Log($"握手里的 installationId（{installationId}）与本次启动用的（{InstallationId}）不一致，仍然接受");
                Log($"游戏端已连接：{name}");
            }
            else
            {
                Log("游戏端没有带 lc-handshake，仍然接受（不影响 RPC）");
            }

            await AcceptWebSocketAsync(stream, key, token).ConfigureAwait(false);
            await FrameLoopAsync(stream, name, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Log($"连接异常断开（{name}）：{ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"处理 IPC 连接时出错（{name}）：{ex}");
        }
        finally
        {
            lock (_connections) _connections.Remove(client);
            try
            {
                client.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 已经关了
            }
        }
    }

    private async Task FrameLoopAsync(NetworkStream stream, string name, CancellationToken token)
    {
        var fragments = new MemoryStream();

        while (!token.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, token).ConfigureAwait(false);
            if (frame is null) return;

            switch (frame.Value.Opcode)
            {
                case OpcodeClose:
                    await WriteFrameAsync(stream, OpcodeClose, ReadOnlyMemory<byte>.Empty, token)
                        .ConfigureAwait(false);
                    Log($"游戏端断开：{name}");
                    return;

                case OpcodePing:
                    await WriteFrameAsync(stream, OpcodePong, frame.Value.Payload, token).ConfigureAwait(false);
                    continue;

                case OpcodePong:
                    continue;

                case OpcodeText:
                case OpcodeBinary:
                    fragments.SetLength(0);
                    fragments.Write(frame.Value.Payload);
                    break;

                default:
                    // 0x0 续帧
                    if (frame.Value.Opcode != 0) continue;
                    fragments.Write(frame.Value.Payload);
                    break;
            }

            if (!frame.Value.Fin) continue;

            var payload = fragments.ToArray();
            if (payload.Length == 0) continue;

            await DispatchAsync(stream, payload, token).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(NetworkStream stream, byte[] payload, CancellationToken token)
    {
        if (!LunarIpcProtocol.TryDecodeRequest(payload, out var request))
        {
            Log($"收到无法解析的 IPC 消息（{payload.Length} 字节），忽略");
            return;
        }

        var isRefresh = request.Service == LunarIpcProtocol.AuthService &&
                        request.Method == LunarIpcProtocol.RefreshAccountMethod;

        if (!isRefresh)
        {
            // 其余服务（Launch / Chat / Browser / Tebex …）PCL 都没有实现。
            // 回一个空响应，让游戏的 RPC 立刻结束，而不是挂着等超时。
            Log($"未实现的 IPC 调用：{request.Service}/{request.Method}，回空响应");
            await SendAsync(stream, request.RequestId, [], token).ConfigureAwait(false);
            return;
        }

        if (!LunarIpcProtocol.TryDecodeRefreshAccountRequest(request.Input, out var uuid))
        {
            Log("RefreshAccount 的入参里没有可用的 UUID");
            await SendRefreshResultAsync(stream, request.RequestId,
                LunarRefreshResult.Fail(LunarAccountError.Unspecified, "入参无法解析"), token)
                .ConfigureAwait(false);
            return;
        }

        Log($"游戏请求续期账号 {uuid}");

        if (!await _refreshGate.WaitAsync(0, token).ConfigureAwait(false))
        {
            Log("已经有一次续期在进行，回 ALREADY_REFRESHING");
            await SendRefreshResultAsync(stream, request.RequestId,
                LunarRefreshResult.Fail(LunarAccountError.AlreadyRefreshing, "已有续期在进行"), token)
                .ConfigureAwait(false);
            return;
        }

        LunarRefreshResult result;
        try
        {
            result = await _refresh(uuid, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"续期过程中抛异常：{ex}");
            result = LunarRefreshResult.Fail(LunarAccountError.Unspecified, ex.Message);
        }
        finally
        {
            _refreshGate.Release();
        }

        Log(result.Success
            ? $"账号 {uuid} 续期成功：{result.Message}"
            : $"账号 {uuid} 续期失败（{result.Error}）：{result.Message}");

        await SendRefreshResultAsync(stream, request.RequestId, result, token).ConfigureAwait(false);
    }

    private static Task SendRefreshResultAsync(NetworkStream stream, byte[] requestId,
        LunarRefreshResult result, CancellationToken token)
    {
        var output = LunarIpcProtocol.EncodeRefreshAccountResponse(result.Success, result.Error);
        return SendAsync(stream, requestId, output, token);
    }

    private static Task SendAsync(NetworkStream stream, byte[] requestId, byte[] output,
        CancellationToken token)
    {
        var message = LunarIpcProtocol.EncodeRpcResponse(requestId, output);
        return WriteFrameAsync(stream, OpcodeBinary, message, token);
    }

    #region WebSocket 握手与帧

    /// <summary>读完 HTTP 请求头（到 <c>\r\n\r\n</c> 为止），返回小写键的字典。</summary>
    private static async Task<Dictionary<string, string>?> ReadHttpRequestAsync(
        NetworkStream stream, CancellationToken token)
    {
        var buffer = new MemoryStream();
        var single = new byte[1];

        while (buffer.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(single, token).ConfigureAwait(false);
            if (read == 0) return null;

            buffer.WriteByte(single[0]);
            if (buffer.Length < 4) continue;

            var tail = buffer.GetBuffer();
            var offset = (int)buffer.Length - 4;
            if (tail[offset] == '\r' && tail[offset + 1] == '\n' &&
                tail[offset + 2] == '\r' && tail[offset + 3] == '\n')
                break;
        }

        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0) continue;
            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        return headers;
    }

    private static Task WriteHttpErrorAsync(NetworkStream stream, string status, string reason,
        CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(reason);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                   $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        var all = new byte[headBytes.Length + body.Length];
        headBytes.CopyTo(all, 0);
        body.CopyTo(all, headBytes.Length);
        return stream.WriteAsync(all, token).AsTask();
    }

    private static async Task AcceptWebSocketAsync(NetworkStream stream, string key,
        CancellationToken token)
    {
        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketMagic)));

        var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                       "Upgrade: websocket\r\n" +
                       "Connection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private readonly record struct WebSocketFrame(bool Fin, int Opcode, byte[] Payload);

    private static async Task<WebSocketFrame?> ReadFrameAsync(NetworkStream stream,
        CancellationToken token)
    {
        var header = new byte[2];
        if (!await ReadExactlyAsync(stream, header, token).ConfigureAwait(false)) return null;

        var fin = (header[0] & 0x80) != 0;
        var opcode = header[0] & 0x0F;
        var masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;

        if (length == 126)
        {
            var extended = new byte[2];
            if (!await ReadExactlyAsync(stream, extended, token).ConfigureAwait(false)) return null;
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            if (!await ReadExactlyAsync(stream, extended, token).ConfigureAwait(false)) return null;
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(extended);
        }

        if (length < 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"WebSocket 帧长度异常：{length}");

        var mask = Array.Empty<byte>();
        if (masked)
        {
            mask = new byte[4];
            if (!await ReadExactlyAsync(stream, mask, token).ConfigureAwait(false)) return null;
        }

        var payload = new byte[length];
        if (length > 0 && !await ReadExactlyAsync(stream, payload, token).ConfigureAwait(false)) return null;

        if (masked)
            for (var i = 0; i < payload.Length; i++)
                payload[i] ^= mask[i % 4];

        return new WebSocketFrame(fin, opcode, payload);
    }

    private static async Task WriteFrameAsync(NetworkStream stream, int opcode,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var header = new List<byte>(10) { (byte)(0x80 | opcode) };

        switch (payload.Length)
        {
            case <= 125:
                header.Add((byte)payload.Length);
                break;
            case <= ushort.MaxValue:
                header.Add(126);
                header.Add((byte)(payload.Length >> 8));
                header.Add((byte)payload.Length);
                break;
            default:
                header.Add(127);
                for (var i = 7; i >= 0; i--) header.Add((byte)((long)payload.Length >> (8 * i)));
                break;
        }

        await stream.WriteAsync(header.ToArray(), token).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer,
        CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    #endregion

    /// <summary>
    ///     端口被占时判断对方是不是 WebSocket 服务。Lunar 启动器的 IPC 服务就是，
    ///     探测完立刻断开，它那边只会记一条「连接已关闭」。
    /// </summary>
    private static async Task<bool> LooksLikeWebSocketAsync(int port, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
                          "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                          $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";

            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token).ConfigureAwait(false);

            var buffer = new byte[16];
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            return read >= 12 &&
                   Encoding.ASCII.GetString(buffer, 0, read).Contains("101", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    private static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value.Trim());
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private void Log(string message) => _log("[Lunar IPC] " + message);

    public void Dispose()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过
        }

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // 已经停了
        }

        lock (_connections)
        {
            foreach (var client in _connections)
            {
                try
                {
                    client.Close();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // 忽略
                }
            }

            _connections.Clear();
        }

        _refreshGate.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>IPC 端口的最终状态。</summary>
public enum LunarIpcPortState
{
    /// <summary>端口空闲，PCL 自己接管了 IPC。</summary>
    Served,

    /// <summary>端口被一个 WebSocket 服务占着，几乎一定是 Lunar 启动器。</summary>
    TakenByLauncher,

    /// <summary>端口被占，但对方不像 WebSocket 服务。</summary>
    TakenByOther
}

/// <summary>启动 IPC 服务的结果。</summary>
/// <param name="Server">接管成功时的服务实例，否则为 <c>null</c>。</param>
/// <param name="GamePort">要传给游戏 <c>--ipcPort</c> 的端口，0 表示不启用。</param>
/// <param name="State">端口状态。</param>
/// <param name="Message">给日志看的说明。</param>
public sealed record LunarIpcStartResult(
    LunarIpcServer? Server,
    int GamePort,
    LunarIpcPortState State,
    string Message);
