using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.IdentityModel.OAuth;

namespace PCL.Core.Minecraft.Profile.Authentication;

/// <summary>
///     用浏览器里 <c>login.live.com</c> 的 Cookie 换一整套微软令牌。
///     <para>
///         走的是「授权码 + <c>prompt=none</c>」：带着 Cookie 请求授权端点，微软认出会话后
///         直接 302 回回调地址，<c>code</c> 就在 URL 里；拿 code 换 access_token 与
///         refresh_token，后面和正常登录完全一样。
///     </para>
///     <para>
///         client id 用的是 Xbox 应用（<c>00000000402b5328</c>，微软自家的公共客户端）。
///         PCL 自己的 Azure 应用一个回调地址都没注册，拿它走授权码流程会直接
///         <c>AADSTS50011</c>；这个 Xbox 应用注册过 <c>oauth20_desktop.srf</c>，且不需要
///         任何 secret，正是为这类场景准备的。代价是它签发的 refresh token 只能用它自己换，
///         所以这类档案刷新时必须回到 <see cref="RefreshAsync" />。
///     </para>
/// </summary>
public static class MsCookieAuth
{
    public const string XboxClientId = "00000000402b5328";
    private const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";
    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";
    private const string AuthorizeEndpoint = "https://login.live.com/oauth20_authorize.srf";

    /// <summary>授权端点的主机名。Cookie 文件按域过滤时就对着它比。</summary>
    private const string AuthorizeHost = "login.live.com";
    private const string TokenEndpoint = "https://login.live.com/oauth20_token.srf";

    /// <summary>
    ///     这段文本像不像 Cookie。调用方靠它把 Cookie 和两种令牌区分开。
    ///     <para>
    ///         认得出四种常见形态：带 <c>Cookie:</c> 前缀的请求头、<c>a=b; c=d</c> 的多项串、
    ///         单独一条 <c>MSPOK=...</c>，以及浏览器扩展导出的 <c>cookies.txt</c>（Netscape 格式）。
    ///         三段式 JWT 有且只有两个点、且没有等号，refresh token 里根本不出现等号，
    ///         所以不会误判。
    ///     </para>
    ///     <para>
    ///         Netscape 格式必须单独认：它每行 7 列、用制表符分隔，既没有 <c>Cookie:</c> 前缀，
    ///         也没有分号 —— 只按「带等号又带分号」判的话，整份 cookie 文件会被当成
    ///         refresh token 拿去兑换，最后报一句「令牌不归这个应用」，完全指错方向。
    ///     </para>
    /// </summary>
    public static bool LooksLikeCookie(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        // 「Copy as cURL」整段粘进来的情况：里面有 -H 'Cookie: ...'
        if (LooksLikeCurl(value)) return true;
        // 扩展导出的 cookies.txt
        if (ParseNetscapeCookies(value) is not null) return true;
        if (value.Contains('=') &&
            value.StartsWith("Cookie", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Contains('=') && value.Contains(';')) return true;

        var separator = value.IndexOf('=');
        if (separator <= 0 || separator > 40) return false;
        // 三段式 JWT 交给令牌分支
        if (value.Count(c => c == '.') == 2) return false;
        if (value.Any(char.IsWhiteSpace)) return false;
        return value[..separator].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    }

    /// <summary>Cookie 文件的每一行：域、名字、值、过期时间戳（秒，0 表示会话 Cookie）。</summary>
    private readonly record struct NetscapeCookie(string Domain, string Name, string Value, long ExpiresAt);

    /// <summary>
    ///     解析浏览器扩展导出的 <c>cookies.txt</c>（Netscape 格式）。
    ///     <para>
    ///         每行 <c>域 标志 路径 安全 过期时间 名字 值</c>，制表符分隔；
    ///         <c>#HttpOnly_</c> 开头的行是 <c>HttpOnly</c> Cookie，前缀要削掉当数据行处理；
    ///         其余 <c>#</c> 开头的是注释（文件头通常就是 <c># Netscape HTTP Cookie File</c>）。
    ///     </para>
    ///     <para>
    ///         不是这种格式就返回 <see langword="null"/>。判定只看「有没有一行凑得出 7 列、
    ///         第 5 列是数字」—— 令牌和 <c>Cookie:</c> 头里都不会出现制表符，不会误判。
    ///     </para>
    /// </summary>
    private static List<NetscapeCookie>? ParseNetscapeCookies(string text)
    {
        if (!text.Contains('\t')) return null;

        var result = new List<NetscapeCookie>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#')
            {
                if (!line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) continue;
                line = line["#HttpOnly_".Length..];
            }

            var fields = line.Split('\t');
            if (fields.Length < 7) continue;
            if (!long.TryParse(fields[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var expiresAt)) continue;
            var name = fields[5].Trim();
            if (name.Length == 0) continue;
            // 值里理论上不该有制表符，但真有的话拼回去总比截断强
            result.Add(new NetscapeCookie(fields[0].Trim(), name, string.Join('\t', fields[6..]), expiresAt));
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>Cookie 的域是否覆盖目标主机。<c>.live.com</c> 覆盖 <c>login.live.com</c>，反之不行。</summary>
    private static bool DomainMatches(string cookieDomain, string host)
    {
        var domain = cookieDomain.Trim();
        if (domain.StartsWith('.')) domain = domain[1..];
        if (domain.Length == 0) return false;
        if (string.Equals(domain, host, StringComparison.OrdinalIgnoreCase)) return true;
        return host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     把解析出来的 Cookie 拼成一行请求头。
    ///     <para>
    ///         <b>只取覆盖 <see cref="AuthorizeHost"/> 的那些</b>：cookie 文件是整个浏览器的导出，
    ///         里面还有 <c>.microsoft.com</c>、<c>login.microsoftonline.com</c> 的几十条，
    ///         浏览器请求 <c>login.live.com</c> 时根本不会带它们。全塞进去等于伪造出一个
    ///         任何浏览器都不会发出的请求，反而可能把微软那边绕晕。
    ///     </para>
    ///     <para>
    ///         同名 Cookie 以更具体的域为准（<c>login.live.com</c> 压过 <c>.live.com</c>），
    ///         过期的直接丢掉 —— 浏览器也不会发它们。
    ///     </para>
    /// </summary>
    /// <exception cref="IdentityModelAuthenticationException">整份文件里没有一条能用于 <see cref="AuthorizeHost"/>。</exception>
    private static string BuildHeaderFromNetscape(List<NetscapeCookie> cookies)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var picked = new Dictionary<string, NetscapeCookie>(StringComparer.OrdinalIgnoreCase);
        var matched = 0;

        foreach (var cookie in cookies)
        {
            if (!DomainMatches(cookie.Domain, AuthorizeHost)) continue;
            matched++;
            // 0 表示会话 Cookie，不看过期时间
            if (cookie.ExpiresAt > 0 && cookie.ExpiresAt < now) continue;
            if (picked.TryGetValue(cookie.Name, out var existing) &&
                existing.Domain.TrimStart('.').Length >= cookie.Domain.TrimStart('.').Length) continue;
            picked[cookie.Name] = cookie;
        }

        if (picked.Count == 0)
            throw new IdentityModelAuthenticationException("invalid_cookie", matched == 0
                ? $"The pasted cookie file has no cookie for {AuthorizeHost}."
                : $"Every cookie for {AuthorizeHost} in the pasted file has expired.");

        return string.Join("; ", picked.Select(pair => $"{pair.Key}={pair.Value.Value}"));
    }

    /// <summary>这段文本是不是浏览器「Copy as cURL」出来的命令。</summary>
    private static bool LooksLikeCurl(string text) =>
        text.StartsWith("curl", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     从 <c>curl</c> 命令里把 Cookie 头抠出来。
    ///     <para>
    ///         这是浏览器里**唯一能一次拿全**的办法：<c>document.cookie</c> 读不到
    ///         <c>HttpOnly</c> 的 <c>MSPOK</c> / <c>RPSSecAuth</c>，而这几个缺了就等于没登录。
    ///         命令行里的 <c>-H 'Cookie: ...'</c>、<c>--header</c>、<c>-b</c> 三种写法都认，
    ///         cmd 那种 <c>^"</c> 转义也照收 —— 反正只找 <c>Cookie:</c> 后面到引号为止那段。
    ///     </para>
    /// </summary>
    private static string? ExtractCookieFromCurl(string text)
    {
        var match = Regex.Match(text, @"Cookie:\s*(?<value>[^""'\r\n]+)", RegexOptions.IgnoreCase);
        if (match.Success) return TrimCmdEscape(match.Groups["value"].Value);
        match = Regex.Match(text, @"(?:-b|--cookie)[\s=]+[""']?(?<value>[^""'\r\n]+)", RegexOptions.IgnoreCase);
        return match.Success ? TrimCmdEscape(match.Groups["value"].Value) : null;
    }

    /// <summary>
    ///     cmd 里引号是写成 <c>^"</c> 的，抠到引号前会把那个 <c>^</c> 带上，去掉。
    /// </summary>
    private static string TrimCmdEscape(string value) => value.Trim().TrimEnd('^').TrimEnd();

    /// <summary>
    ///     把用户粘进来的东西整理成一行标准的 <c>Cookie</c> 头。
    ///     容忍 <c>Cookie:</c> 前缀、换行、末尾的分号，整段 <c>curl</c> 命令，
    ///     以及扩展导出的 <c>cookies.txt</c>（Netscape 格式，会按域筛一遍）。
    /// </summary>
    /// <exception cref="IdentityModelAuthenticationException">一个 <c>name=value</c> 都没解析出来。</exception>
    public static string NormalizeCookieHeader(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        var text = raw.Trim();
        if (LooksLikeCurl(text))
            text = ExtractCookieFromCurl(text) ?? text;
        // cookies.txt 要走单独的解析：它有域信息，能按 login.live.com 筛掉别的站点的 Cookie
        if (ParseNetscapeCookies(text) is { } netscape)
            return BuildHeaderFromNetscape(netscape);
        if (text.StartsWith("Cookie", StringComparison.OrdinalIgnoreCase))
            text = text[(text.IndexOf(':') + 1)..];
        var pairs = text
            .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(pair => pair.Contains('=') && pair.IndexOf('=') > 0)
            .ToList();
        if (pairs.Count == 0)
            throw new IdentityModelAuthenticationException("invalid_cookie",
                "The pasted text does not contain any cookie pair.");
        return string.Join("; ", pairs);
    }

    /// <summary>
    ///     用 Cookie 换出整套微软令牌。返回的结果可以直接交给
    ///     <see cref="MicrosoftProvider.CompleteAsync" /> 继续换 Minecraft 令牌。
    /// </summary>
    /// <exception cref="IdentityModelAuthenticationException">Cookie 过期、没登录，或者换令牌被拒。</exception>
    public static async Task<AuthorizeResult> ExchangeAsync(string rawCookie, CancellationToken token)
    {
        var cookie = NormalizeCookieHeader(rawCookie);
        var url = $"{AuthorizeEndpoint}?client_id={XboxClientId}" +
                  $"&response_type=code&scope={Uri.EscapeDataString(Scope)}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&prompt=none";
        // 必须用不跟随重定向的客户端：code 就在 302 的 Location 里。
        // 让客户端自己跟过去的话，回调页是一张 HTML，还得再从地址栏里捞 code，多一层没必要的依赖
        using var response = await HttpRequest.Create(url)
            .WithHeader("Cookie", cookie)
            .WithHeader("Accept", "text/html,application/xhtml+xml")
            .SendAsync(NetworkService.GetClient(NetworkService.NoRedirect), cancellationToken: token)
            .ConfigureAwait(false);
        var location = response.Headers.Location;
        var query = ParseQuery(location?.Query ?? string.Empty);

        if (query.TryGetValue("error", out var error))
        {
            var description = query.GetValueOrDefault("error_description", error);
            throw new IdentityModelAuthenticationException(error, description);
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new IdentityModelAuthenticationException("cookie_flow_failed",
                "Microsoft did not return an authorization code.");

        using var tokenResponse = await HttpRequest.CreatePost(TokenEndpoint)
            .WithFormContent(new Dictionary<string, string>
            {
                ["client_id"] = XboxClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["scope"] = Scope
            })
            .SendAsync(NetworkService.GetClient(NetworkService.MicrosoftEntraId), cancellationToken: token)
            .ConfigureAwait(false);
        var body = await tokenResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return ParseTokenResponse(body);
    }

    /// <summary>
    ///     刷新 Cookie 档案的 refresh token。
    ///     <para>
    ///         必须用签发它的那个 client id 换 —— 拿 PCL 自己的身份去换，微软只会回
    ///         <c>AADSTS70000</c>「令牌是发给别的客户端的」。
    ///     </para>
    /// </summary>
    public static async Task<AuthorizeResult> RefreshAsync(string refreshToken, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        using var response = await HttpRequest.CreatePost(TokenEndpoint)
            .WithFormContent(new Dictionary<string, string>
            {
                ["client_id"] = XboxClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken.Trim(),
                ["scope"] = Scope
            })
            .SendAsync(NetworkService.GetClient(NetworkService.MicrosoftEntraId), cancellationToken: token)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var result = ParseTokenResponse(body);
        // 微软不一定会回新的 refresh token，那就继续用旧的
        return result with { RefreshToken = result.RefreshToken ?? refreshToken };
    }

    /// <summary>
    ///     解析 token 端点的响应。live.com 这套是老版 OAuth，<c>expires_in</c> 有时是字符串、
    ///     <c>token_type</c> 是小写的 <c>bearer</c>，所以全按字符串读再自己转。
    /// </summary>
    private static AuthorizeResult ParseTokenResponse(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new IdentityModelAuthenticationException("invalid_response",
                "Microsoft returned a response that is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            var error = ReadString(root, "error");
            if (!string.IsNullOrWhiteSpace(error))
                throw new IdentityModelAuthenticationException(error, ReadString(root, "error_description"));

            var expiresIn = ReadString(root, "expires_in");
            return new AuthorizeResult
            {
                AccessToken = ReadString(root, "access_token"),
                RefreshToken = ReadString(root, "refresh_token"),
                TokenType = ReadString(root, "token_type"),
                Scope = ReadString(root, "scope"),
                ExpiresIn = int.TryParse(expiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    ? seconds
                    : null
            };
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    /// <summary>把 <c>?a=b&amp;c=d</c> 拆成字典，值已经做过 URL 解码。</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = Uri.UnescapeDataString(part[..separator]);
            var value = Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }
}
