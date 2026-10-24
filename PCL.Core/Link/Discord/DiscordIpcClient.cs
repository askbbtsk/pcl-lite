using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace PCL.Core.Link.Discord;

/// <summary>
/// Discord IPC 的最小实现。
/// <para>
/// Discord 桌面端会在本机开一组命名管道 <c>discord-ipc-0</c> 到 <c>discord-ipc-9</c>，
/// 协议是「4 字节小端 op + 4 字节小端长度 + UTF-8 JSON」，不需要任何 SDK。
/// 这里刻意不引第三方 NuGet：整个传输层不到 200 行，比自己维护一个 RPC 库省心。
/// </para>
/// <para>
/// 已知的 op：0 握手、1 帧、2 关闭、3 Ping、4 Pong。发送 <c>SET_ACTIVITY</c> 走 op 1。
/// </para>
/// </summary>
public sealed class DiscordIpcClient : IDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;

    /// <summary>最多尝试 10 个管道槽位，与 Discord 官方 SDK 一致。</summary>
    private const int MaxPipeIndex = 10;

    private readonly object _writeLock = new();
    private NamedPipeClientStream? _pipe;
    private Thread? _reader;
    private volatile bool _disposed;
    private string? _lastError;

    /// <summary>最近一次错误信息，供设置页展示。</summary>
    public string? LastError => _lastError;

    /// <summary>管道是否还连着。</summary>
    public bool IsConnected => _pipe is { IsConnected: true };

    /// <summary>连接断开时触发，参数是原因。</summary>
    public event Action<string>? Disconnected;

    /// <summary>
    /// 连接本机 Discord 并完成握手。
    /// </summary>
    /// <param name="clientId">Discord 应用的 Application ID。</param>
    /// <param name="error">失败原因。</param>
    /// <param name="pipeIndex">
    /// 指定管道槽位；-1 表示从 0 开始扫描。多开 Discord 或做协议测试时用得上。
    /// </param>
    /// <returns>是否连接成功。</returns>
    public bool Connect(string clientId, out string? error, int pipeIndex = -1)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            error = "未填写 Discord 应用 ID";
            _lastError = error;
            return false;
        }

        _lastError = null;
        var first = pipeIndex < 0 ? 0 : pipeIndex;
        var last = pipeIndex < 0 ? MaxPipeIndex - 1 : pipeIndex;

        for (var i = first; i <= last; i++)
        {
            if (_disposed) { error = "客户端已释放"; return false; }

            var pipe = new NamedPipeClientStream(
                ".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                // 只等 200ms：Discord 没开时没必要在每个槽位上耗一秒
                pipe.Connect(200);
            }
            catch (Exception)
            {
                pipe.Dispose();
                continue;
            }

            if (!_Handshake(pipe, clientId, out error))
            {
                pipe.Dispose();
                _lastError = error;
                return false;
            }

            _pipe = pipe;
            _StartReader();
            return true;
        }

        error = "没有找到 Discord 进程（请确认 Discord 桌面端正在运行）";
        _lastError = error;
        return false;
    }

    private bool _Handshake(NamedPipeClientStream pipe, string clientId, out string? error)
    {
        error = null;
        try
        {
            var payload = new JsonObject
            {
                ["v"] = 1,
                ["client_id"] = clientId
            };
            _WriteFrame(pipe, OpHandshake, payload.ToJsonString());

            // 握手响应可能是 READY，也可能是 ERROR。读不到响应不能当成功——
            // 否则会连上一个不说话的管道，后面每次 SET_ACTIVITY 都失败却看不出原因。
            if (!_ReadFrame(pipe, 3000, out var op, out var body))
            {
                error = "Discord 没有响应握手";
                return false;
            }

            if (op == OpFrame && body is not null
                && body.Contains("\"evt\":\"ERROR\"", StringComparison.OrdinalIgnoreCase))
            {
                error = _ExtractMessage(body) ?? "握手被 Discord 拒绝（应用 ID 可能无效）";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"握手失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 下发一次 activity。传 <see langword="null"/> 表示清除（关闭 Rich Presence）。
    /// </summary>
    /// <param name="activityJson">activity 对象的 JSON；为 null 时清除。</param>
    /// <param name="error">失败原因。</param>
    public bool SetActivity(string? activityJson, out string? error)
    {
        error = null;
        var pipe = _pipe;
        if (pipe is not { IsConnected: true })
        {
            error = "未连接";
            return false;
        }

        try
        {
            var args = new JsonObject
            {
                ["pid"] = Environment.ProcessId
            };
            if (activityJson is null)
                args["activity"] = null;
            else
                args["activity"] = JsonNode.Parse(activityJson);

            var payload = new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = args,
                ["nonce"] = Guid.NewGuid().ToString()
            };

            lock (_writeLock)
                _WriteFrame(pipe, OpFrame, payload.ToJsonString());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _lastError = error;
            return false;
        }
    }

    private void _StartReader()
    {
        _reader = new Thread(_ReadLoop)
        {
            IsBackground = true,
            Name = "Discord-RPC-Reader"
        };
        _reader.Start();
    }

    /// <summary>
    /// 后台读循环。必须持续把 Discord 的响应读走，否则管道缓冲区满了会卡住对方。
    /// </summary>
    private void _ReadLoop()
    {
        while (!_disposed)
        {
            var pipe = _pipe;
            if (pipe is not { IsConnected: true }) break;

            try
            {
                if (!_ReadFrame(pipe, -1, out var op, out var body))
                {
                    // 管道被对端关掉时 Read 返回 0，这里也要通知上层，否则状态会停在"已连接"
                    if (!_disposed) Disconnected?.Invoke("Discord 关闭了连接");
                    return;
                }

                if (op == OpPing)
                {
                    lock (_writeLock)
                        _WriteFrame(pipe, OpPong, body ?? "{}");
                    continue;
                }

                if (op == OpClose)
                {
                    Disconnected?.Invoke("Discord 关闭了连接");
                    return;
                }

                if (op == OpFrame && body is not null
                    && body.Contains("\"evt\":\"ERROR\"", StringComparison.OrdinalIgnoreCase))
                {
                    _lastError = _ExtractMessage(body) ?? "Discord 返回错误";
                    // 常见于 activity 字段超长或含非法字符，断开后由上层决定是否重连
                    Disconnected?.Invoke(_lastError!);
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!_disposed) Disconnected?.Invoke($"读取失败：{ex.Message}");
                return;
            }
        }
    }

    #region 帧读写

    private static void _WriteFrame(Stream stream, int op, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BitConverter.GetBytes(op).CopyTo(header, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(header, 4);
        stream.Write(header, 0, header.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    /// <param name="timeoutMs">-1 表示一直等。</param>
    private static bool _ReadFrame(Stream stream, int timeoutMs, out int op, out string? json)
    {
        op = -1;
        json = null;
        var header = new byte[8];
        if (!_ReadExactly(stream, header, 0, 8, timeoutMs)) return false;

        op = BitConverter.ToInt32(header, 0);
        var length = BitConverter.ToInt32(header, 4);
        if (length is < 0 or > 1024 * 1024) return false;   // 异常帧，直接放弃

        var payload = new byte[length];
        if (length > 0 && !_ReadExactly(stream, payload, 0, length, timeoutMs)) return false;
        json = Encoding.UTF8.GetString(payload);
        return true;
    }

    private static bool _ReadExactly(Stream stream, byte[] buffer, int offset, int count, int timeoutMs)
    {
        var deadline = timeoutMs < 0 ? long.MaxValue : Environment.TickCount64 + timeoutMs;
        var read = 0;
        while (read < count)
        {
            if (stream is NamedPipeClientStream { IsConnected: false }) return false;
            if (Environment.TickCount64 > deadline) return false;

            var n = stream.Read(buffer, offset + read, count - read);
            if (n <= 0) return false;
            read += n;
        }

        return true;
    }

    private static string? _ExtractMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return node?["data"]?["message"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    /// <summary>
    /// 主动断开。不清除 activity —— 调用方如果要"下线"，应当先 <see cref="SetActivity"/> 传 null。
    /// </summary>
    public void Disconnect()
    {
        var pipe = _pipe;
        _pipe = null;
        try { pipe?.Dispose(); } catch (Exception) { /* 忽略 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
