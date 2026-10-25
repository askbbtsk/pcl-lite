using System;
using PCL.Core.App;

namespace PCL;

/// <summary>
///     基岩版 GDK 内容密钥（CIK）的取用与保存。
///     <para>
///         密钥属于微软商店的分发凭据，**不随启动器分发**：优先读用户在下载页填写的值
///         （<see cref="Config.Launch" /> 里的 <c>BedrockReleaseCik</c> / <c>BedrockPreviewCik</c>），
///         其次才是构建时用 <c>PCL_BEDROCK_RELEASE_CIK</c> / <c>PCL_BEDROCK_PREVIEW_CIK</c>
///         注入的 <see cref="Secrets" />。两者都没有时，GDK 包只能下载、不能解包。
///     </para>
/// </summary>
public static class ModBedrockCik
{
    /// <summary>CIK 是 AES 密钥加 IV，XVC 要求至少 48 字节（32 + 16）。</summary>
    public const int MinLength = 48;

    /// <summary>当前是否至少配置了某个通道的密钥。</summary>
    public static bool HasAny => Resolve(false) is not null || Resolve(true) is not null;

    /// <summary>是否配置了预览版密钥。预览版与正式版的密钥不通用，没有就不能解包预览版。</summary>
    public static bool HasPreviewKey => Resolve(true) is not null;

    /// <summary>取指定通道的密钥；没有配置或格式不对时返回 <c>null</c>。</summary>
    /// <param name="preview">true 取预览版（含 Beta），false 取正式版</param>
    /// <remarks>
    ///     两个通道的密钥**互不通用**，所以这里不做跨通道回退。
    ///     实测 26.70.21（<c>Microsoft.MinecraftWindowsBeta</c>）与 26.52
    ///     （<c>Microsoft.MinecraftUWP</c>）用的不是同一把：拿正式版密钥解预览版，
    ///     加密区域会解出无效 DLL，启动时报「损坏的映像」。
    /// </remarks>
    public static byte[]? Resolve(bool preview)
    {
        if (preview)
            return Parse(Config.Launch.BedrockPreviewCik) ?? Secrets.BedrockPreviewCik;

        return Parse(Config.Launch.BedrockReleaseCik) ?? Secrets.BedrockReleaseCik;
    }

    /// <summary>判断一串十六进制文本是否是合法的 CIK。</summary>
    public static bool IsValid(string? hex) => Parse(hex) is not null;

    /// <summary>保存用户填写的密钥；传空字符串表示清除。</summary>
    public static void Save(bool preview, string? hex)
    {
        var normalized = Normalize(hex);
        if (preview)
            Config.Launch.BedrockPreviewCik = normalized;
        else
            Config.Launch.BedrockReleaseCik = normalized;
    }

    /// <summary>把用户输入清洗成纯十六进制串（去掉空格、短横线、<c>0x</c> 前缀）。</summary>
    public static string Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "";

        var text = hex.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return text.Replace(" ", "").Replace("-", "").Replace("\r", "").Replace("\n", "");
    }

    private static byte[]? Parse(string? hex)
    {
        var text = Normalize(hex);
        if (text.Length == 0) return null;

        try
        {
            var bytes = Convert.FromHexString(text);
            return bytes.Length >= MinLength ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>根据版本通道判断该用哪个密钥。</summary>
    public static bool IsPreviewChannel(string channel)
        => channel.Equals("Preview", StringComparison.OrdinalIgnoreCase) ||
           channel.Equals("Beta", StringComparison.OrdinalIgnoreCase);
}
