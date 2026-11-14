using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PCL.Core.Link.Ipv6Direct;

public enum InviteKind : byte
{
    HostInvite = 0x11,
    GuestAck = 0x12
}

public readonly record struct InviteCodePayload(
    InviteKind Kind,
    IPAddress Address,
    ushort Port,
    uint Token);

/// <summary>
/// 离线 IPv6 邀请码 / 回执码编解码器（25 字节定长载荷 + CRC16 + Base58）。
/// </summary>
public static class InviteCodeCodec
{
    public const string HostPrefix = "BSK-";
    public const string GuestAckPrefix = "ACK-";

    private const int PayloadLength = 25;
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private static readonly int[] Base58Map = BuildBase58Map();

    private static int[] BuildBase58Map()
    {
        var map = new int[128];
        Array.Fill(map, -1);
        for (var i = 0; i < Base58Alphabet.Length; i++)
        {
            map[Base58Alphabet[i]] = i;
        }
        return map;
    }

    public static string Encode(InviteCodePayload payload)
    {
        if (payload.Address.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException("Address must be IPv6.", nameof(payload));
        if (payload.Port == 0)
            throw new ArgumentOutOfRangeException(nameof(payload), "Port must be between 1 and 65535.");

        Span<byte> buffer = stackalloc byte[PayloadLength];
        buffer[0] = (byte)payload.Kind;
        if (!payload.Address.TryWriteBytes(buffer[1..17], out var written) || written != 16)
            throw new InvalidOperationException("Failed to serialize IPv6 address.");
        BinaryPrimitives.WriteUInt16BigEndian(buffer[17..19], payload.Port);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[19..23], payload.Token);
        var crc = ComputeCrc16(buffer[..23]);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[23..25], crc);

        var prefix = payload.Kind == InviteKind.GuestAck ? GuestAckPrefix : HostPrefix;
        var encodedBody = EncodeBase58(buffer);
        var checkChar = ComputeCheckChar(payload.Kind, encodedBody);
        return prefix + encodedBody + checkChar;
    }

    public static bool TryDecode(string? code, out InviteCodePayload payload, out string error)
    {
        payload = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(code))
        {
            error = "邀请码不能为空。";
            return false;
        }

        var trimmed = code.Trim();
        InviteKind expectedKind;
        string body;
        if (trimmed.StartsWith(HostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            expectedKind = InviteKind.HostInvite;
            body = trimmed[HostPrefix.Length..];
        }
        else if (trimmed.StartsWith(GuestAckPrefix, StringComparison.OrdinalIgnoreCase))
        {
            expectedKind = InviteKind.GuestAck;
            body = trimmed[GuestAckPrefix.Length..];
        }
        else
        {
            error = "邀请码前缀无效（应为 BSK- 或 ACK-）。";
            return false;
        }

        // 允许复制时混入空白或换行，但不动大小写（Base58 区分大小写）
        var cleaned = StripWhitespace(body);
        if (cleaned.Length < 2)
        {
            error = "邀请码格式损坏或长度不正确。";
            return false;
        }

        var encodedBody = cleaned[..^1];
        var actualCheckChar = cleaned[^1];
        if (!TryComputeCheckChar(expectedKind, encodedBody, out var expectedCheckChar) ||
            actualCheckChar != expectedCheckChar)
        {
            error = "邀请码末尾校验位不匹配，请检查是否输错、多字或漏字。";
            return false;
        }

        if (!TryDecodeBase58(encodedBody, out var raw) || raw.Length != PayloadLength)
        {
            error = "邀请码格式损坏或长度不正确。";
            return false;
        }

        var expectedCrc = ComputeCrc16(raw.AsSpan(0, 23));
        var actualCrc = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(23, 2));
        if (expectedCrc != actualCrc)
        {
            error = "邀请码校验失败，请检查是否漏字或错字。";
            return false;
        }

        var kindByte = raw[0];
        if (kindByte != (byte)expectedKind)
        {
            error = "邀请码类型与前缀不匹配。";
            return false;
        }

        var ip = new IPAddress(raw.AsSpan(1, 16));
        var port = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(17, 2));
        var token = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(19, 4));
        if (port == 0)
        {
            error = "邀请码中的端口号无效。";
            return false;
        }

        payload = new InviteCodePayload(expectedKind, ip, port, token);
        return true;
    }

    private static char ComputeCheckChar(InviteKind kind, string encodedBody)
    {
        if (!TryComputeCheckChar(kind, encodedBody, out var checkChar))
            throw new InvalidOperationException("Failed to compute invite code check character.");
        return checkChar;
    }

    private static bool TryComputeCheckChar(InviteKind kind, string encodedBody, out char checkChar)
    {
        checkChar = default;
        if (string.IsNullOrEmpty(encodedBody))
            return false;

        // Luhn mod 58 + 类型盐值，覆盖最后一位的任意单字符输入错误
        var sum = kind == InviteKind.GuestAck ? 17 : 11;
        var factor = 2;
        for (var i = encodedBody.Length - 1; i >= 0; i--)
        {
            var ch = encodedBody[i];
            if (ch >= 128)
                return false;
            var digit = Base58Map[ch];
            if (digit < 0)
                return false;

            var addend = factor * digit;
            if (addend >= 58)
                addend -= 57; // floor(addend / 58) + (addend % 58)

            sum += addend;
            factor = factor == 2 ? 1 : 2;
        }

        var checkDigit = (58 - (sum % 58)) % 58;
        checkChar = Base58Alphabet[checkDigit];
        return true;
    }

    private static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (!char.IsWhiteSpace(ch) && ch != '-')
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    private static string EncodeBase58(ReadOnlySpan<byte> data)
    {
        var zeros = 0;
        while (zeros < data.Length && data[zeros] == 0)
            zeros++;

        var temp = data.ToArray();
        var encoded = new char[data.Length * 2];
        var outIndex = encoded.Length;

        var startAt = zeros;
        while (startAt < temp.Length)
        {
            var mod = DivMod58(temp, startAt);
            if (temp[startAt] == 0)
                startAt++;
            encoded[--outIndex] = Base58Alphabet[mod];
        }

        while (outIndex < encoded.Length && encoded[outIndex] == Base58Alphabet[0])
            outIndex++;

        while (zeros-- > 0)
            encoded[--outIndex] = Base58Alphabet[0];

        return new string(encoded, outIndex, encoded.Length - outIndex);
    }

    private static bool TryDecodeBase58(string input, out byte[] output)
    {
        output = [];
        if (string.IsNullOrEmpty(input))
            return false;

        var input58 = new byte[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch >= 128)
                return false;
            var digit = Base58Map[ch];
            if (digit < 0)
                return false;
            input58[i] = (byte)digit;
        }

        var zeros = 0;
        while (zeros < input58.Length && input58[zeros] == 0)
            zeros++;

        var temp = new byte[input.Length];
        var j = temp.Length;
        var startAt = zeros;
        while (startAt < input58.Length)
        {
            var mod = DivMod256(input58, startAt);
            if (input58[startAt] == 0)
                startAt++;
            temp[--j] = mod;
        }

        while (j < temp.Length && temp[j] == 0)
            j++;

        var decodedLen = zeros + (temp.Length - j);
        output = new byte[decodedLen];
        Array.Copy(temp, j, output, zeros, temp.Length - j);
        return true;
    }

    private static byte DivMod58(byte[] number, int startAt)
    {
        var remainder = 0;
        for (var i = startAt; i < number.Length; i++)
        {
            var digit256 = number[i] & 0xFF;
            var temp = remainder * 256 + digit256;
            number[i] = (byte)(temp / 58);
            remainder = temp % 58;
        }
        return (byte)remainder;
    }

    private static byte DivMod256(byte[] number58, int startAt)
    {
        var remainder = 0;
        for (var i = startAt; i < number58.Length; i++)
        {
            var digit58 = number58[i] & 0xFF;
            var temp = remainder * 58 + digit58;
            number58[i] = (byte)(temp / 256);
            remainder = temp % 256;
        }
        return (byte)remainder;
    }
}
