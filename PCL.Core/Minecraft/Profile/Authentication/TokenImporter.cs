using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Core.Minecraft.Profile.Authentication;

/// <summary>
///     用现成的凭据直接换一个账号，跳过设备码 / 浏览器登录。
///     <para>
///         支持三种输入，自动识别：
///         <list type="bullet">
///             <item><b>Refresh Token</b>：拿它去换一整套新令牌，能一直续期，效果等同于正常登录；</item>
///             <item><b>Minecraft 令牌</b>（三段式 JWT）：直接用，但 24 小时后失效，且没有东西可续；</item>
///             <item><b>微软 Cookie</b>：从 <c>login.live.com</c> 导出的会话 Cookie，同样能换出可续期的整套令牌。</item>
///         </list>
///     </para>
///     <para>
///         这里只管换令牌，不管界面。凭据等同于账号密码，调用方负责提示用户只填自己的。
///     </para>
/// </summary>
public static class TokenImporter
{
    /// <summary>
    ///     按凭据类型换出账号信息。
    /// </summary>
    /// <param name="credential">用户粘贴的 Refresh Token、Minecraft 令牌或微软 Cookie。</param>
    /// <exception cref="ArgumentException">凭据是空的。</exception>
    /// <exception cref="IdentityModelAuthenticationException">凭据无效、已过期，或不属于当前应用。</exception>
    public static async Task<AuthenticationResult> ImportAsync(string credential, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        credential = credential.Trim();

        // Cookie 先认：它带等号、带分号，和两种令牌长得完全不一样
        if (MsCookieAuth.LooksLikeCookie(credential))
        {
            var cookieOAuth = await MsCookieAuth.ExchangeAsync(credential, token).ConfigureAwait(false);
            // Cookie 换令牌用不着 PCL 自己的 client id，所以显式传 Xbox 应用的身份，
            // 免得没配 MS_CLIENT_ID 时构造函数直接抛异常
            var cookieResult = await new MicrosoftProvider(MsCookieAuth.XboxClientId)
                .CompleteAsync(cookieOAuth, token).ConfigureAwait(false);
            cookieResult.LoginMethod = "cookie";
            cookieResult.RefreshClientId = MsCookieAuth.XboxClientId;
            return cookieResult;
        }

        // 令牌里不会出现空白，但复制粘贴经常带换行，先抹掉再判断
        credential = string.Concat(credential.Where(c => !char.IsWhiteSpace(c)));
        if (TryReadMinecraftToken(credential, out var uuid, out var name, out var expiresAt))
            return new AuthenticationResult
            {
                ProfileType = ProfileType.Microsoft,
                UserName = name,
                Uuid = uuid,
                AccessToken = credential,
                // 没有 refreshToken：这个账号只能用到期为止
                RefreshToken = string.Empty,
                ClientToken = uuid,
                TokenType = "Bearer",
                ExpiresAt = expiresAt,
                Provider = "microsoft-token",
                LoginMethod = "token"
            };

        // 不是 JWT 就当 refresh token。
        // refresh token 绑死签发它的应用，换别的 client 一律 invalid_grant ——
        // 所以挨个试我们知道的签发者：PCL 自己、Lunar 启动器、IAS（游戏内账号切换）。
        Exception? mismatch = null;
        foreach (var clientId in RefreshClientIds())
        {
            try
            {
                var result = await new MicrosoftProvider(clientId)
                    .RefreshAsync(new McProfile { RefreshToken = credential }, token)
                    .ConfigureAwait(false);
                result.LoginMethod = "refresh";
                // 记下签发者，否则这条档案下次续期就换不动了
                result.RefreshClientId = clientId;
                return result;
            }
            catch (IdentityModelAuthenticationException ex) when (IsClientMismatch(ex))
            {
                // 这个 client 认不了这条令牌，换下一个
                mismatch = ex;
            }
        }

        throw mismatch ?? new IdentityModelAuthenticationException("invalid_grant",
            "The refresh token was not accepted by any known application.");
    }

    /// <summary>可以拿来换 refresh token 的应用身份，按优先级排列、去重。</summary>
    private static IEnumerable<string> RefreshClientIds()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[]
                 {
                     Secrets.MSOAuthClientId, Secrets.LunarOAuthClientId, Secrets.IasOAuthClientId
                 })
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (seen.Add(id)) yield return id;
        }
    }

    /// <summary>这条错误是不是「令牌不属于这个应用」，是的话换下一个 client 再试。</summary>
    private static bool IsClientMismatch(IdentityModelAuthenticationException ex)
    {
        if (string.Equals(ex.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(ex.Error, "invalid_client", StringComparison.OrdinalIgnoreCase)) return true;
        // AADSTS70000 / 700003 / 70008 都是「这条令牌不归你」
        return ex.Message.Contains("AADSTS7000", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("different client", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     从 Minecraft 令牌里读出 UUID / 名字 / 到期时间。读不出来就说明这串不是 MC 令牌。
    ///     <para>
    ///         UUID 在 <c>profiles.mc</c>（或 <c>pfd</c> 里 type=mc 的那项），
    ///         名字只有 <c>pfd</c> 里有，到期时间看 <c>exp</c>。
    ///     </para>
    /// </summary>
    public static bool TryReadMinecraftToken(string accessToken, out string uuid, out string name,
        out DateTimeOffset? expiresAt)
    {
        uuid = string.Empty;
        name = string.Empty;
        expiresAt = null;
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Count(x => x == '.') != 2) return false;

        try
        {
            var parts = accessToken.Split('.');
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;

            if (root.TryGetProperty("pfd", out var pfd) && pfd.ValueKind == JsonValueKind.Array)
                foreach (var item in pfd.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var type) ||
                        !string.Equals(type.GetString(), "mc", StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.TryGetProperty("name", out var mcName)) name = mcName.GetString() ?? string.Empty;
                    if (item.TryGetProperty("id", out var mcId)) uuid = mcId.GetString() ?? string.Empty;
                    break;
                }

            if (string.IsNullOrWhiteSpace(uuid) && root.TryGetProperty("profiles", out var profiles) &&
                profiles.TryGetProperty("mc", out var profileUuid))
                uuid = profileUuid.GetString() ?? string.Empty;

            if (root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);

            uuid = NormalizeUuid(uuid);
            return uuid.Length == 32 && uuid != new string('0', 32);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 UUID 统一成 32 位小写不带横线的形式；读不出来返回空串。</summary>
    private static string NormalizeUuid(string? uuid)
    {
        var hex = (uuid ?? string.Empty).Replace("-", string.Empty).Trim().ToLowerInvariant();
        return hex.Length == 32 ? hex : string.Empty;
    }
}
