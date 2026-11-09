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


#region XVC 容器解析（移植自 BedrockLauncher.Core.GdkDecode）

internal sealed class XvcContentKey
{
    /// <summary>
    ///     密钥自身的 GUID，即 <c>.cik</c> 文件的前 16 字节。
    ///     <para>
    ///         它必须与包内 <c>XvcInfo.EncryptionKeyIds</c> 声明的某个 GUID 相同，否则解密必然失败。
    ///         每个 GDK 包各自一把：实测 26.52 是 <c>91e7b9bd…</c>，26.70.21 是 <c>3fd6491f…</c>。
    ///     </para>
    /// </summary>
    public readonly byte[] KeyId;

    public readonly byte[] TweakKey;
    public readonly byte[] DataKey;

    public XvcContentKey(byte[] bytes)
    {
        KeyId = bytes[..16];
        TweakKey = bytes[16..32];
        DataKey = bytes[32..48];
    }

    /// <summary>密钥 GUID 的小写十六进制形式，用于和包内声明比对、以及写进报错信息。</summary>
    public string KeyIdHex => Convert.ToHexString(KeyId).ToLowerInvariant();
}
