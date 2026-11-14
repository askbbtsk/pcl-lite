using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace PCL.Core.Link.Ipv6Direct;

/// <summary>固定宽度的常用汉字编码。校验位只帮助发现复制错误，不提供身份认证。</summary>
internal static class CommonHanziCode
{
    internal const int PayloadLength = 38;
    internal const int CharacterCount = 26;
    internal static readonly string Alphabet = BuildAlphabet();
    private static readonly Dictionary<char, int> Digits = BuildDigits();
    private static readonly BigInteger Limit = BigInteger.One << (PayloadLength * 8 + 4);

    private static string BuildAlphabet()
    {
        // GB2312 一级区：3755 字，按拼音排列，全部位于 BMP；不使用二级字、扩展字或私用区。
        var bytes = new byte[3755 * 2];
        var index = 0;
        for (var row = 0xB0; row <= 0xD7; row++)
        for (var column = 0xA1; column <= (row == 0xD7 ? 0xF9 : 0xFE); column++)
        {
            bytes[index++] = (byte)row;
            bytes[index++] = (byte)column;
        }
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(936)
            ?? throw new InvalidOperationException("GB2312 character encoding is unavailable.");
        var alphabet = encoding.GetString(bytes);
        if (alphabet.Length != 3755) throw new InvalidOperationException("Invalid common Hanzi alphabet.");
        return alphabet;
    }

    private static Dictionary<char, int> BuildDigits()
    {
        var result = new Dictionary<char, int>(Alphabet.Length);
        for (var i = 0; i < Alphabet.Length; i++) result.Add(Alphabet[i], i);
        return result;
    }

    internal static string Encode(ReadOnlySpan<byte> payload, InviteKind kind)
    {
        if (payload.Length != PayloadLength) throw new ArgumentException("Invalid payload length.", nameof(payload));
        var value = (new BigInteger(payload, isUnsigned: true, isBigEndian: true) << 4) | Check(payload, kind);
        Span<char> text = stackalloc char[CharacterCount];
        for (var i = text.Length - 1; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, Alphabet.Length, out var remainder);
            text[i] = Alphabet[(int)remainder];
        }
        if (value != BigInteger.Zero) throw new InvalidOperationException("Compact code capacity exceeded.");
        return new string(text);
    }

    internal static bool TryDecode(ReadOnlySpan<char> text, InviteKind kind, out byte[] payload)
    {
        payload = [];
        if (text.Length != CharacterCount) return false;
        var value = BigInteger.Zero;
        foreach (var ch in text)
        {
            if (!Digits.TryGetValue(ch, out var digit)) return false;
            value = value * Alphabet.Length + digit;
        }
        if (value >= Limit) return false;
        var check = (int)(value & 15);
        var bytes = (value >> 4).ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[PayloadLength];
        bytes.CopyTo(result.AsSpan(PayloadLength - bytes.Length));
        if (Check(result, kind) != check) return false;
        payload = result;
        return true;
    }

    private static int Check(ReadOnlySpan<byte> payload, InviteKind kind)
    {
        var crc = (byte)((byte)kind ^ TunnelAuthentication.Version);
        foreach (var value in payload)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (byte)((crc << 1) ^ ((crc & 0x80) != 0 ? 0x07 : 0));
        }
        return (crc ^ (crc >> 4)) & 15;
    }
}
