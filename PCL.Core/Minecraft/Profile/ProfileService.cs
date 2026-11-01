using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.IoC;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Utils;
using PCL.Core.Utils.Secret;

namespace PCL.Core.Minecraft.Profile;

[LifecycleScope("profile", "档案服务")]
[LifecycleService(LifecycleState.Loaded)]
public partial class ProfileService
{
    private static readonly ProfileManagement<McProfile> _profiles = new();
    private static readonly object _saveLock = new();
    private static bool _canPersist = true;

    public static IReadOnlyList<McProfile> Profiles => _profiles.GetAll();
    public static McProfile? Current => _profiles.Current;
    public static int LastUsedProfile => _profiles.LastUsed;
    public static bool IsLoaded => _profiles.IsLoaded;
    public static bool IsCreatingProfile { get; set; }

    [LifecycleStart]
    private static Task _Start()
    {
        Load();
        return Task.CompletedTask;
    }

    [LifecycleStop]
    private static void _Stop()
    {
        if (IsLoaded) Save();
    }

    public static void Load()
    {
        if (_profiles.IsLoaded) return;

        var encryptedProvider = ConfigService.GetProvider(ConfigSource.SharedEncrypt);
        var hasConfiguredProfiles = encryptedProvider.GetValue<string>("Profile", out var configured);
        if (!hasConfiguredProfiles && ConfigService.GetProvider(ConfigSource.Shared).GetValue<string>("Profile", out var rawProfile))
        {
            if (_TryLoad(rawProfile))
            {
                _EnsureProfileIds();
                Save();
                return;
            }
            Context.Error("档案加密配置无法读取；为避免覆盖原始凭据，本次不会保存档案配置。");
            _canPersist = false;
            _profiles.LoadFromString("{\"lastUsed\":-1,\"profiles\":[]}");
            return;
        }
        if (hasConfiguredProfiles)
        {
            if (_TryLoad(configured))
            {
                _EnsureProfileIds();
                return;
            }
            Context.Error("档案配置格式无效；为避免覆盖原始凭据，本次不会保存档案配置。");
            _canPersist = false;
            _profiles.LoadFromString("{\"lastUsed\":-1,\"profiles\":[]}");
            return;
        }

        if (!_TryMigrateLegacy(out var migrated))
        {
            Context.Error("迁移旧档案失败；为避免覆盖原始凭据，本次不会保存档案配置。");
            _canPersist = false;
            _profiles.LoadFromString("{\"lastUsed\":-1,\"profiles\":[]}");
            return;
        }
        _profiles.LoadFromString(migrated ?? "{\"lastUsed\":-1,\"profiles\":[]}");
        Save();
    }

    public static void Save()
    {
        if (!IsLoaded || !_canPersist) return;
        lock (_saveLock)
            Config.System.ProfilesConfig.SetValue(_profiles.Serialize(), forceNewValue: true);
    }

    public static void Add(McProfile profile, bool select = true)
    {
        _EnsureLoaded();
        if (string.IsNullOrWhiteSpace(profile.ProfileId)) profile.ProfileId = Guid.NewGuid().ToString("N");
        _profiles.Add(profile, select);
        Save();
    }

    public static void Update(McProfile origin, McProfile current)
    {
        _EnsureLoaded();
        _profiles.Update(origin, current);
        Save();
    }

    public static void Remove(McProfile profile)
    {
        _EnsureLoaded();
        _profiles.Delete(profile);
        Save();
    }

    public static void Select(McProfile? profile)
    {
        _EnsureLoaded();
        _profiles.Select(profile);
        Save();
    }

    public static void SelectAt(int index)
    {
        _EnsureLoaded();
        _profiles.SelectAt(index);
        Save();
    }

    public static void Clear()
    {
        _EnsureLoaded();
        _profiles.Clear();
        Save();
    }

    public static async Task<McProfile> AuthenticateAsync(ProfileType profileType, AuthenticationRequest request,
        McProfile? existing, bool select, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        _EnsureLoaded();

        var canUseExisting = existing?.ProfileType == profileType;

        if (canUseExisting && !request.ForceRefresh &&
            (profileType is ProfileType.Microsoft or ProfileType.YggdrasilConnect) &&
            existing!.ExpiresAt is { } expires &&
            expires > DateTimeOffset.UtcNow.AddMinutes(1) &&
            !string.IsNullOrWhiteSpace(existing.AccessToken))
        {
            if (select) Select(existing);
            return existing;
        }

        var provider = await _CreateProviderAsync(profileType, request, existing, token).ConfigureAwait(false);
        AuthenticationResult? result = null;

        if (canUseExisting && profileType == ProfileType.Authlib && !request.ForceRefresh &&
            !request.ForceReselectProfile)
        {
            try
            {
                if (await provider.ValidateAsync(existing!, token).ConfigureAwait(false))
                {
                    if (select) Select(existing!);
                    return existing!;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Context.Warn("验证第三方档案失败，将尝试刷新", ex);
            }
        }

        if (canUseExisting && (!request.ForceReselectProfile || profileType != ProfileType.Authlib))
        {
            try
            {
                result = await provider.RefreshAsync(existing!, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IdentityModelAuthenticationException ex)
            {
                Context.Warn("刷新档案失败，将尝试重新认证", ex);
                if (ex.Error?.StartsWith("xsts_", StringComparison.OrdinalIgnoreCase) == true)
                    throw;
                if (!string.Equals(ex.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase) &&
                    request.RefreshFailureHandler is not null &&
                    await request.RefreshFailureHandler(ex, token).ConfigureAwait(false))
                {
                    if (select) Select(existing!);
                    return existing!;
                }
            }
            catch (Exception ex)
            {
                if (request.RefreshFailureHandler is not null &&
                    await request.RefreshFailureHandler(ex, token).ConfigureAwait(false))
                {
                    if (select) Select(existing!);
                    return existing!;
                }
                if (profileType == ProfileType.Authlib &&
                    !string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrWhiteSpace(request.Password))
                {
                    Context.Warn("刷新第三方档案失败，将尝试使用凭据重新认证", ex);
                }
                else
                {
                    throw;
                }
            }
        }

        if (result is null)
        {
            var loginRequest = request with { RefreshToken = null, IdToken = existing?.IdToken ?? request.IdToken };
            result = await provider.AuthenticateAsync(loginRequest, token).ConfigureAwait(false);
        }

        var matched = canUseExisting ? existing : _FindMatchingProfile(result);
        return ApplyAuthenticationResult(result, matched, select);
    }

    public static McProfile ApplyAuthenticationResult(AuthenticationResult result, McProfile? existing = null, bool select = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        // 同一个号、同一种登录方式 = 续期，不要再堆一条新的
        existing ??= _FindMatchingProfile(result);
        var profile = existing?.Clone() ?? new McProfile { ProfileId = Guid.NewGuid().ToString("N") };
        profile.ProfileType = result.ProfileType;
        profile.UserName = result.UserName;
        profile.Uuid = result.Uuid;
        profile.AccessToken = result.AccessToken;
        profile.RefreshToken = result.RefreshToken;
        profile.ClientToken = result.ClientToken;
        profile.TokenType = result.TokenType;
        profile.ExpiresAt = result.ExpiresAt;
        profile.RawJson = result.RawJson;
        profile.Server = result.Server ?? profile.Server;
        profile.ServerName = result.ServerName ?? profile.ServerName;
        profile.LoginName = result.LoginName ?? profile.LoginName;
        profile.Password = result.Password ?? profile.Password;
        profile.Provider = result.Provider ?? profile.Provider;
        profile.DiscoveryAddress = result.DiscoveryAddress ?? profile.DiscoveryAddress;
        profile.IdToken = result.IdToken ?? profile.IdToken;
        if (result.ProfileType == ProfileType.Microsoft)
            profile.LoginMethod = NormalizeLoginMethod(result.LoginMethod ?? profile.LoginMethod);
        // 续期沿用原来的签发者；新登进来的记下这次是谁签的
        if (!string.IsNullOrWhiteSpace(result.RefreshClientId))
            profile.RefreshClientId = result.RefreshClientId;
        if (existing is null) Add(profile, select);
        else
        {
            Update(existing, profile);
            if (select) Select(profile);
        }
        return profile;
    }

    public static bool HasMicrosoftProfile => Profiles.Any(p => p.ProfileType == ProfileType.Microsoft);

    private static async Task<IAuthenticateProvider> _CreateProviderAsync(ProfileType profileType,
        AuthenticationRequest request, McProfile? existing, CancellationToken token)
    {
        switch (profileType)
        {
            case ProfileType.Microsoft:
                return new MicrosoftProvider();
            case ProfileType.Authlib:
                return new AuthlibProvider(request.Server ?? existing?.Server ??
                    throw new InvalidOperationException("Authlib profile has no server."));
            case ProfileType.YggdrasilConnect:
            {
                var provider = new YggdrasilConnectProvider(
                    request.DiscoveryAddress ?? existing?.DiscoveryAddress ??
                    throw new InvalidOperationException("Yggdrasil Connect profile has no discovery address."),
                    request.ClientId,
                    request.Server ?? existing?.Server);
                await provider.InitializeAsync(token).ConfigureAwait(false);
                return provider;
            }
            default:
                throw new InvalidOperationException($"Profile type '{profileType}' does not require remote authentication.");
        }
    }

    private static McProfile? _FindMatchingProfile(AuthenticationResult result)
        => Profiles.FirstOrDefault(profile =>
            profile.ProfileType == result.ProfileType &&
            string.Equals(profile.Uuid, result.Uuid, StringComparison.Ordinal) &&
            (result.ProfileType == ProfileType.Microsoft
                ? string.Equals(NormalizeLoginMethod(profile.LoginMethod),
                    NormalizeLoginMethod(result.LoginMethod), StringComparison.Ordinal)
                : string.Equals(profile.Server, result.Server, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    ///     登录方式的归一化。老档案没有这个字段，按设备码算 —— 否则升级之后
    ///     同一个号会凭空多出一条「设备码」档案。
    /// </summary>
    public static string NormalizeLoginMethod(string? method) =>
        string.IsNullOrWhiteSpace(method) ? "device" : method.Trim().ToLowerInvariant();

    private static void _EnsureLoaded()
    {
        if (!IsLoaded) Load();
    }

    private static bool _TryLoad(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var node = JsonCompat.ParseNode(json);
            if (node is not JsonObject root || root["profiles"] is not JsonArray)
                return false;
            _profiles.LoadFromString(json);
            return true;
        }
        catch (Exception ex)
        {
            Context.Warn("读取新档案配置失败，将尝试迁移旧档案", ex);
            return false;
        }
    }

    private static void _EnsureProfileIds()
    {
        var changed = false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in Profiles)
        {
            if (!string.IsNullOrWhiteSpace(profile.ProfileId) && ids.Add(profile.ProfileId)) continue;
            do profile.ProfileId = Guid.NewGuid().ToString("N");
            while (!ids.Add(profile.ProfileId));
            changed = true;
        }

        if (_MergeDuplicateMicrosoftProfiles()) changed = true;
        if (changed) Save();
    }

    /// <summary>
    ///     合并重复的正版档案：同一个号 + 同一种登录方式只留一条。
    ///     <para>
    ///         以前不区分登录方式，同一个号反复登录会一直往下堆新条目 ——
    ///         列表里出现好几个一模一样的名字就是这么来的。
    ///     </para>
    ///     <para>
    ///         留哪条：先看有没有 Refresh Token（有才续得下去，令牌导入的那种 24 小时就废），
    ///         再看到期时间。反过来只看时间的话，一条快过期的令牌档案会把能续期的挤掉。
    ///     </para>
    /// </summary>
    /// <returns>删掉了东西返回 <c>true</c>。</returns>
    private static bool _MergeDuplicateMicrosoftProfiles()
    {
        var kept = new Dictionary<string, McProfile>(StringComparer.Ordinal);
        var duplicates = new List<McProfile>();
        foreach (var profile in Profiles.Where(x => x.ProfileType == ProfileType.Microsoft))
        {
            var key = $"{profile.Uuid}|{NormalizeLoginMethod(profile.LoginMethod)}";
            if (!kept.TryGetValue(key, out var existing))
            {
                kept[key] = profile;
                continue;
            }

            var replace = _IsBetterProfile(profile, existing);
            duplicates.Add(replace ? existing : profile);
            if (replace) kept[key] = profile;
        }

        if (duplicates.Count == 0) return false;

        var currentGone = _profiles.Current is { } current && duplicates.Contains(current);
        foreach (var profile in duplicates) _profiles.Delete(profile);
        if (currentGone && Profiles.Count > 0) _profiles.SelectAt(0);
        Context.Info($"[Profile] 已合并 {duplicates.Count} 条重复的正版档案");
        return true;
    }

    /// <summary>两条同号同方式的档案里，<paramref name="candidate" /> 是不是更该留的那条。</summary>
    private static bool _IsBetterProfile(McProfile candidate, McProfile current)
    {
        var candidateRenewable = !string.IsNullOrWhiteSpace(candidate.RefreshToken);
        var currentRenewable = !string.IsNullOrWhiteSpace(current.RefreshToken);
        if (candidateRenewable != currentRenewable) return candidateRenewable;
        return (candidate.ExpiresAt ?? DateTimeOffset.MinValue) >
               (current.ExpiresAt ?? DateTimeOffset.MinValue);
    }

    private static bool _TryMigrateLegacy(out string? migrated)
    {
        migrated = null;
        var candidates = new[]
        {
            Path.Combine(Paths.OldSharedData, "profiles.json"),
            Path.Combine(Paths.SharedData, "profiles.json")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return true;

        try
        {
            var root = JsonCompat.ParseNode(File.ReadAllText(path)) as JsonObject;
            if (root?["profiles"] is not JsonArray oldProfiles) return false;
            var result = new ProfileJson<McProfile>
            {
                LastUsed = root["lastUsed"]?.GetValue<int>() ?? -1
            };

            foreach (var node in oldProfiles.OfType<JsonObject>())
            {
                var type = node["type"]?.ToString()?.ToLowerInvariant();
                var profile = new McProfile
                {
                    ProfileId = Guid.NewGuid().ToString("N"),
                    UserName = node["username"]?.ToString() ?? string.Empty,
                    Uuid = node["uuid"]?.ToString() ?? string.Empty,
                    Description = node["desc"]?.ToString() ?? string.Empty,
                    SkinHeadId = node["skinHeadId"]?.ToString() ?? string.Empty,
                    Server = node["server"]?.ToString(),
                    ServerName = node["serverName"]?.ToString(),
                    LoginName = _Decrypt(node["name"]?.ToString()),
                    Password = _Decrypt(node["password"]?.ToString()),
                    ClientToken = _Decrypt(node["clientToken"]?.ToString()),
                    AccessToken = _Decrypt(node["accessToken"]?.ToString()),
                    RefreshToken = _Decrypt(node["refreshToken"]?.ToString()),
                    RawJson = _Decrypt(node["rawJson"]?.ToString()),
                    TokenType = "Bearer",
                    ProfileType = type switch
                    {
                        "microsoft" => ProfileType.Microsoft,
                        "authlib" => ProfileType.Authlib,
                        _ => ProfileType.Offline
                    }
                };

                if (node["expires"]?.GetValue<long>() is { } expires && expires > 0)
                {
                    try
                    {
                        profile.ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expires);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        profile.ExpiresAt = null;
                    }
                }
                result.Profiles.Add(profile);
            }

            migrated = JsonSerializer.Serialize(result, JsonCompat.SerializerOptions);
            return true;
        }
        catch (Exception ex)
        {
            Context.Error("迁移旧档案失败", ex);
            return false;
        }
    }

    private static string _Decrypt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try { return EncryptHelper.SecretDecrypt(value); }
        catch { return value; }
    }
}
