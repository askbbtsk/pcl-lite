using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Consent;

/// <summary>
/// 网络授权中心。
/// <para>
/// 所有出站请求都会先经过 <see cref="NetworkGuardHandler"/>，由它向本服务确认目标域名所属的
/// 「用途」是否已被授权。授权粒度是用途而不是域名，因此一次确认可以覆盖该用途下的全部域名。
/// </para>
/// <para>
/// 决策顺序：会话内已决策 → 永久白名单 → 隐私模式（直接拒绝）→ 询问用户。
/// </para>
/// </summary>
public static class NetworkConsentService
{
    /// <summary>
    /// 询问用户的回调，由宿主（启动器 UI）注册。返回 <c>true</c> 表示本次允许。
    /// 未注册时一律拒绝，避免在无人值守场景下静默放行。
    /// </summary>
    public static Func<NetworkPurposeInfo, Task<bool>>? ConsentRequested { get; set; }

    /// <summary>
    /// 隐私模式拦下某个用途时触发，由宿主提示用户「为什么没联网」。
    /// 隐私模式是静默拒绝，没有这个回调用户只会看到功能莫名失败。
    /// </summary>
    public static event Action<NetworkPurposeInfo>? PrivacyBlocked;

    private static readonly ConcurrentDictionary<NetworkPurpose, bool> _session = new();
    private static readonly ConcurrentDictionary<string, bool> _unknownHosts =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _gate = new(1, 1);

    private static volatile bool _allowAllForSession;

    /// <summary>隐私模式：不弹窗，直接拒绝所有未永久授权的用途。</summary>
    public static bool PrivacyMode => Config.System.PrivacyMode;

    /// <summary>
    /// 是否处于「授权全部连接」状态。
    /// <para>
    /// 两种来源：设置里长期打开（默认打开），或用户在授权窗口里选了「本次会话全部允许」。
    /// 命中任意一种时，所有用途与未登记域名都直接放行，不再询问。
    /// </para>
    /// </summary>
    public static bool AllowAll => Config.Network.ConsentAllowAll || _allowAllForSession;

    /// <summary>本次会话是否已选择「全部允许」。</summary>
    public static bool AllowAllForSession => _allowAllForSession;

    /// <summary>允许本次会话内全部用途。</summary>
    public static void SetAllowAllForSession()
    {
        _allowAllForSession = true;
        LogWrapper.Info("Network", "用户选择：本次会话允许全部网络用途");
    }

    /// <summary>查询某用途当前是否已授权（不触发询问）。</summary>
    public static bool IsAllowed(NetworkPurpose purpose)
    {
        if (AllowAll) return true;
        if (_session.TryGetValue(purpose, out var decided)) return decided;
        var info = NetworkPurposeCatalog.ByPurpose(purpose);
        return info is not null && _IsPersistedAllowed(info.Id);
    }

    /// <summary>确认某用途是否已授权，必要时询问用户。</summary>
    public static async Task<bool> EnsureAsync(NetworkPurpose purpose, CancellationToken ct = default)
    {
        if (AllowAll) return true;
        if (_session.TryGetValue(purpose, out var decided)) return decided;

        var info = NetworkPurposeCatalog.ByPurpose(purpose);
        if (info is null) return false;

        if (_IsPersistedAllowed(info.Id))
        {
            _session[purpose] = true;
            return true;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session.TryGetValue(purpose, out decided)) return decided;
            var decision = await _AskAsync(info).ConfigureAwait(false);
            _session[purpose] = decision;
            return decision;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>确认未登记域名是否允许访问。</summary>
    public static async Task<bool> EnsureUnknownHostAsync(string host, CancellationToken ct = default)
    {
        if (AllowAll) return true;
        if (_unknownHosts.TryGetValue(host, out var decided)) return decided;

        var info = new NetworkPurposeInfo(
            NetworkPurpose.Unknown,
            "unknown:" + host,
            "未登记的目标", "Unregistered target",
            $"连接到 {host}。该域名不在 PCL Lite 的网络白名单中。",
            $"Connect to {host}. This host is not in the PCL Lite allow-list.",
            "发起该请求的功能", "the feature that requested it",
            [host]);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_unknownHosts.TryGetValue(host, out decided)) return decided;
            var decision = await _AskAsync(info).ConfigureAwait(false);
            _unknownHosts[host] = decision;
            return decision;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<bool> _AskAsync(NetworkPurposeInfo info)
    {
        if (PrivacyMode)
        {
            LogWrapper.Warn("Network",
                $"隐私模式已开启，拒绝网络用途「{info.Id}」（{string.Join(", ", info.Hosts)}）");
            try
            {
                PrivacyBlocked?.Invoke(info);
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "Network", "隐私模式拦截通知回调出错");
            }

            return false;
        }

        var handler = ConsentRequested;
        if (handler is null)
        {
            LogWrapper.Warn("Network", $"未注册授权回调，拒绝网络用途「{info.Id}」");
            return false;
        }

        try
        {
            var allowed = await handler(info).ConfigureAwait(false);
            LogWrapper.Info("Network",
                $"网络用途「{info.Id}」授权结果：{(allowed ? "允许" : "拒绝")}");
            return allowed;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Network", $"网络用途「{info.Id}」授权过程出错，按拒绝处理");
            return false;
        }
    }

    #region 白名单维护

    /// <summary>
    ///     首次启动时默认放行的用途。
    ///     <para>
    ///         只包含「没有它启动器就没法正常工作」的核心用途：更新、版本下载、加载器元数据、加密 DNS。
    ///         遥测默认不在其中；联机、仓库等需要用户显式同意的用途也不在其中。
    ///     </para>
    /// </summary>
    public static readonly NetworkPurpose[] DefaultAllowed =
    [
        NetworkPurpose.LauncherUpdate,
        NetworkPurpose.MinecraftDownload,
        NetworkPurpose.LoaderMetadata,
        NetworkPurpose.DnsResolve
    ];

    /// <summary>
    ///     写入首次启动的默认白名单。仅在从未写入过时生效，
    ///     因此用户在设置里撤销过的条目不会在下次启动时被重新加回来。
    /// </summary>
    public static void SeedDefaults()
    {
        if (Config.Network.ConsentSeeded) return;

        var set = _ParseAllowed();
        foreach (var purpose in DefaultAllowed)
        {
            var info = NetworkPurposeCatalog.ByPurpose(purpose);
            if (info is not null) set.Add(info.Id);
        }

        _SaveAllowed(set);
        Config.Network.ConsentSeeded = true;
        LogWrapper.Info("Network", $"已写入默认网络白名单：{string.Join(", ", set)}");
    }

    /// <summary>永久允许某用途。</summary>
    public static void AllowPermanently(NetworkPurpose purpose)
    {
        var info = NetworkPurposeCatalog.ByPurpose(purpose);
        if (info is null) return;
        _session[purpose] = true;
        var set = _ParseAllowed();
        set.Add(info.Id);
        _SaveAllowed(set);
    }

    /// <summary>撤销某用途的授权（含永久白名单条目）。</summary>
    public static void Revoke(NetworkPurpose purpose)
    {
        var info = NetworkPurposeCatalog.ByPurpose(purpose);
        _session[purpose] = false;
        if (info is null) return;
        var set = _ParseAllowed();
        if (set.Remove(info.Id)) _SaveAllowed(set);
    }

    /// <summary>清空白名单并重置本次会话的全部决策。</summary>
    public static void ResetAll()
    {
        _session.Clear();
        _unknownHosts.Clear();
        _allowAllForSession = false;
        _SaveAllowed([]);
        LogWrapper.Info("Network", "已清空网络白名单并重置授权状态");
    }

    /// <summary>获取当前永久白名单中的用途 Id 集合。</summary>
    public static IReadOnlyCollection<string> GetPersistedAllowed() => _ParseAllowed();

    private static HashSet<string> _ParseAllowed() =>
        new((Config.Network.AllowedPurposes ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private static bool _IsPersistedAllowed(string id) => _ParseAllowed().Contains(id);

    private static void _SaveAllowed(IEnumerable<string> ids) =>
        Config.Network.AllowedPurposes = string.Join("|", ids.Distinct(StringComparer.OrdinalIgnoreCase));

    #endregion
}
