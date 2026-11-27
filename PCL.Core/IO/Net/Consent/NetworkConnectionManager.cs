using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Consent;

/// <summary>A connection's owner travels with the request, never in an HTTP header.</summary>
public sealed record NetworkRequestSource(string Name, NetworkPurpose Purpose = NetworkPurpose.Unknown)
{
    public static NetworkRequestSource FromCaller(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) name = "Launcher";
        var normalized = path.Replace('\\', '/');
        var purpose = normalized switch
        {
            _ when normalized.Contains("/Lunar/", StringComparison.Ordinal) => NetworkPurpose.Lunar,
            _ when normalized.Contains("/Updates/", StringComparison.Ordinal) => NetworkPurpose.LauncherUpdate,
            _ when normalized.Contains("/Authentication/", StringComparison.Ordinal) ||
                   normalized.Contains("/IdentityModel/", StringComparison.Ordinal) ||
                   normalized.Contains("/Yggdrasil/", StringComparison.Ordinal) => NetworkPurpose.Authentication,
            _ when normalized.Contains("/Plugins/", StringComparison.Ordinal) ||
                   normalized.Contains("/PagePlugins/", StringComparison.Ordinal) => NetworkPurpose.Plugins,
            _ when name.Contains("Bedrock", StringComparison.Ordinal) => NetworkPurpose.Bedrock,
            _ when name.Contains("Modrinth", StringComparison.Ordinal) ||
                   name.Contains("CurseForge", StringComparison.Ordinal) ||
                   name.Contains("ModComp", StringComparison.Ordinal) => NetworkPurpose.ModRepository,
            _ when name.Contains("Forge", StringComparison.Ordinal) ||
                   name.Contains("Fabric", StringComparison.Ordinal) ||
                   name.Contains("OptiFine", StringComparison.Ordinal) ||
                   name.Contains("LiteLoader", StringComparison.Ordinal) => NetworkPurpose.LoaderMetadata,
            _ when name.Contains("Java", StringComparison.Ordinal) ||
                   name.Contains("Download", StringComparison.Ordinal) ||
                   name.Contains("Manifest", StringComparison.Ordinal) ||
                   name is "ModAssets" or "ModLibrary" => NetworkPurpose.MinecraftDownload,
            _ => NetworkPurpose.Unknown
        };
        return new NetworkRequestSource(name, purpose);
    }
}

/// <summary>Authorizes a named owner before opening its transport and returns the result to that owner.</summary>
public static class NetworkConnectionManager
{
    private static readonly HttpRequestOptionsKey<NetworkRequestSource> SourceKey = new("PCL.Network.Source");

    public static void SetSource(HttpRequestMessage request, NetworkRequestSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
        request.Options.Set(SourceKey, source);
    }

    internal static NetworkRequestSource GetSource(HttpRequestMessage request, NetworkRequestSource fallback) =>
        request.Options.TryGetValue(SourceKey, out var source) ? source : fallback;

    public static async Task AuthorizeAsync(NetworkRequestSource source, Uri target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!target.IsAbsoluteUri) throw new ArgumentException("A connection requires an absolute target.", nameof(target));
        if (target.IsLoopback) return;

        var allowed = source.Purpose == NetworkPurpose.Unknown
            ? await NetworkConsentService.EnsureUnknownHostAsync(target.Host, cancellationToken, source.Name)
                .ConfigureAwait(false)
            : await NetworkConsentService.EnsureAsync(source.Purpose, cancellationToken, source.Name, target.Host)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        LogWrapper.Debug("Network", $"{source.Name}: {target.Scheme}://{target.IdnHost}:{target.Port} — {(allowed ? "allowed" : "denied")}");
        if (!allowed)
            throw new NetworkAccessDeniedException(target.Host, source.Name);
    }

    public static HttpClient CreateHttpClient(string name, NetworkPurpose purpose = NetworkPurpose.Unknown,
        TimeSpan? timeout = null, Func<HttpMessageHandler>? transportFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var handler = new NetworkGuardHandler(new NetworkRequestSource(name, purpose))
        {
            InnerHandler = new DeferredTransport(transportFactory ?? NetworkService.CreateTransport)
        };
        var client = new HttpClient(handler);
        if (timeout is { } value) client.Timeout = value;
        return client;
    }

    public static async Task ConnectWebSocketAsync(ClientWebSocket socket, NetworkRequestSource source, Uri target,
        CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(source, target, cancellationToken).ConfigureAwait(false);
        await socket.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ConnectSocketAsync(Socket socket, NetworkRequestSource source, EndPoint target,
        CancellationToken cancellationToken = default)
    {
        var address = target switch
        {
            IPEndPoint endpoint => new UriBuilder("tcp", endpoint.Address.ToString(), endpoint.Port).Uri,
            DnsEndPoint endpoint => new UriBuilder("tcp", endpoint.Host, endpoint.Port).Uri,
            _ => throw new ArgumentException("Unsupported network endpoint.", nameof(target))
        };
        await AuthorizeAsync(source, address, cancellationToken).ConfigureAwait(false);
        await socket.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
    }

    // Static catalogs may be used before application configuration starts.
    // Delay proxy/DNS configuration until a permitted request actually needs a transport.
    private sealed class DeferredTransport(Func<HttpMessageHandler> factory) : HttpMessageHandler
    {
        private readonly object _sync = new();
        private HttpMessageInvoker? _transport;
        private bool _disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _transport ??= new HttpMessageInvoker(factory());
                return _transport.SendAsync(request, cancellationToken);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                lock (_sync)
                {
                    _disposed = true;
                    _transport?.Dispose();
                }
            base.Dispose(disposing);
        }
    }
}
