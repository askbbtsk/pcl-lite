using System;
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
/// Named HTTP transport gate. Permissions belong to the requesting module,
/// not to another feature that happens to use the same domain.
/// </summary>
internal sealed class NetworkGuardHandler(NetworkRequestSource? source = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var owner = NetworkConnectionManager.GetSource(request, source ?? new NetworkRequestSource("Launcher"));
        NetworkConnectionManager.SetSource(request, owner);
        await NetworkConnectionManager.AuthorizeAsync(owner, uri, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
