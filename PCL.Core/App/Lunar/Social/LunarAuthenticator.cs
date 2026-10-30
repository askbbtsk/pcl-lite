using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.App.Lunar.Social;

/// <summary>
///     用 Minecraft 令牌换 Lunar 的启动器 JWT。
///     <para>
///         Lunar 后端不认 Minecraft 令牌，只认它自己签的 JWT。换法是一条三段式握手：
///     </para>
///     <list type="number">
///         <item>连 <c>wss://authenticator.lunarclientprod.com/launcher-v3</c>，发 <c>hello</c>；</item>
///         <item>服务端回 <c>encryptionRequest { publicKey, randomBytes }</c>；</item>
///         <item>
///             本地生成 16 字节 <c>sharedSecret</c>，用服务端公钥（RSA PKCS#1）分别加密
///             <c>sharedSecret</c> 和 <c>randomBytes</c>，回 <c>encryptionResponse</c>。
///             <b>中间必须拿 <c>sha1(sharedSecret ‖ publicKey)</c> 去调一次
///             <c>sessionserver</c> 的 join</b> —— 那是 Mojang 的「我要进服务器」声明，
///             Lunar 靠它确认这个令牌真的属于这个账号；
///         </item>
///         <item>服务端回 <c>authSuccess { encryptedJwt }</c>，用 <c>sharedSecret</c> 做
///         AES-128-GCM 解密（12 字节 nonce + 密文 + 16 字节 tag）就是 JWT。</item>
///     </list>
///     <para>
///         这套流程和 Mojang 的服务器加密握手一模一样，只是把「连服务器」换成了「连 Lunar」。
///         实现照抄官方启动器 <c>app.asar</c> 里的 <c>iAe</c>。
///     </para>
/// </summary>
public static class LunarAuthenticator
{
    private const string SessionJoinUrl = "https://sessionserver.mojang.com/session/minecraft/join";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>
    ///     换一个启动器 JWT。
    /// </summary>
    /// <param name="accessToken">Minecraft 访问令牌（<c>accounts.json</c> 里那份就是）。</param>
    /// <param name="uuid">令牌所属账号的 UUID。</param>
    /// <param name="username">账号名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="LunarSocialException">认证被拒、超时或网络失败。</exception>
    public static async Task<string> GetLauncherJwtAsync(string accessToken, LunarUuid uuid, string username,
        string launcherVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherVersion);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "Lunar Client Launcher v" + launcherVersion);
        socket.Options.SetRequestHeader("Accept", "application/x-protobuf");
        socket.Options.SetRequestHeader("X-Initiator", LunarSocialProtocol.LauncherInitiator);

        try
        {
            await socket.ConnectAsync(new Uri(LunarSocialProtocol.AuthenticatorUrl), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or UriFormatException)
        {
            throw new LunarSocialException("连不上 Lunar 认证服务：" + ex.Message, ex);
        }

        await SendAsync(socket, LunarSocialProtocol.EncodeHello(uuid, username), cancellationToken)
            .ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        byte[]? sharedSecret = null;

        while (true)
        {
            var frame = await ReceiveAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
            if (frame is null) throw new LunarSocialException("Lunar 认证服务提前断开了连接");

            if (LunarSocialProtocol.TryDecodeEncryptionRequest(frame, out var publicKey, out var randomBytes))
            {
                sharedSecret = RandomNumberGenerator.GetBytes(16);

                using var rsa = RSA.Create();
                try
                {
                    rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                }
                catch (CryptographicException ex)
                {
                    throw new LunarSocialException("Lunar 认证服务给了一把读不懂的公钥", ex);
                }

                var encryptedSecret = rsa.Encrypt(sharedSecret, RSAEncryptionPadding.Pkcs1);
                var encryptedRandom = rsa.Encrypt(randomBytes, RSAEncryptionPadding.Pkcs1);

                var serverHash = LunarSocialProtocol.ComputeServerHash(sharedSecret, publicKey);
                await JoinSessionAsync(accessToken, uuid, serverHash, cancellationToken).ConfigureAwait(false);

                await SendAsync(socket,
                        LunarSocialProtocol.EncodeEncryptionResponse(encryptedSecret, encryptedRandom),
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var encryptedJwt = LunarSocialProtocol.TryDecodeEncryptedJwt(frame);
            if (encryptedJwt is not null)
            {
                if (sharedSecret is null)
                    throw new LunarSocialException("Lunar 认证服务在加密握手之前就回了令牌");

                return DecryptJwt(sharedSecret, encryptedJwt);
            }

            throw new LunarSocialException("Lunar 认证服务回了认不出的消息");
        }
    }

    /// <summary>
    ///     告诉 Mojang「这个账号要进 Lunar 的服务器」。返回 204 才算成功。
    /// </summary>
    private static async Task JoinSessionAsync(string accessToken, LunarUuid uuid, string serverHash,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            accessToken,
            selectedProfile = uuid.ToCompactString(),
            serverId = serverHash
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(SessionJoinUrl, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LunarSocialException("连不上 Mojang 会话服务器：" + ex.Message, ex);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return;

            var detail = string.Empty;
            try
            {
                detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 读不出正文就算了，状态码已经够判断
            }

            var reason = detail.Length > 200 ? detail[..200] : detail;
            throw new LunarSocialException(
                $"Mojang 拒绝了这次会话声明（HTTP {(int)response.StatusCode}）{(reason.Length > 0 ? "：" + reason : "")}");
        }
    }

    /// <summary>用 <c>sharedSecret</c> 解出 JWT：<c>nonce(12) ‖ 密文 ‖ tag(16)</c>。</summary>
    private static string DecryptJwt(byte[] sharedSecret, byte[] payload)
    {
        if (payload.Length < 28) throw new LunarSocialException("Lunar 认证服务回的令牌长度不对");

        var nonce = payload.AsSpan(0, 12);
        var cipher = payload.AsSpan(12, payload.Length - 28);
        var tag = payload.AsSpan(payload.Length - 16, 16);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(sharedSecret, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException ex)
        {
            throw new LunarSocialException("解不开 Lunar 认证服务给的令牌", ex);
        }

        return Encoding.UTF8.GetString(plain);
    }

    private static Task SendAsync(ClientWebSocket socket, byte[] payload, CancellationToken cancellationToken) =>
        socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);

    /// <summary>收一条完整消息（Lunar 的帧都很小，不做流式拼接，只处理分片）。</summary>
    private static async Task<byte[]?> ReceiveAsync(ClientWebSocket socket, byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var stream = new System.IO.MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return stream.ToArray();
        }
    }
}

/// <summary>Lunar 社交链路（认证 / 好友 / 私聊）出错时抛这个，界面层统一按「连不上」处理。</summary>
public sealed class LunarSocialException(string message, Exception? inner = null) : Exception(message, inner);
