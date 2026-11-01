using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Minecraft.IdentityModel.OAuth;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Core.Minecraft.Profile.Authentication;

public sealed record AuthenticationRequest
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? RefreshToken { get; init; }
    public string? IdToken { get; init; }
    public McProfile? ExistingProfile { get; init; }
    public AuthorizeResult? OAuthResult { get; init; }
    public string? Server { get; init; }
    public string? DiscoveryAddress { get; init; }
    public string? ClientId { get; init; }
    public bool ForceRefresh { get; init; }
    public bool ForceReselectProfile { get; init; }
    public Func<DeviceCodeAuthenticationContext, CancellationToken, Task<AuthorizeResult?>>? DeviceCodeHandler { get; init; }
    public Func<Exception, CancellationToken, Task<bool>>? RefreshFailureHandler { get; init; }
    public Func<IReadOnlyList<AuthenticationCandidate>, CancellationToken, Task<AuthenticationCandidate?>>? ProfileSelector { get; init; }
}

public sealed record AuthenticationCandidate(string Id, string Name);

public sealed record DeviceCodeAuthenticationContext(DeviceCodeData Data,
    Func<CancellationToken, Task<AuthorizeResult?>> PollAsync);

public sealed class AuthenticationResult
{
    public required ProfileType ProfileType { get; init; }
    public required string UserName { get; init; }
    public required string Uuid { get; init; }
    public required string AccessToken { get; init; }
    public string RefreshToken { get; init; } = string.Empty;
    public string ClientToken { get; init; } = string.Empty;
    public string TokenType { get; init; } = "Bearer";
    public DateTimeOffset? ExpiresAt { get; init; }
    public string RawJson { get; init; } = string.Empty;
    public string? Server { get; init; }
    public string? ServerName { get; init; }
    public string? LoginName { get; init; }
    public string? Password { get; init; }
    public string? Provider { get; init; }
    public string? DiscoveryAddress { get; init; }
    public string? IdToken { get; init; }

    /// <summary>
    ///     这条账号是怎么登进来的：<c>device</c>（设备码）、<c>refresh</c>（Refresh Token）、
    ///     <c>token</c>（Minecraft 令牌）、<c>cookie</c>（微软 Cookie）。界面靠它区分同一个号的多条档案。
    /// </summary>
    public string? LoginMethod { get; set; }

    /// <summary>
    ///     签发这条 Refresh Token 的 Azure 应用 client id。为空表示 PCL 自己的应用。
    ///     <para>
    ///         Refresh Token 绑死签发它的应用，续期时必须拿同一个身份去换 ——
    ///         不记下来，用 IAS / Lunar 的令牌建的档案下次续期就会 AADSTS70000。
    ///     </para>
    /// </summary>
    public string? RefreshClientId { get; set; }
}

public interface IAuthenticateProvider
{
    Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken token);
    Task<AuthenticationResult> RefreshAsync(McProfile profile, CancellationToken token);
    Task<bool> ValidateAsync(McProfile profile, CancellationToken token);
}
