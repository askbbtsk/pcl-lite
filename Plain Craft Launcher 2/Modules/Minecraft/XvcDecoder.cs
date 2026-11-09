using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;


/// <summary>
///     XTS-AES 解密器（软件实现，按 4KB 块批量处理）。
/// </summary>
internal sealed class XvcDecoder : IDisposable
{
    private readonly Aes _tweakAes;
    private readonly Aes _dataAes;

    public XvcDecoder(XvcContentKey key)
    {
        _tweakAes = CreateAes(key.TweakKey);
        _dataAes = CreateAes(key.DataKey);
    }

    public int Decrypt(ReadOnlySpan<byte> input, Span<byte> output, ReadOnlySpan<byte> tweakIv)
    {
        if (tweakIv.Length < 16) return 0;

        var length = Math.Min(input.Length, output.Length);
        if (length == 0) return 0;

        var blockCount = length / 16;
        if (blockCount == 0) return 0;

        Span<byte> tweak = stackalloc byte[16];
        _tweakAes.EncryptEcb(tweakIv[..16], tweak, PaddingMode.None);

        var inputBuffer = new byte[blockCount * 16];
        var outputBuffer = new byte[blockCount * 16];
        var tweaks = new byte[blockCount * 16];

        for (var i = 0; i < blockCount; i++)
        {
            var offset = i * 16;
            tweak.CopyTo(tweaks.AsSpan(offset, 16));
            for (var j = 0; j < 16; j++)
                inputBuffer[offset + j] = (byte)(input[offset + j] ^ tweak[j]);
            MultiplyGf128(tweak);
        }

        _dataAes.DecryptEcb(inputBuffer, outputBuffer, PaddingMode.None);

        for (var i = 0; i < blockCount * 16; i++)
            output[i] = (byte)(outputBuffer[i] ^ tweaks[i]);

        return blockCount * 16;
    }

    private static Aes CreateAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes;
    }

    /// <summary>GF(2^128) 上乘以 x（与 BedrockLauncher.Core 的 Gf128MulSoftware 等价）。</summary>
    private static void MultiplyGf128(Span<byte> iv)
    {
        var high = (iv[15] >> 7) & 1;
        var low = (iv[7] >> 7) & 1;

        for (var i = 7; i > 0; i--)
            iv[i] = (byte)((iv[i] << 1) | (iv[i - 1] >> 7));
        iv[0] <<= 1;

        for (var i = 15; i > 8; i--)
            iv[i] = (byte)((iv[i] << 1) | (iv[i - 1] >> 7));
        iv[8] <<= 1;

        if (low != 0) iv[8] ^= 1;
        if (high != 0) iv[0] ^= 135;
    }

    public void Dispose()
    {
        _tweakAes.Dispose();
        _dataAes.Dispose();
    }
}
