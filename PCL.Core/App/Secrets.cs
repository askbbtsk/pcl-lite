using System;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.OS;

namespace PCL.Core.App;

// ReSharper disable InconsistentNaming
public static class Secrets
{
    /// <summary>
    /// 微软 OAuth 的 Client ID
    /// <para>
    /// 优先取编译期注入的密钥（构建时设 <c>PCL_WRITE_SECRET=1</c> 与 <c>PCL_MS_CLIENT_ID</c>），
    /// 其次读取运行时环境变量 <c>PCL_MS_CLIENT_ID</c>。
    /// 这里刻意允许 Release 读取运行时环境变量，这样用户不必重新编译就能接上自己的 Azure 应用。
    /// </para>
    /// </summary>
    public static string MSOAuthClientId { get; } = EnvironmentInterop.GetSecret("MS_CLIENT_ID").ReplaceNullOrEmpty();

    /// <summary>
    /// CurseForge API 的 Client ID
    /// </summary>
    public static string CurseForgeAPIKey { get; } = EnvironmentInterop.GetSecret("CURSEFORGE_API_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 当前版本的 Git 提交 SHA
    /// </summary>
    public static string CommitHash { get; } = EnvironmentInterop.GetSecret("GITHUB_SHA", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    ///     内置的 GDK 内容密钥（48 字节）。
    ///     <para>
    ///         <c>.cik</c> 文件就是 48 字节，布局为 <c>GUID(16) + TweakKey(16) + DataKey(16)</c>：
    ///         前 16 字节是这个密钥的 GUID（用来和包内声明的 <c>EncryptionKeyIds</c> 比对），
    ///         <c>[16..32]</c> 是 XTS 的 TweakKey，<c>[32..48]</c> 是 DataKey（均为 AES-128）。
    ///         下面这把的 GUID 是 <c>91e7b9bd7cc93437e1a8bc602552df06</c>，对应 26.52 正式版包。
    ///     </para>
    ///     <para>
    ///         <b>来源</b>：反编译上游 PCL-Symbio-Edition 2.17.10 的
    ///         <c>EnvironmentInterop.SecretBytesDictionary</c> 得到。上游把它当 <c>byte[48]</c>
    ///         字面量内嵌（不是 hex 文本），所以按 96 位十六进制字符串去搜二进制是搜不到的——
    ///         这也是最早误判“上游没有密钥”的原因。
    ///     </para>
    ///     <para>
    ///         <b>注意</b>：实测这把密钥能解开 Release 通道的 GDK 包
    ///         （1.21.120 至 26.52 / 1.26.5203.0 全覆盖，同通道内版本共用一把）。
    ///         Preview 通道另有一把，见 <see cref="BuiltinPreviewCik" />。
    ///         若后续失效，用环境变量覆盖即可。
    ///     </para>
    /// </summary>
    private static readonly byte[] BuiltinReleaseCik =
    [
        0x91, 0xE7, 0xB9, 0xBD, 0x7C, 0xC9, 0x34, 0x37, 0xE1, 0xA8, 0xBC, 0x60, 0x25, 0x52, 0xDF, 0x06,
        0xC9, 0xA9, 0x69, 0xFB, 0xFC, 0xBB, 0xF5, 0xF4, 0x6D, 0x71, 0x25, 0x0A, 0xF2, 0x26, 0xCF, 0x6A,
        0xC7, 0xD1, 0x5C, 0x25, 0xF9, 0x54, 0x63, 0x44, 0x54, 0x93, 0x91, 0xD1, 0x68, 0x57, 0x39, 0x1F
    ];

    /// <summary>
    ///     基岩版 GDK 正式版安装包的内容密钥（CIK）。
    ///     <para>
    ///         GDK 版下发的是加密的 <c>.msixvc</c>（XVC 容器，<c>Volumes</c> 未置
    ///         <c>EncryptionDisabled</c>），没有这个密钥解不开。
    ///     </para>
    ///     <para>
    ///         取值优先级：环境变量 <c>PCL_BEDROCK_RELEASE_CIK</c>（十六进制）→ 内置密钥
    ///         <see cref="BuiltinReleaseCik" />。环境变量刻意允许 Release 读取，
    ///         这样密钥轮换后不必重新编译。
    ///     </para>
    /// </summary>
    public static byte[]? BedrockReleaseCik { get; } =
        ParseCik(EnvironmentInterop.GetSecret("BEDROCK_RELEASE_CIK")) ?? BuiltinReleaseCik;

    /// <summary>
    ///     内置的基岩版 GDK 预览版内容密钥（CIK，48 字节）。
    ///     <para>
    ///         取值方式与 <see cref="BuiltinReleaseCik" /> 一致，布局为
    ///         <c>GUID(16) + TweakKey(16) + DataKey(16)</c>。
    ///     </para>
    ///     <para>
    ///         <b>来源</b>：在本机用 <c>LukeFZ/CikExtractor</c> 从自己安装的
    ///         <c>Microsoft.MinecraftWindowsBeta</c> 许可证中提取，GUID 为
    ///         <c>1f49d63f-8bf5-1f8d-ed7e-dbd89477dad9</c>，与预览版 GDK 包内声明的
    ///         <c>EncryptionKeyIds[0]</c>（文件字节序 <c>3fd6491f…</c>）一致，已验证可解包。
    ///     </para>
    ///     <para>
    ///         <b>注意</b>：这是商店分发的 DRM 内容密钥，<b>仅供持有正版 Preview 的用户使用</b>。
    ///         密钥失效后请用环境变量 <c>PCL_BEDROCK_PREVIEW_CIK</c> 覆盖，不必重新编译。
    ///     </para>
    /// </summary>
    private static readonly byte[] BuiltinPreviewCik =
    [
        0x3F, 0xD6, 0x49, 0x1F, 0xF5, 0x8B, 0x8D, 0x1F, 0xED, 0x7E, 0xDB, 0xD8, 0x94, 0x77, 0xDA, 0xD9,
        0x80, 0x28, 0x14, 0x00, 0x75, 0x71, 0xF6, 0xA3, 0x53, 0xC7, 0x10, 0xBA, 0x97, 0x2E, 0xF1, 0x13,
        0xC6, 0xF2, 0x50, 0xC5, 0x4B, 0x31, 0x5A, 0xF6, 0x1A, 0x33, 0xCC, 0xA5, 0xDE, 0x85, 0xB0, 0x8A
    ];

    /// <summary>
    ///     基岩版 GDK 预览版安装包的内容密钥（CIK）。
    ///     <para>
    ///         预览版与正式版用的是**不同的密钥**，不能互相回退：实测拿正式版密钥解 26.70.21
    ///         （<c>Microsoft.MinecraftWindowsBeta</c>），加密区域会解出一堆无效 DLL，
    ///         文件大小正常但 PE 头是乱的，启动时报「损坏的映像 0xc000012f」。
    ///     </para>
    ///     <para>
    ///         <b>密钥按通道分配，不按版本</b>：GUID 写在包内 <c>XvcInfo.EncryptionKeyIds[0]</c>，
    ///         实测 Release 通道从 1.21.120 到 26.52 共用 <c>91e7b9bd…</c>，
    ///         Preview 通道从 1.21.120.21 到 26.70.21 共用 <c>3fd6491f…</c>，
    ///         一年来两个通道都没有轮换过。所以每个通道只需要一把密钥。
    ///     </para>
    ///     <para>
    ///         取值优先级：环境变量 <c>PCL_BEDROCK_PREVIEW_CIK</c>（十六进制）→ 内置密钥
    ///         <see cref="BuiltinPreviewCik" />。
    ///     </para>
    /// </summary>
    public static byte[]? BedrockPreviewCik { get; } =
        ParseCik(EnvironmentInterop.GetSecret("BEDROCK_PREVIEW_CIK")) ?? BuiltinPreviewCik;

    private static byte[]? ParseCik(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var bytes = Convert.FromHexString(hex.Trim());
            return bytes.Length >= 48 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
