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
    /// Natayark ID OAuth 的 Client ID
    /// </summary>
    public static string NatayarkClientId { get; } = EnvironmentInterop.GetSecret("NAID_CLIENT_ID", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// Natayark ID OAuth 的 Client ID
    /// </summary>
    public static string NatayarkClientSecret { get; } = EnvironmentInterop.GetSecret("NAID_CLIENT_SECRET", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 联机根服务器
    /// </summary>
    public static string[] LinkServers { get; } = EnvironmentInterop.GetSecret("LINK_SERVER_ROOT", readEnvDebugOnly: true).ReplaceNullOrEmpty().Split("|");

    /// <summary>
    /// 当前版本的 Git 提交 SHA
    /// </summary>
    public static string CommitHash { get; } = EnvironmentInterop.GetSecret("GITHUB_SHA", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    ///     基岩版 GDK 正式版安装包的内容密钥（CIK）。
    ///     <para>
    ///         GDK 版下发的是加密的 <c>.msixvc</c>（XVC 容器），没有这个密钥解不开。
    ///         密钥是商店分发凭据，不写进源码：构建时设环境变量 <c>PCL_BEDROCK_RELEASE_CIK</c>
    ///         （十六进制）即可注入。没配时下载页会明确提示无法解包，而不是静默失败。
    ///     </para>
    /// </summary>
    public static byte[]? BedrockReleaseCik { get; } = ParseCik(EnvironmentInterop.GetSecret("BEDROCK_RELEASE_CIK", readEnvDebugOnly: true));

    /// <summary>基岩版 GDK 预览版安装包的内容密钥（CIK），说明见 <see cref="BedrockReleaseCik" />。</summary>
    public static byte[]? BedrockPreviewCik { get; } = ParseCik(EnvironmentInterop.GetSecret("BEDROCK_PREVIEW_CIK", readEnvDebugOnly: true));

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
