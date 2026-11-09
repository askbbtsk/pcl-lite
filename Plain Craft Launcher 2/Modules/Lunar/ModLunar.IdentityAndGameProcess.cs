using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;
using PCL.Core.App.Lunar.Social;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Network;

namespace PCL;

public static partial class ModLunar
{

    /// <summary>
    ///     用 PCL 自己登录的账号启动。
    ///     <para>
    ///         这条路的坑在于：游戏进程**不读**命令行里的令牌，它按 <c>accounts.json</c>
    ///         的激活账号决定「我是谁」。所以这里会顺手在 Lunar 账户库里找同名（同 UUID）
    ///         的账号并把激活账号切过去；找不到就只能启动，进游戏仍是 Lunar 原来那个身份。
    ///     </para>
    /// </summary>
    private static async Task<(string Name, string Uuid, string Token)> ResolveFromIdentityAsync(
        LunarIdentityAccount identity)
    {
        Log($"账号：{identity.DisplayName}（Lunar 身份账号）");
        // 有 refreshToken 才谈得上续期；令牌导入的那种到期只能重新导入
        _canRefreshSession = identity.CanRefresh;

        var uuid = NormalizeUuid(identity.Uuid);
        var twin = _accounts.FirstOrDefault(x =>
            string.Equals(NormalizeUuid(x.MinecraftUuid), uuid, StringComparison.OrdinalIgnoreCase));

        // 过期就用 Lunar 的 client id 刷新一次，新令牌写回身份库
        var current = await RefreshIdentityAsync(identity, CancellationToken.None).ConfigureAwait(false);
        var token = current.AccessToken;
        DateTimeOffset? tokenExpiresAt = current.ExpiresAt;
        var refreshToken = current.RefreshToken;
        var displayName = current.DisplayName;

        // 续不了又已经过期：再往下走就是拿个死令牌进游戏，只会在 Connecting... 卡住
        if (current.IsExpired && !current.CanRefresh)
            throw new InvalidOperationException(
                Lang.Text("Lunar.Error.TokenOnlyExpired", displayName));

        if (twin is not null && LunarAccountStore.SetActive(LunarClientService.SettingsRoot, twin.LocalId))
        {
            _launchedLocalId = twin.LocalId;
            // 只切 active 是不够的：Lunar 用 refreshToken 判定账号是否可用
            // （invalid: !e.refreshToken），为空会直接标无效并卡在 Connecting...
            var wrote = LunarAccountStore.UpdateToken(LunarClientService.SettingsRoot, twin.LocalId,
                token, tokenExpiresAt, refreshToken);
            Log(wrote
                ? $"Lunar 账户库里已有这个 UUID，激活账号已切过去（{twin.LocalId}），令牌已写回"
                : $"Lunar 账户库里已有这个 UUID，激活账号已切过去（{twin.LocalId}），但令牌写回失败");
        }
        else if (!string.IsNullOrWhiteSpace(token) &&
                 LunarAccountStore.Upsert(LunarClientService.SettingsRoot, Guid.NewGuid().ToString("N"), displayName,
                     displayName, uuid.Replace("-", string.Empty), token, tokenExpiresAt, refreshToken))
        {
            // 账户库里没有这个号：补一条并切过去。游戏只认 activeAccountLocalId，
            // 光传命令行令牌的话，进游戏还是 Lunar 原来那个身份
            RefreshAccounts();
            var added = _accounts.FirstOrDefault(x =>
                string.Equals(NormalizeUuid(x.MinecraftUuid), uuid, StringComparison.OrdinalIgnoreCase));
            _launchedLocalId = added?.LocalId ?? string.Empty;
            Log($"Lunar 账户库里没有这个 UUID，已按登录账号新建一条并切成激活账号（{added?.LocalId}）");
        }
        else
        {
            Log("Lunar 账户库写入失败：游戏内身份仍由 Lunar 原来的激活账号决定");
        }

        WarnIfNotLunarToken(token, displayName);
        return (displayName, uuid, token ?? string.Empty);
    }


    /// <summary>
    ///     检查令牌是不是 Lunar 的应用签的。不是就在日志里说清楚，别让它一路走到
    ///     卡在 <c>Connecting...</c> 才让人猜。
    /// </summary>
    private static void WarnIfNotLunarToken(string? accessToken, string displayName)
    {
        var aid = ReadTokenAid(accessToken);
        if (string.IsNullOrEmpty(aid)) return;
        if (string.Equals(aid, LunarOAuthClientId, StringComparison.OrdinalIgnoreCase)) return;

        Log($"警告：{displayName} 的令牌不是 Lunar 应用签的（aid = {aid}），" +
            "游戏端会认不出来、卡在 Connecting...。请在 Lunar 页面重新登录一次这个账号。");
    }


    /// <summary>读 Minecraft accessToken 里的 <c>aid</c>（签发它的 Azure 应用 id）。读不出来返回空串。</summary>
    private static string ReadTokenAid(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return string.Empty;
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return string.Empty;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("aid", out var aid)
                ? aid.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }


    /// <summary>
    ///     确保这条 Lunar 身份账号的 accessToken 可用：没过期直接返回，
    ///     过期就用 Lunar 的 client id 刷新并写回身份库。
    ///     <para>
    ///         必须用 Lunar 的 client id 刷新 —— refreshToken 绑死签发它的应用，
    ///         换别的 client 会直接 <c>invalid_grant</c>。
    ///     </para>
    /// </summary>
    private static async Task<LunarIdentityAccount> RefreshIdentityAsync(LunarIdentityAccount identity,
        CancellationToken token)
    {
        if (!identity.IsExpired && !string.IsNullOrWhiteSpace(identity.AccessToken)) return identity;

        // 用 Minecraft 令牌导入的账号没有 refreshToken，续不了 —— 别去撞 invalid_grant，
        // 直接说清楚，让用户重新导入或改用设备码登录
        if (!identity.CanRefresh)
        {
            Log($"Lunar 身份账号 {identity.DisplayName} 是令牌导入的，已于 " +
                $"{FormatExpiry(identity.ExpiresAt)} 过期，没法自动续期。");
            return identity;
        }

        Log($"Lunar 身份账号 {identity.DisplayName} 的令牌已过期" +
            $"（{FormatExpiry(identity.ExpiresAt)}），正在刷新…");
        try
        {
            var refreshed = await new MicrosoftProvider(LunarOAuthClientId)
                .RefreshAsync(new McProfile { RefreshToken = identity.RefreshToken, UserName = identity.UserName },
                    token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refreshed.AccessToken))
            {
                Log("微软没有返回新的 accessToken，继续用旧的");
                return identity;
            }

            var renewed = identity with
            {
                UserName = string.IsNullOrWhiteSpace(refreshed.UserName) ? identity.UserName : refreshed.UserName,
                // 微软每次刷新都轮换 refreshToken，旧的再用就是 invalid_grant
                RefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
                    ? identity.RefreshToken
                    : refreshed.RefreshToken,
                AccessToken = refreshed.AccessToken,
                ExpiresAt = refreshed.ExpiresAt
            };
            LunarIdentityStore.Upsert(renewed);
            Log($"Lunar 身份令牌已刷新，到期 {FormatExpiry(renewed.ExpiresAt)}");
            return renewed;
        }
        catch (Exception ex)
        {
            Log("刷新 Lunar 身份令牌失败：" + ex.Message);
            return identity;
        }
    }


    /// <summary>
    ///     打开 Lunar 页面时预热一次令牌：快过期或已经过期的，现在就刷掉。
    ///     <para>
    ///         聊天（Lunar 后端）和启动用的是同一个 accessToken。以前只有「点启动」和
    ///     「点连接」时才顺手刷，用户先看到的就是好友列表空着、私聊发不出去，
    ///     或者连上之后一小时令牌过期、消息全丢。
    ///     </para>
    ///     <para>
    ///         单飞 + 60 秒节流：来回切页面不会反复打微软的刷新接口
    ///         （refreshToken 每次刷新都会轮换，没必要空转）。
    ///     </para>
    /// </summary>
    public static Task PrepareSessionAsync()
    {
        if (_prepareSessionTask is { IsCompleted: false }) return _prepareSessionTask;
        if (DateTime.UtcNow - _lastPrepareUtc < TimeSpan.FromSeconds(60)) return Task.CompletedTask;

        _lastPrepareUtc = DateTime.UtcNow;
        _prepareSessionTask = Task.Run(PrepareSessionCoreAsync);
        return _prepareSessionTask;
    }


    private static async Task PrepareSessionCoreAsync()
    {
        try
        {
            // 顺序和 ResolveCredentialsAsync 保持一致：身份账号优先，其次 Lunar 账户库
            if (SelectedIdentityAccount is { } identity)
            {
                if (!identity.IsExpired && !IsExpiringSoon(identity.ExpiresAt)) return;
                await RefreshIdentityAsync(identity, CancellationToken.None).ConfigureAwait(false);
                RaiseStateChanged();
                return;
            }

            if (SelectedAccount is { } account)
            {
                if (!account.IsExpired && !IsExpiringSoon(account.ExpiresAt)) return;
                if (await RefreshAccountTokenAsync(account, CancellationToken.None).ConfigureAwait(false) is not null)
                    RaiseStateChanged();
            }
        }
        catch (Exception ex)
        {
            // 预热失败不弹窗：真正要用的时候（启动 / 连聊天）还会再试一次并给出明确提示
            Log("预热 Lunar 令牌失败：" + ex.Message);
        }
    }


    /// <summary>
    ///     离到期不到 10 分钟就算「快过期」。聊天连接是长连接，等它真过期再刷就晚了。
    /// </summary>
    private static bool IsExpiringSoon(DateTimeOffset? expiresAt) =>
        expiresAt is { } value && value <= DateTimeOffset.UtcNow.AddMinutes(10);


    /// <summary>
    ///     在 Lunar 页面主动登录一个「Lunar 身份账号」。
    ///     <para>
    ///         走 Lunar 自己的 Azure 应用（<see cref="LunarOAuthClientId" />）的设备码流程。
    ///         产出的 refreshToken 的 <c>aid</c> 是 Lunar 的 client id，游戏端才认得出来。
    ///     </para>
    ///     <para>
    ///         结果只写 <see cref="LunarIdentityStore" />，**不写 PCL 的档案列表** ——
    ///         所以主页面看不到这个账号，两套登录互不影响。
    ///     </para>
    /// </summary>
    /// <returns>登录成功返回该账号；用户取消返回 <c>null</c>；失败抛异常。</returns>
    public static async Task<LunarIdentityAccount?> LoginLunarIdentityAsync()
    {
        var provider = new MicrosoftProvider(LunarOAuthClientId);
        var device = await provider.GetDeviceCodeAsync(CancellationToken.None).ConfigureAwait(false);
        var oauth = await ProfileUi.ShowDeviceCodeLoginAsync(
            new DeviceCodeAuthenticationContext(device,
                pollToken => provider.PollDeviceCodeAsync(device, pollToken)),
            CancellationToken.None).ConfigureAwait(false);
        if (oauth is null)
        {
            Log("Lunar 身份登录已取消");
            return null;
        }

        var result = await provider.CompleteAsync(oauth, CancellationToken.None).ConfigureAwait(false);
        var account = new LunarIdentityAccount
        {
            Uuid = NormalizeUuid(result.Uuid).Replace("-", string.Empty),
            UserName = result.UserName,
            RefreshToken = result.RefreshToken,
            AccessToken = result.AccessToken,
            ExpiresAt = result.ExpiresAt
        };
        if (!LunarIdentityStore.Upsert(account))
        {
            Log("Lunar 身份登录成功，但写入身份库失败");
            return null;
        }

        WarnIfNotLunarToken(account.AccessToken, account.DisplayName);
        Log($"Lunar 身份登录完成：{account.DisplayName}（{account.Uuid}）");
        // 登录完直接选中它，省得用户再点一次下拉框
        Config.Preference.Lunar.AccountLocalId = SelectionTag(account);
        Refresh();
        return account;
    }


    /// <summary>
    ///     抢占 Lunar 的 IPC 端口。抢到了就由 PCL 应答游戏的续期请求，
    ///     抢不到（Lunar 启动器在跑）就把端口原样交给游戏。
    /// </summary>
    private static async Task<LunarIpcStartResult> StartIpcAsync(string installationId)
    {
        StopIpc();

        // 能自己续期时不要轻易把端口让给 Lunar 启动器：启动器开着但没登录的话，
        // 游戏找它续期只会失败。28190 抢不到就退到 28191，把端口指给游戏。
        var fallback = _canRefreshSession ? new[] { LunarIpcServer.DefaultPort + 1 } : null;

        var result = await LunarIpcServer
            .StartAsync(LunarIpcServer.DefaultPort, installationId, RefreshLunarAccountAsync, Log,
                fallbackPorts: fallback, host: IpcHost)
            .ConfigureAwait(false);

        _ipcServer = result.Server;
        return result;
    }


    private static void StopIpc()
    {
        _ipcServer?.Dispose();
        _ipcServer = null;
    }


    /// <summary>
    ///     应答游戏端的 <c>AuthService/RefreshAccount</c>。
    ///     <para>
    ///         Lunar 启动器收到这个请求时用的是它自己 OAuth 应用的 refreshToken，
    ///         那个令牌绑死在 Lunar 的应用上（拿它换新令牌会得到 <c>AADSTS70000</c>），
    ///         PCL 用不了。这里改走另一条路：用 **PCL 自己登录**的微软账号刷一个
    ///         新的 Minecraft 令牌，写回 <c>accounts.json</c>。
    ///     </para>
    ///     <para>
    ///         所以前提是 PCL 主页面登录了同一个账号；不是同一个号就宁可失败，
    ///         也不能把别人的令牌写进这条账号里（令牌和 UUID 是绑定的）。
    ///     </para>
    /// </summary>
    private static async Task<LunarRefreshResult> RefreshLunarAccountAsync(Guid minecraftUuid,
        CancellationToken cancellationToken)
    {
        var wanted = NormalizeUuid(minecraftUuid.ToString());

        RefreshAccounts();
        if (_accounts.Count == 0)
        {
            // 游戏随时在写这个文件，偶尔会正好读到写了一半的内容，重读一次再下结论
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            RefreshAccounts();
        }

        var account = FindRefreshTarget(wanted);
        if (account is null)
            return LunarRefreshResult.Fail(LunarAccountError.AccountsFileEmpty,
                $"Lunar 账户库里没有 UUID {wanted}（现有：{DescribeAccountUuids()}）");

        if (FindPclProfileFor(account) is null)
            return LunarRefreshResult.Fail(LunarAccountError.MissingRefreshToken,
                $"PCL 里没有登录 {account.DisplayName}（{account.MinecraftUuid}），没有可用的 refreshToken");

        // 落盘在 RefreshAccountTokenAsync 里做完了才返回 —— 游戏拿到 success 会立刻重读文件
        var fresh = await RefreshAccountTokenAsync(account, cancellationToken).ConfigureAwait(false);
        return fresh is { } value
            ? LunarRefreshResult.Ok(value.Message)
            : LunarRefreshResult.Fail(LunarAccountError.MissingRefreshToken,
                "没能换到新的 accessToken（详见日志）");
    }


    /// <summary>
    ///     在账户库里找到这次要续期的账号。
    ///     <para>
    ///         只按 UUID 和 <c>localId</c> 认，**绝不拿别的账号顶替**。
    ///         顶替的代价是：我们回了 <c>success</c>，但刷新出来的令牌写进了另一个账号的条目，
    ///         游戏回头重读自己要的那一条，发现还是过期的，于是再问一次 —— 每秒一轮，
    ///         界面永远停在 <c>Connecting...</c>。宁可失败让游戏明确报错，也不能回这种假成功。
    ///     </para>
    /// </summary>
    private static LunarAccount? FindRefreshTarget(string wantedUuid)
    {
        var byUuid = _accounts.FirstOrDefault(x =>
            string.Equals(NormalizeUuid(x.MinecraftUuid), wantedUuid, StringComparison.OrdinalIgnoreCase));
        if (byUuid is not null) return byUuid;

        var byLocalId = _accounts.FirstOrDefault(x =>
            string.Equals(NormalizeUuid(x.LocalId), wantedUuid, StringComparison.OrdinalIgnoreCase));
        if (byLocalId is not null) return byLocalId;

        Log($"[续期] 账户库里没有 UUID {wantedUuid}（现有：{DescribeAccountUuids()}），不顶替，直接回失败");
        return null;
    }


    /// <summary>
    ///     把 PCL 当前登录的账号写进 Lunar 账户库并切成激活账号，供游戏内的
    ///     <c>AuthService/AddAccount</c> 使用。
    ///     <para>
    ///         官方启动器收到这个请求时弹自己的登录窗口；PCL 没有那套 UI，但游戏
    ///         真正认的身份来源就是 <c>accounts.json</c>，所以「把已经有的账号补进去」
    ///         等价于官方那条路的结果 —— 游戏重新读一遍文件就有账号了。
    ///     </para>
    /// </summary>
    /// <returns>真的交出了一个账号返回 <c>true</c>。</returns>
    public static bool TryProvideAccountToStore()
    {
        try
        {
            // 只认 Lunar 页面登录出来的身份账号 —— PCL 主页面的账号令牌 Lunar 认不出来
            var identity = SelectedIdentityAccount ?? LunarIdentityAccounts.FirstOrDefault();
            if (identity is null)
            {
                Log("[Lunar IPC] 游戏请求添加账号，但还没有 Lunar 身份账号（需要先在 Lunar 页面点「登录」）");
                return false;
            }

            var current = RefreshIdentityAsync(identity, CancellationToken.None).GetAwaiter().GetResult();
            var token = current.AccessToken;
            var expiresAt = current.ExpiresAt;
            var refreshToken = current.RefreshToken;
            var displayName = current.DisplayName;
            var uuid = NormalizeUuid(current.Uuid).Replace("-", string.Empty);
            RefreshAccounts();
            var twin = _accounts.FirstOrDefault(x =>
                string.Equals(NormalizeUuid(x.MinecraftUuid), uuid, StringComparison.OrdinalIgnoreCase));
            // 复用已有的 localId：游戏记的是这个 id，换一个新的等于凭空多出一个账号
            var localId = twin?.LocalId ??
                          (string.IsNullOrWhiteSpace(_launchedLocalId)
                              ? Guid.NewGuid().ToString("N")
                              : _launchedLocalId);

            if (!LunarAccountStore.Upsert(LunarClientService.SettingsRoot, localId, displayName, displayName,
                    uuid, token, expiresAt, refreshToken))
            {
                Log("[Lunar IPC] 写 Lunar 账户库失败，账号没能交给游戏");
                return false;
            }

            RefreshAccounts();
            _launchedLocalId = localId;
            Log($"[Lunar IPC] 已把账号 {displayName}（{localId}）写进 Lunar 账户库并切成激活账号");
            return true;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar IPC] 把账号写进 Lunar 账户库时出错", ModBase.LogLevel.Debug);
            return false;
        }
    }


    /// <summary>
    ///     找 PCL 里对应的登录。**先按 UUID 认，再按名字认** ——
    ///     改过名的账号 UUID 不变，只比名字会认错人。
    /// </summary>
    /// <summary>
    ///     在 PCL 的账号列表里找与 <paramref name="account" /> 对应的登录（UUID 优先，其次同名）。
    ///     <para>页面用它判断「这个 Lunar 账号能不能由 PCL 续期」，规则必须和续期时一致。</para>
    /// </summary>
    public static McProfile? MatchPclProfile(LunarAccount account) => FindPclProfileFor(account);


    private static McProfile? FindPclProfileFor(LunarAccount account)
    {
        var uuid = NormalizeUuid(account.MinecraftUuid);
        var profiles = PclProfiles;

        return profiles.FirstOrDefault(x =>
                   string.Equals(NormalizeUuid(x.Uuid), uuid, StringComparison.OrdinalIgnoreCase))
               ?? profiles.FirstOrDefault(x =>
                   string.Equals(x.UserName, account.DisplayName, StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>失败信息里带上账户库现有的 UUID，下次一眼就能看出对不上的是哪个。</summary>
    private static string DescribeAccountUuids() =>
        _accounts.Count == 0
            ? "账户库是空的"
            : string.Join("、", _accounts.Select(x =>
                $"{x.DisplayName}={NormalizeUuid(x.MinecraftUuid)}"));


    /// <summary>刷新出来的令牌，附带一句给日志看的说明。</summary>
    private readonly record struct LunarTokenRefresh(string Token, DateTimeOffset? ExpiresAt, string Message);


    private static string FormatExpiry(DateTimeOffset? expiresAt) =>
        expiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";


    /// <summary>
    ///     把账户库里这个账号的令牌换成新鲜的，并写回 <c>accounts.json</c>。
    ///     <para>
    ///         用的一定是 PCL 里**同一个账号**的登录（UUID 优先，其次同名）——令牌和 UUID
    ///         绑在一起，拿别人的令牌联机会被服务器直接踢。PCL 那份过期了就当场刷一次
    ///         （用档案里已有的 refreshToken，不弹窗）。
    ///     </para>
    ///     <para>
    ///         找不到能刷的登录、或者微软拒绝刷新时返回 <see langword="null" />，
    ///         由调用方决定是照旧启动还是提示用户重新登录。
    ///     </para>
    /// </summary>
    private static async Task<LunarTokenRefresh?> RefreshAccountTokenAsync(LunarAccount account,
        CancellationToken cancellationToken)
    {
        // ① 账户库里自己那份 refreshToken 是 Lunar 应用签的，优先用它自愈 ——
        //    这条路不依赖 PCL 里有没有同 UUID 的登录
        if (!string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            if (!account.IsExpired && !string.IsNullOrWhiteSpace(account.AccessToken))
                return new LunarTokenRefresh(account.AccessToken, account.ExpiresAt,
                    $"账户库里的令牌还能用（{account.DisplayName}），到期 {FormatExpiry(account.ExpiresAt)}");

            Log($"{account.DisplayName} 的账户库令牌已过期（{FormatExpiry(account.ExpiresAt)}），" +
                "用 Lunar 的 client id 刷新…");
            try
            {
                var refreshed = await new MicrosoftProvider(LunarOAuthClientId)
                    .RefreshAsync(new McProfile { RefreshToken = account.RefreshToken, UserName = account.Username },
                        cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(refreshed.AccessToken))
                {
                    // 微软每次刷新都轮换 refreshToken，必须一起写回去
                    var newRefresh = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
                        ? account.RefreshToken
                        : refreshed.RefreshToken;
                    var written = LunarAccountStore.UpdateToken(LunarClientService.SettingsRoot, account.LocalId,
                        refreshed.AccessToken, refreshed.ExpiresAt, newRefresh);
                    RefreshAccounts();
                    return new LunarTokenRefresh(refreshed.AccessToken, refreshed.ExpiresAt, written
                        ? $"已用 Lunar 身份刷新（{account.DisplayName}），到期 {FormatExpiry(refreshed.ExpiresAt)}"
                        : $"已刷新（{account.DisplayName}），但写回 accounts.json 失败，游戏内可能仍是旧令牌");
                }
            }
            catch (Exception ex)
            {
                Log($"用账户库的 refreshToken 刷新失败：{ex.Message}");
                // 落到下面用 PCL 登录再试一次
            }
        }

        // ② 账户库里没有 refreshToken（Lunar 启动器留下的老数据，或 PCL 早先写的条目）。
        //    退回「用 PCL 的登录替它续期」：能保证游戏内会话不失效，
        //    但产出的令牌 aid 是 PCL 的，Lunar 端可能认不出来 —— 想彻底解决就在 Lunar 页面重新登录一次
        McProfile? profile;
        try
        {
            profile = FindPclProfileFor(account);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 读取 PCL 登录凭据失败", ModBase.LogLevel.Debug);
            return null;
        }

        if (profile is null)
        {
            Log($"PCL 里没有 {account.DisplayName}（{NormalizeUuid(account.MinecraftUuid)}）的登录，" +
                "账户库里也没有可用的 refreshToken，没法替它续期");
            return null;
        }

        var token = profile.AccessToken;
        var expiresAt = profile.ExpiresAt;
        var refreshToken = profile.RefreshToken;

        if (profile.IsExpired || string.IsNullOrWhiteSpace(token))
        {
            Log($"PCL 里 {profile.UserName} 的令牌已过期（{FormatExpiry(expiresAt)}），正在刷新…");
            try
            {
                var refreshed = await new MicrosoftProvider()
                    .RefreshAsync(profile, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(refreshed.AccessToken))
                {
                    token = refreshed.AccessToken;
                    expiresAt = refreshed.ExpiresAt;
                    if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken)) refreshToken = refreshed.RefreshToken;
                    ProfileService.ApplyAuthenticationResult(refreshed, profile, select: false);
                    Log($"刷新成功，新令牌到期 {FormatExpiry(expiresAt)}");
                }
            }
            catch (Exception ex)
            {
                Log("刷新令牌失败：" + ex.Message);
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(token)) return null;

        // 游戏进程读的是 accounts.json，命令行里的令牌只是兜底，所以必须写回去
        var writtenLegacy = LunarAccountStore.UpdateToken(LunarClientService.SettingsRoot, account.LocalId, token,
            expiresAt, refreshToken);
        RefreshAccounts();

        return new LunarTokenRefresh(token, expiresAt, writtenLegacy
            ? $"取自 PCL 登录（{profile.UserName}），到期 {FormatExpiry(expiresAt)}"
            : $"取自 PCL 登录（{profile.UserName}），但写回 accounts.json 失败，游戏内可能仍是旧令牌");
    }


    /// <summary>
    ///     Lunar 的 <c>installation_id</c> 存在 <c>launcher-cache/installation-id</c> 下。
    ///     <para>
    ///         有就复用，没有就生成一个**并落盘** —— 每次启动都换一个 GUID 的话，
    ///         服务端会把同一台机器当成无数次全新安装。
    ///     </para>
    /// </summary>
    private static string ReadInstallationId()
    {
        var path = Path.Combine(LunarClientService.LauncherCacheDirectory(LunarRoot), "installation-id");
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (Guid.TryParse(text, out var existing)) return existing.ToString();
            }

            var created = Guid.NewGuid();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, created.ToString());
            return created.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去就现生成一个，不影响启动
            return Guid.NewGuid().ToString();
        }
    }


    /// <summary>Lunar 要的是不带横线的 UUID，接口校验要的是带横线的，这里统一成带横线。</summary>
    private static string NormalizeUuid(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return Guid.Empty.ToString();
        var hex = uuid.Replace("-", string.Empty);
        if (hex.Length != 32) return Guid.Empty.ToString();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

}
