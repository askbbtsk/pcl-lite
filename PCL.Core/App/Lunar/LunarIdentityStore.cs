using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.App.Lunar;

/// <summary>
///     一条「Lunar 身份」账号。
///     <para>
///         它和 PCL 主页面的登录**没有任何关系**：这是用户在 Lunar 页面里主动用
///         <b>Lunar 自己的 Azure 应用</b>授权登录出来的账号，只服务于 Lunar 启动。
///         主页面的账号不会出现在这里，这里的账号也不会写进 PCL 的档案列表。
///     </para>
///     <para>
///         <b>为什么必须用 Lunar 的 client id 登录</b>：Lunar 游戏端按 JWT 里的 <c>aid</c>
///         选刷新实现（<c>aid === Lunar 的 client id</c> → 标准 OAuth；否则落到 SISU 分支，
///         需要 deviceToken 和密钥对，PCL 造不出来）。用 PCL 自己的 client id 登录，
///         <c>aid</c> 对不上，游戏启动就卡在 <c>Connecting...</c>。
///     </para>
/// </summary>
public sealed record LunarIdentityAccount
{
    /// <summary>游戏内 UUID，32 位不带横线，同时用作列表里的唯一键。</summary>
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;

    /// <summary>Xbox 显示名。</summary>
    [JsonPropertyName("username")]
    public string UserName { get; init; } = string.Empty;

    /// <summary>Lunar 应用签发的微软 refreshToken。Lunar 判定账号可用的硬条件。</summary>
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;

    /// <summary>Minecraft 的 accessToken，24 小时。</summary>
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>accessToken 到期时间。</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>到期判断，留两分钟余量，避免正好卡在边界上失效。</summary>
    [JsonIgnore]
    public bool IsExpired => ExpiresAt is not { } expires || expires <= DateTimeOffset.UtcNow.AddMinutes(2);

    /// <summary>下拉框和日志里显示的名字。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(UserName) ? Uuid : UserName;

    /// <summary>
    ///     能不能自己续期。
    ///     <para>
    ///         只有 refreshToken 才能续。用 Minecraft 令牌导入的账号没有它 ——
    ///         那种账号能用，但 24 小时后必须重新导入。
    ///     </para>
    /// </summary>
    [JsonIgnore]
    public bool CanRefresh => !string.IsNullOrWhiteSpace(RefreshToken);
}

/// <summary>
///     Lunar 身份账号的落盘存储。
///     <para>
///         放在 PCL 自己的共享目录（<see cref="Paths.SharedLocalData" />）下，
///         <b>不放进 <c>~/.lunarclient</c></b>：那个目录归 Lunar 管，游戏和启动器都会扫，
///         塞私货进去可能被当成未知文件清掉。
///     </para>
/// </summary>
public static class LunarIdentityStore
{
    private sealed record Container
    {
        [JsonPropertyName("accounts")]
        public List<LunarIdentityAccount> Accounts { get; init; } = [];
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(Paths.SharedLocalData, "Lunar", "identities.json");

    /// <summary>读全部账号。文件不存在或读不动时返回空列表，不抛异常。</summary>
    public static IReadOnlyList<LunarIdentityAccount> Read()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return [];
            var container = JsonSerializer.Deserialize<Container>(File.ReadAllText(path));
            return container?.Accounts?
                       .Where(x => !string.IsNullOrWhiteSpace(x.Uuid) &&
                                   (!string.IsNullOrWhiteSpace(x.RefreshToken) ||
                                    !string.IsNullOrWhiteSpace(x.AccessToken)))
                       .ToList()
                   ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>按 UUID 找一条账号。</summary>
    public static LunarIdentityAccount? Find(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return null;
        return Read().FirstOrDefault(x => string.Equals(x.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     新增或更新一条账号（按 UUID 去重）。
    ///     <para>
    ///         refreshToken 是唯一不能丢的东西 —— 丢了就等于要重新登录，
    ///         所以新值为空时保留旧的。
    ///     </para>
    /// </summary>
    public static bool Upsert(LunarIdentityAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Uuid)) return false;

        var list = Read().ToList();
        var index = list.FindIndex(x => string.Equals(x.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var old = list[index];
            list[index] = account with
            {
                RefreshToken = string.IsNullOrWhiteSpace(account.RefreshToken) ? old.RefreshToken : account.RefreshToken
            };
        }
        else
        {
            // 没有 refreshToken 也行：那是用 Minecraft 令牌导入的，能用但续不了
            if (string.IsNullOrWhiteSpace(account.AccessToken)) return false;
            list.Add(account);
        }

        return Write(list);
    }

    /// <summary>删掉一条账号。</summary>
    public static bool Remove(string uuid)
    {
        var list = Read().Where(x => !string.Equals(x.Uuid, uuid, StringComparison.OrdinalIgnoreCase)).ToList();
        return Write(list);
    }

    private static bool Write(IReadOnlyList<LunarIdentityAccount> accounts)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(new Container { Accounts = accounts.ToList() }, SerializerOptions));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
