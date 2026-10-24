using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.IO.Net.Consent;

/// <summary>
/// 出站请求被网络白名单拦截时抛出。
/// </summary>
public sealed class NetworkAccessDeniedException(string host, string purposeId)
    : Exception($"网络访问被拒绝：{host}（用途：{purposeId}）")
{
    public string Host { get; } = host;
    public string PurposeId { get; } = purposeId;
}

/// <summary>
/// 出站守门人。所有 <see cref="HttpClient"/> 请求都会经过这里，
/// 按域名反查用途并确认授权，未通过则直接抛异常，请求不会离开进程。
/// </summary>
internal sealed class NetworkGuardHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var host = uri.Host;
        if (_IsLocal(host))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var purposes = NetworkPurposeCatalog.ByHost(host);
        if (purposes.Count == 0)
        {
            if (!await NetworkConsentService.EnsureUnknownHostAsync(host, cancellationToken).ConfigureAwait(false))
                throw new NetworkAccessDeniedException(host, "unknown");
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // 同一域名可能服务多个用途，任一已授权即放行
        if (purposes.Any(x => NetworkConsentService.IsAllowed(x.Purpose)))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        foreach (var info in purposes)
            if (await NetworkConsentService.EnsureAsync(info.Purpose, cancellationToken).ConfigureAwait(false))
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        throw new NetworkAccessDeniedException(host, string.Join(",", purposes.Select(x => x.Id)));
    }

    private static bool _IsLocal(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0" ||
        host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
