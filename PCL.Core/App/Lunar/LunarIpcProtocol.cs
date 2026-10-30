using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PCL.Core.App.Lunar;

/// <summary>
///     Lunar 游戏进程与启动器之间的 IPC 协议编解码。
///     <para>
///         整套协议是 gRPC 风格的手写 protobuf，但没有 HTTP/2：帧直接走一条
///         WebSocket（<c>ws://127.0.0.1:&lt;ipcPort&gt;</c>）。消息定义来自
///         <c>lunar.jar</c> 里的 <c>com.lunarclient.gameipc.*</c>：
///     </para>
///     <code>
///     LauncherboundIPCMessage { bytes request_id = 1; string service = 2; string method = 3;
///                               bytes input = 4; string full_method = 6;
///                               string input_name = 7; string input_full_name = 8; }
///     GameboundIPCMessage     { IPCRpcResponse rpc_response = 1; google.protobuf.Any push_notification = 2; }
///     IPCRpcResponse          { bytes request_id = 1; bytes output = 2; }
///     RefreshAccountRequest   { lunarclient.common.v1.Uuid uuid = 1; }
///     RefreshAccountResponse  { bool success = 1; AccountError error = 2; }
///     Uuid                    { fixed64 high64 = 1; fixed64 low64 = 2; }
///     Handshake               { string launch_id = 1; string process_id = 2; string installation_id = 3; }
///     </code>
///     <para>
///         这里刻意不引入 Google.Protobuf：只有五个消息、字段全是标量，
///         手写比多一个依赖更省事，也不用跟着源生成器走。
///     </para>
/// </summary>
internal static class LunarIpcProtocol
{
    /// <summary>认证服务的全名，游戏用它当 <c>service</c> 字段。</summary>
    public const string AuthService = "lunarclient.gameipc.auth.v1.AuthService";

    /// <summary>续期方法名。</summary>
    public const string RefreshAccountMethod = "RefreshAccount";

    // 以下服务名全部来自官方启动器 app.asar 里 protobufjs 生成代码的常量，
    // 不是猜的。注意 lunarclient.websocket.* 那一批是启动器连自家后端的，
    // 不走本地 IPC，跟这里无关。

    /// <summary>游戏内点链接（商店 / 公告 / 帮助 / 社交链接）。</summary>
    public const string BrowserService = "lunarclient.gameipc.browser.v1.BrowserService";

    /// <summary>游戏内聊天面板把消息转给启动器。</summary>
    public const string ChatService = "lunarclient.gameipc.chat.v1.ChatService";

    /// <summary>游戏内「换版本重开」。</summary>
    public const string LaunchService = "lunarclient.gameipc.launch.v1.LaunchService";

    /// <summary>游戏把「在哪个服务器 / 是否聚焦」告诉启动器。</summary>
    public const string LocationService = "lunarclient.gameipc.location.v1.LocationService";

    /// <summary>PayNow 结账通道。</summary>
    public const string PayNowService = "lunarclient.gameipc.paynow.v1.PayNowService";

    /// <summary>活动推广。</summary>
    public const string PromotionService = "lunarclient.gameipc.promotion.v1.PromotionService";

    /// <summary>游戏内电台（Styngr）。</summary>
    public const string StyngrRadioService = "lunarclient.gameipc.styngr.v1.StyngrRadioService";

    /// <summary>Tebex 商店结账通道。</summary>
    public const string TebexService = "lunarclient.gameipc.tebex.v1.TebexService";

    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLengthDelimited = 2;
    private const int WireFixed32 = 5;

    #region 编码

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteTag(Stream stream, int field, int wireType) =>
        WriteVarint(stream, (ulong)((field << 3) | wireType));

    private static void WriteBytes(Stream stream, int field, ReadOnlySpan<byte> value)
    {
        WriteTag(stream, field, WireLengthDelimited);
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static void WriteString(Stream stream, int field, string value) =>
        WriteBytes(stream, field, Encoding.UTF8.GetBytes(value));

    private static void WriteBool(Stream stream, int field, bool value)
    {
        WriteTag(stream, field, WireVarint);
        WriteVarint(stream, value ? 1UL : 0UL);
    }

    private static void WriteEnum(Stream stream, int field, int value)
    {
        WriteTag(stream, field, WireVarint);
        WriteVarint(stream, (ulong)value);
    }

    // 给各服务的响应体用的公开写入器。protobuf 的规矩是「等于默认值就不写」，
    // 所以这些方法都会先跟默认值比一次：少写几个字节，游戏侧解出来是同一个对象。

    public static void WriteStringField(Stream stream, int field, string value)
    {
        if (!string.IsNullOrEmpty(value)) WriteString(stream, field, value);
    }

    public static void WriteBoolField(Stream stream, int field, bool value)
    {
        if (value) WriteBool(stream, field, true);
    }

    public static void WriteInt32Field(Stream stream, int field, int value)
    {
        if (value != 0) WriteEnum(stream, field, value);
    }

    public static void WriteFloatField(Stream stream, int field, float value)
    {
        if (value == 0f) return;
        WriteTag(stream, field, WireFixed32);
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        stream.Write(buffer);
    }

    /// <summary>写一个嵌套消息（长度前缀 + 已编码好的字节）。</summary>
    public static void WriteMessageField(Stream stream, int field, ReadOnlySpan<byte> payload)
    {
        if (!payload.IsEmpty) WriteBytes(stream, field, payload);
    }

    /// <summary>拼 <c>GameboundIPCMessage { rpc_response { request_id, output } }</c>。</summary>
    public static byte[] EncodeRpcResponse(ReadOnlySpan<byte> requestId, ReadOnlySpan<byte> output)
    {
        using var response = new MemoryStream();
        WriteBytes(response, 1, requestId);
        if (!output.IsEmpty) WriteBytes(response, 2, output);

        using var envelope = new MemoryStream();
        WriteBytes(envelope, 1, response.ToArray());
        return envelope.ToArray();
    }

    /// <summary>拼 <c>RefreshAccountResponse</c>。成功时只写 <c>success = true</c>。</summary>
    public static byte[] EncodeRefreshAccountResponse(bool success, LunarAccountError error)
    {
        using var stream = new MemoryStream();
        WriteBool(stream, 1, success);
        if (!success) WriteEnum(stream, 2, (int)error);
        return stream.ToArray();
    }

    #endregion

    #region 解码

    /// <summary>解析 <c>LauncherboundIPCMessage</c>。</summary>
    public static bool TryDecodeRequest(ReadOnlySpan<byte> payload, out LunarIpcRequest request)
    {
        request = default!;

        byte[] requestId = [];
        var service = string.Empty;
        var method = string.Empty;
        var inputFullName = string.Empty;
        byte[] input = [];

        var reader = new Reader(payload);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1 when wireType == WireLengthDelimited:
                    requestId = reader.ReadLengthDelimited().ToArray();
                    break;
                case 2 when wireType == WireLengthDelimited:
                    service = reader.ReadString();
                    break;
                case 3 when wireType == WireLengthDelimited:
                    method = reader.ReadString();
                    break;
                case 4 when wireType == WireLengthDelimited:
                    input = reader.ReadLengthDelimited().ToArray();
                    break;
                case 8 when wireType == WireLengthDelimited:
                    inputFullName = reader.ReadString();
                    break;
                default:
                    if (!reader.Skip(wireType)) return false;
                    break;
            }
        }

        if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(method)) return false;

        request = new LunarIpcRequest(requestId, service, method, inputFullName, input);
        return true;
    }

    /// <summary>解析 <c>RefreshAccountRequest</c>，取出要续期的 Minecraft UUID。</summary>
    public static bool TryDecodeRefreshAccountRequest(ReadOnlySpan<byte> payload, out Guid uuid)
    {
        uuid = Guid.Empty;

        var reader = new Reader(payload);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            if (field == 1 && wireType == WireLengthDelimited)
            {
                if (!TryDecodeUuid(reader.ReadLengthDelimited(), out uuid)) return false;
                continue;
            }

            if (!reader.Skip(wireType)) return false;
        }

        return uuid != Guid.Empty;
    }

    /// <summary>
    ///     <c>lunarclient.common.v1.Uuid</c> 是 Java 的 <c>UUID</c>：
    ///     两个 <c>fixed64</c>，按大端拼成 16 字节就是标准 UUID 文本。
    /// </summary>
    private static bool TryDecodeUuid(ReadOnlySpan<byte> payload, out Guid uuid)
    {
        uuid = Guid.Empty;
        ulong high = 0;
        ulong low = 0;

        var reader = new Reader(payload);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1 when wireType == WireFixed64:
                    high = reader.ReadFixed64();
                    break;
                case 2 when wireType == WireFixed64:
                    low = reader.ReadFixed64();
                    break;
                default:
                    if (!reader.Skip(wireType)) return false;
                    break;
            }
        }

        if (high == 0 && low == 0) return false;

        var hex = high.ToString("x16") + low.ToString("x16");
        return Guid.TryParse(
            $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}",
            out uuid);
    }

    /// <summary>解析握手头 <c>lc-handshake</c> 里的 <c>Handshake</c>。</summary>
    public static bool TryDecodeHandshake(ReadOnlySpan<byte> payload,
        out string launchId, out string processId, out string installationId)
    {
        launchId = string.Empty;
        processId = string.Empty;
        installationId = string.Empty;

        var reader = new Reader(payload);
        while (reader.TryReadTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1 when wireType == WireLengthDelimited:
                    launchId = reader.ReadString();
                    break;
                case 2 when wireType == WireLengthDelimited:
                    processId = reader.ReadString();
                    break;
                case 3 when wireType == WireLengthDelimited:
                    installationId = reader.ReadString();
                    break;
                default:
                    if (!reader.Skip(wireType)) return false;
                    break;
            }
        }

        return true;
    }

    // 通用单字段读取：各服务的请求都是扁平的几十字节消息，没必要为每个都写一个
    // 专用解析器。找不到字段就返回 false，调用方按「游戏没传这个值」处理。

    /// <summary>读一个 <c>string</c> 字段。</summary>
    public static bool TryGetString(ReadOnlySpan<byte> payload, int field, out string value)
    {
        value = string.Empty;
        var reader = new Reader(payload);
        while (reader.TryReadTag(out var current, out var wireType))
        {
            if (current == field && wireType == WireLengthDelimited)
            {
                value = reader.ReadString();
                return true;
            }

            if (!reader.Skip(wireType)) return false;
        }

        return false;
    }

    /// <summary>读一个 <c>bool</c> 字段。</summary>
    public static bool TryGetBool(ReadOnlySpan<byte> payload, int field, out bool value)
    {
        value = false;
        var reader = new Reader(payload);
        while (reader.TryReadTag(out var current, out var wireType))
        {
            if (current == field && wireType == WireVarint)
            {
                if (!reader.TryReadVarint(out var raw)) return false;
                value = raw != 0;
                return true;
            }

            if (!reader.Skip(wireType)) return false;
        }

        return false;
    }

    /// <summary>读一个 <c>int32</c> / 枚举字段。</summary>
    public static bool TryGetInt32(ReadOnlySpan<byte> payload, int field, out int value)
    {
        value = 0;
        var reader = new Reader(payload);
        while (reader.TryReadTag(out var current, out var wireType))
        {
            if (current == field && wireType == WireVarint)
            {
                if (!reader.TryReadVarint(out var raw)) return false;
                value = (int)raw;
                return true;
            }

            if (!reader.Skip(wireType)) return false;
        }

        return false;
    }

    /// <summary>读一个 <c>float</c> 字段。</summary>
    public static bool TryGetFloat(ReadOnlySpan<byte> payload, int field, out float value)
    {
        value = 0f;
        var reader = new Reader(payload);
        while (reader.TryReadTag(out var current, out var wireType))
        {
            if (current == field && wireType == WireFixed32)
            {
                value = reader.ReadFixed32();
                return true;
            }

            if (!reader.Skip(wireType)) return false;
        }

        return false;
    }

    /// <summary>读一个嵌套消息字段的原始字节（调用方再用同样的方法往里解）。</summary>
    public static bool TryGetMessage(ReadOnlySpan<byte> payload, int field, out byte[] nested)
    {
        nested = [];
        var reader = new Reader(payload);
        while (reader.TryReadTag(out var current, out var wireType))
        {
            if (current == field && wireType == WireLengthDelimited)
            {
                nested = reader.ReadLengthDelimited().ToArray();
                return true;
            }

            if (!reader.Skip(wireType)) return false;
        }

        return false;
    }

    /// <summary>只读游标，读坏了就返回 <c>false</c>，不抛异常。</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public bool TryReadTag(out int field, out int wireType)
        {
            field = 0;
            wireType = 0;
            if (_position >= _data.Length) return false;
            if (!TryReadVarint(out var tag)) return false;

            field = (int)(tag >> 3);
            wireType = (int)(tag & 0x7);
            return field > 0;
        }

        public bool TryReadVarint(out ulong value)
        {
            value = 0;
            var shift = 0;

            while (_position < _data.Length)
            {
                var b = _data[_position++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift >= 64) return false;
            }

            return false;
        }

        public ulong ReadFixed64()
        {
            if (_position + 8 > _data.Length) return 0;
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
            _position += 8;
            return value;
        }

        /// <summary>读 <c>fixed32</c>（float 用它传）。</summary>
        public float ReadFixed32()
        {
            if (_position + 4 > _data.Length) return 0f;
            var value = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_position, 4));
            _position += 4;
            return value;
        }

        public ReadOnlySpan<byte> ReadLengthDelimited()
        {
            if (!TryReadVarint(out var length)) return [];
            var count = (int)Math.Min(length, (ulong)(_data.Length - _position));
            var slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }

        public string ReadString() => Encoding.UTF8.GetString(ReadLengthDelimited());

        public bool Skip(int wireType)
        {
            switch (wireType)
            {
                case WireVarint:
                    return TryReadVarint(out _);
                case WireFixed64:
                    if (_position + 8 > _data.Length) return false;
                    _position += 8;
                    return true;
                case WireLengthDelimited:
                    if (!TryReadVarint(out var length)) return false;
                    if (length > (ulong)(_data.Length - _position)) return false;
                    _position += (int)length;
                    return true;
                case WireFixed32:
                    if (_position + 4 > _data.Length) return false;
                    _position += 4;
                    return true;
                default:
                    return false;
            }
        }
    }

    #endregion
}

/// <summary>游戏端发过来的一次 RPC 调用。</summary>
/// <param name="RequestId">游戏生成的 UUID 字符串字节，响应必须原样带回。</param>
/// <param name="Service">服务全名，例如 <c>lunarclient.gameipc.auth.v1.AuthService</c>。</param>
/// <param name="Method">方法名，例如 <c>RefreshAccount</c>。</param>
/// <param name="InputFullName">入参消息全名，仅用于日志。</param>
/// <param name="Input">入参的 protobuf 字节。</param>
internal sealed record LunarIpcRequest(
    byte[] RequestId,
    string Service,
    string Method,
    string InputFullName,
    byte[] Input);

/// <summary>续期失败的原因，取值与游戏侧 <c>RefreshAccountResponse.AccountError</c> 一一对应。</summary>
public enum LunarAccountError
{
    /// <summary>没有具体原因。</summary>
    Unspecified = 0,

    /// <summary>账户库是空的，或者里面没有这个 UUID。</summary>
    AccountsFileEmpty = 1,

    /// <summary>这个微软账号没买 Minecraft。</summary>
    NotPurchasedMinecraft = 2,

    /// <summary>拿不到可用的凭据（Lunar 那条 refreshToken 只有启动器能用）。</summary>
    MissingRefreshToken = 3,

    /// <summary>已经有一次续期在跑。</summary>
    AlreadyRefreshing = 4
}

/// <summary>续期的结果，直接对应 <c>RefreshAccountResponse</c>。</summary>
/// <param name="Success">是否续期成功。</param>
/// <param name="Error">失败原因，成功时忽略。</param>
/// <param name="Message">给日志看的中文说明，不会发给游戏。</param>
public readonly record struct LunarRefreshResult(
    bool Success,
    LunarAccountError Error,
    string Message)
{
    public static LunarRefreshResult Ok(string message) =>
        new(true, LunarAccountError.Unspecified, message);

    public static LunarRefreshResult Fail(LunarAccountError error, string message) =>
        new(false, error, message);
}
