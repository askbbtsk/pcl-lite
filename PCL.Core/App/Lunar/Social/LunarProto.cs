using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace PCL.Core.App.Lunar.Social;

/// <summary>
///     手写 protobuf 写入器。
///     <para>
///         Lunar 的 WebSocket 后端（<c>lunarclient.websocket.*</c>）和本地 IPC 一样是
///         protobuf，但这里没有 <c>Google.Protobuf</c> 依赖，也没有 <c>.proto</c> 文件 ——
///         字段号是从官方启动器 <c>app.asar</c> 里 protobufjs 生成的编解码代码反推出来的。
///         手写一个够用的写入器比引入整套运行时省事。
///     </para>
///     <para>
///         protobuf 的规矩是「等于默认值就不写」，但这里刻意<b>不做</b>那个判断：
///         多写几个字节对端一样能解，而少写一个「其实有意义」的字段（比如空字符串）
///         会让服务端把整条消息当成缺字段。取舍是「宁可多写」。
///     </para>
/// </summary>
internal sealed class ProtoWriter
{
    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLengthDelimited = 2;

    private readonly MemoryStream _buffer = new();

    public int Length => (int)_buffer.Length;

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _buffer.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        _buffer.WriteByte((byte)value);
    }

    private void WriteTag(int field, int wireType) => WriteVarint((ulong)((field << 3) | wireType));

    public void Int32(int field, int value)
    {
        if (value == 0) return;
        WriteTag(field, WireVarint);
        WriteVarint((ulong)value);
    }

    public void UInt64(int field, ulong value)
    {
        if (value == 0) return;
        WriteTag(field, WireVarint);
        WriteVarint(value);
    }

    public void Bool(int field, bool value)
    {
        if (!value) return;
        WriteTag(field, WireVarint);
        WriteVarint(1);
    }

    public void Fixed64(int field, ulong value)
    {
        if (value == 0) return;
        WriteTag(field, WireFixed64);
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _buffer.Write(buffer);
    }

    public void String(int field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Bytes(field, Encoding.UTF8.GetBytes(value));
    }

    public void Bytes(int field, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return;
        WriteTag(field, WireLengthDelimited);
        WriteVarint((ulong)value.Length);
        _buffer.Write(value);
    }

    /// <summary>写嵌套消息。空消息会被跳过，所以「必须出现但内容为空」的字段要走 <see cref="EmptyMessage" />。</summary>
    public void Message(int field, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return;
        Bytes(field, payload);
    }

    /// <summary>写一个零长度的嵌套消息（<c>tag + 0x00</c>），用来表达「这个字段存在，只是没内容」。</summary>
    public void EmptyMessage(int field)
    {
        WriteTag(field, WireLengthDelimited);
        WriteVarint(0);
    }

    public byte[] ToArray() => _buffer.ToArray();
}

/// <summary>手写 protobuf 读取器，只做「跳字段 + 取需要的字段」。</summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
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

    public ReadOnlySpan<byte> ReadBytes()
    {
        if (!TryReadVarint(out var length)) return default;
        if (length > (ulong)(_data.Length - _position)) return default;
        var slice = _data.Slice(_position, (int)length);
        _position += (int)length;
        return slice;
    }

    public string ReadString()
    {
        var bytes = ReadBytes();
        return bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    public ulong ReadFixed64()
    {
        if (_position + 8 > _data.Length) return 0;
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
        _position += 8;
        return value;
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                TryReadVarint(out _);
                break;
            case 1:
                _position += 8;
                break;
            case 2:
                ReadBytes();
                break;
            case 5:
                _position += 4;
                break;
            default:
                _position = _data.Length; // 不认识的类型，别硬着头皮往下读
                break;
        }

        if (_position > _data.Length) _position = _data.Length;
    }
}
