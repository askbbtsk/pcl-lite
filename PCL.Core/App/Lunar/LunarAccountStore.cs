using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Core.App.Lunar;

/// <summary>
///     Lunar Client 自己账户库里的一条账号。
/// </summary>
/// <param name="LocalId">Lunar 内部的账号标识，<c>accounts.json</c> 的键。</param>
/// <param name="Username">Lunar 账号名（微软/Xbox 账号的显示名）。</param>
/// <param name="MinecraftName">游戏内名字。</param>
/// <param name="MinecraftUuid">游戏内 UUID，32 位不带横线。</param>
/// <param name="Type"><c>XBOX</c> 或 <c>MOJANG</c>。</param>
/// <param name="AccessToken">
///     Minecraft 的 accessToken（<c>api.minecraftservices.com</c> 签发，24 小时）。
///     游戏拿它去 <c>sessionserver</c> 建会话，联机校验的就是它。
/// </param>
public sealed record LunarAccount(
    string LocalId,
    string Username,
    string MinecraftName,
    string MinecraftUuid,
    string Type,
    string AccessToken)
{
    /// <summary>
    ///     会话到期时间，来自 <c>accessTokenExpiresAt</c>。
    ///     <para>
    ///         过期后游戏里会显示 <c>Invalid session</c>。续期走本地 IPC 的
    ///         <c>AuthService/RefreshAccount</c>：正常情况下由 Lunar 启动器应答，
    ///         PCL 启动时没人应答，所以 1.5 起由 <see cref="LunarIpcServer" /> 自己接管。
    ///     </para>
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Lunar 会话是否已经过期（留一分钟余量）。</summary>
    public bool IsExpired => ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow.AddMinutes(1);

    public string DisplayName =>
        string.IsNullOrWhiteSpace(MinecraftName) ? Username : MinecraftName;
}

/// <summary>
///     Lunar 账户库的只读快照。
/// </summary>
/// <param name="ActiveLocalId">Lunar 当前激活的账号，可能为空。</param>
/// <param name="Accounts">全部账号。</param>
public sealed record LunarAccountSnapshot(string ActiveLocalId, IReadOnlyList<LunarAccount> Accounts)
{
    public static readonly LunarAccountSnapshot Empty = new(string.Empty, []);

    public LunarAccount? Find(string? localId) =>
        string.IsNullOrWhiteSpace(localId)
            ? null
            : Accounts.FirstOrDefault(x => x.LocalId == localId);

    /// <summary>优先取指定账号，取不到就用 Lunar 当前的激活账号。</summary>
    public LunarAccount? Resolve(string? preferredLocalId) =>
        Find(preferredLocalId) ?? Find(ActiveLocalId) ?? Accounts.FirstOrDefault();
}

/// <summary>
///     Lunar Client 的账户库。
///     <para>
///         这里有个必须说清楚的坑：Lunar 游戏进程**不认**命令行里的
///         <c>--accessToken</c> / <c>--username</c>。它自己会去读
///         <c>~/.lunarclient/settings/game/accounts.json</c>，按
///         <c>activeAccountLocalId</c> 挑账号，甚至在 token 过期时自己刷新。
///         所以「在 PCL 里换了账号、进游戏还是原来那个」不是登录没生效，
///         而是 Lunar 根本没看我们传的凭据。
///     </para>
///     <para>
///         要换账号，只能改 <c>activeAccountLocalId</c>。这里在写之前先备份一份
///         <c>accounts.json.pclbak</c>，并且只动这一个字段，其余内容原样保留。
///     </para>
/// </summary>
public static class LunarAccountStore
{
    public static string FilePath(string lunarRoot) =>
        Path.Combine(lunarRoot, "settings", "game", "accounts.json");

    /// <summary>读取账户库。文件不存在或读不动时返回空快照，不抛异常。</summary>
    public static LunarAccountSnapshot Read(string lunarRoot)
    {
        try
        {
            var path = FilePath(lunarRoot);
            if (!File.Exists(path)) return LunarAccountSnapshot.Empty;

            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["accounts"] is not JsonObject accounts) return LunarAccountSnapshot.Empty;

            var list = new List<LunarAccount>();
            foreach (var pair in accounts)
            {
                if (pair.Value is not JsonObject item) continue;

                var profile = item["minecraftProfile"];
                list.Add(new LunarAccount(
                    pair.Key,
                    item["username"]?.GetValue<string>() ?? string.Empty,
                    profile?["name"]?.GetValue<string>() ?? string.Empty,
                    profile?["id"]?.GetValue<string>() ?? string.Empty,
                    item["type"]?.GetValue<string>() ?? string.Empty,
                    item["accessToken"]?.GetValue<string>() ?? string.Empty)
                {
                    ExpiresAt = ParseTimestamp(item["accessTokenExpiresAt"])
                });
            }

            var active = root["activeAccountLocalId"]?.GetValue<string>() ?? string.Empty;
            return new LunarAccountSnapshot(active, list);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return LunarAccountSnapshot.Empty;
        }
    }

    /// <summary>
    ///     把 Lunar 的激活账号切成 <paramref name="localId" />。
    ///     <para>
    ///         只在真的需要切换时写文件：目标账号不存在、或本来就是激活状态，
    ///         都直接返回。写入走「临时文件 + 换名」，避免写一半断电把
    ///         Lunar 的账户库弄坏。
    ///     </para>
    /// </summary>
    /// <returns>切换成功（或本来就是这个账号）返回 <c>true</c>。</returns>
    public static bool SetActive(string lunarRoot, string localId)
    {
        if (string.IsNullOrWhiteSpace(localId)) return false;

        var path = FilePath(lunarRoot);
        try
        {
            if (!File.Exists(path)) return false;

            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text) is not JsonObject root) return false;
            if (root["accounts"] is not JsonObject accounts || !accounts.ContainsKey(localId)) return false;

            if (root["activeAccountLocalId"]?.GetValue<string>() == localId) return true;

            // 第一次改之前留个底，出问题可以直接换回来
            var backup = path + ".pclbak";
            if (!File.Exists(backup)) File.Copy(path, backup);

            root["activeAccountLocalId"] = localId;

            var temporary = path + ".pcltmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     把某个账号的 <c>accessToken</c> / <c>accessTokenExpiresAt</c> 换成新的。
    ///     <para>
    ///         只动这两个字段：<c>refreshToken</c> 是 Lunar 自己的 OAuth 应用签的，
    ///         换掉会让 Lunar 启动器以后再也刷不动这个账号；其余字段（profile、
    ///         localId、type…）游戏也在用，一律原样保留。
    ///     </para>
    ///     <para>
    ///         游戏端拿到 IPC 的成功响应后会**重新读一遍这个文件**，所以写入必须先落盘
    ///         再回响应 —— 调用方要保证在返回前 <c>await</c> 完。
    ///     </para>
    /// </summary>
    /// <returns>写入成功返回 <c>true</c>。</returns>
    public static bool UpdateToken(string lunarRoot, string localId, string accessToken,
        DateTimeOffset? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(localId) || string.IsNullOrWhiteSpace(accessToken)) return false;

        var path = FilePath(lunarRoot);
        try
        {
            if (!File.Exists(path)) return false;

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return false;
            if (root["accounts"] is not JsonObject accounts) return false;
            if (accounts[localId] is not JsonObject account) return false;

            // 第一次改之前留个底，出问题可以直接换回来
            var backup = path + ".pclbak";
            if (!File.Exists(backup)) File.Copy(path, backup);

            account["accessToken"] = accessToken;
            if (expiresAt is { } value)
                account["accessTokenExpiresAt"] =
                    value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

            var temporary = path + ".pcltmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     账户库文件不存在时，按给定凭据建一份。
    ///     <para>
    ///         这是「全新机器、没装过 Lunar 启动器」的唯一出路：游戏进程不读命令行里的
    ///         <c>--accessToken</c>，它只认 <c>accounts.json</c> 的激活账号。文件不存在时
    ///         它没有任何身份可用，所以必须由我们按这次登录写出来。
    ///     </para>
    ///     <para>
    ///         <b>只在文件不存在、或者文件里一个账号都没有时才写</b>。已有内容一律不动 ——
    ///         那是 Lunar 启动器（或上一次启动）的成果，动了可能把别人的账号弄坏。
    ///     </para>
    /// </summary>
    /// <param name="lunarRoot">Lunar 数据根目录。</param>
    /// <param name="localId">要写进去的账号标识，调用方生成 32 位 hex。</param>
    /// <param name="username">微软/Xbox 账号名。</param>
    /// <param name="minecraftName">游戏内名字。</param>
    /// <param name="minecraftUuid">游戏内 UUID，32 位不带横线。</param>
    /// <param name="accessToken">Minecraft 令牌。</param>
    /// <param name="expiresAt">令牌到期时间。</param>
    /// <returns>写入成功（或本来就有账号）返回 <c>true</c>。</returns>
    public static bool CreateIfMissing(string lunarRoot, string localId, string username,
        string minecraftName, string minecraftUuid, string accessToken, DateTimeOffset? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(localId) || string.IsNullOrWhiteSpace(minecraftUuid)) return false;

        var path = FilePath(lunarRoot);
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject existing) return false;

                // 已经有账号了就不动，只在「空账户库」的情况下补一条
                if (existing["accounts"] is JsonObject { Count: > 0 }) return true;
                root = existing;
            }
            else
            {
                root = new JsonObject();
            }

            var account = new JsonObject
            {
                ["accessToken"] = accessToken,
                ["eligibleForMigration"] = false,
                ["hasMultipleProfiles"] = false,
                ["legacy"] = false,
                ["persistent"] = true,
                ["localId"] = localId,
                // Lunar 自己的 OAuth 令牌拿不到，留空：游戏端续期走本地 IPC，
                // 由 LunarIpcServer 用 PCL 的登录去刷新，不依赖这个字段
                ["refreshToken"] = string.Empty,
                ["minecraftProfile"] = new JsonObject
                {
                    ["id"] = minecraftUuid,
                    ["name"] = minecraftName
                },
                ["remoteId"] = string.Empty,
                ["type"] = "Xbox",
                ["username"] = username
            };

            if (expiresAt is { } value)
                account["accessTokenExpiresAt"] =
                    value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

            root["activeAccountLocalId"] = localId;
            root["accounts"] = new JsonObject { [localId] = account };

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temporary = path + ".pcltmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>读 <c>accessTokenExpiresAt</c>（形如 <c>2026-10-20T15:21:35.563Z</c>），读不出来就当没有。</summary>
    private static DateTimeOffset? ParseTimestamp(JsonNode? node)
    {
        try
        {
            var text = node?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
