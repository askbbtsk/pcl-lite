using System;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Profile.Models;

/// <summary>
/// 启动器档案的完整持久化模型。该类型是引用类型，档案 ID 是其稳定身份。
/// </summary>
public sealed class McProfile : SafeProfile
{
    /// <summary>
    ///     登录方式：<c>device</c> / <c>refresh</c> / <c>token</c> / <c>cookie</c>。老档案没有这个字段，按设备码算。
    /// </summary>
    [JsonPropertyName("loginMethod")]
    public string LoginMethod { get; set; } = string.Empty;

    /// <summary>
    ///     签发 <see cref="RefreshToken" /> 的应用 client id。空 = PCL 自己。
    ///     <para>续期必须用签发者身份，否则 AADSTS70000。</para>
    /// </summary>
    [JsonPropertyName("refreshClientId")]
    public string RefreshClientId { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = string.Empty;
    [JsonPropertyName("clientToken")] public string ClientToken { get; set; } = string.Empty;
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("skinHeadId")] public string SkinHeadId { get; set; } = string.Empty;
    [JsonPropertyName("rawJson")] public string RawJson { get; set; } = string.Empty;

    // Authlib/Yggdrasil
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }
    [JsonPropertyName("name")] public string? LoginName { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }

    // OAuth/OpenID provider metadata
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }
    [JsonPropertyName("discoveryAddress")]
    public string? DiscoveryAddress { get; set; }
    [JsonPropertyName("idToken")]
    public string? IdToken { get; set; }

    [JsonIgnore]
    public bool IsExpired => ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow.AddMinutes(1);

    /// <summary>
    ///     复制一份用于就地替换。
    ///     <para>
    ///         <b>这里是手写的字段清单，漏一个字段就等于在续期时把它抹成默认值。</b>
    ///         以前漏了 <see cref="LoginMethod" /> 和 <see cref="RefreshClientId" />：
    ///         导入的 Refresh Token 档案刷新一次就变成「设备代码」（<see cref="LoginMethod" />
    ///         被抹成空 → 归一化成 device），签发者也被清掉，下次续期拿 PCL 自己的身份去换，
    ///         直接 AADSTS70000。加字段时记得同步这里。
    ///     </para>
    /// </summary>
    public McProfile Clone() => new()
    {
        ProfileId = ProfileId,
        UserName = UserName,
        AccessToken = AccessToken,
        Uuid = Uuid,
        TokenType = TokenType,
        ProfileType = ProfileType,
        RefreshToken = RefreshToken,
        ClientToken = ClientToken,
        ExpiresAt = ExpiresAt,
        Description = Description,
        SkinHeadId = SkinHeadId,
        RawJson = RawJson,
        Server = Server,
        ServerName = ServerName,
        LoginName = LoginName,
        Password = Password,
        Provider = Provider,
        DiscoveryAddress = DiscoveryAddress,
        IdToken = IdToken,
        LoginMethod = LoginMethod,
        RefreshClientId = RefreshClientId
    };
}
