using PCL.Core.IO.Storage.Cache;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.IO.Net.Http.Cache;

/// <summary>
/// HTTP 缓存处理器
/// </summary>
public class HttpCacheHandler : DelegatingHandler
{
    private ICacheService _cacheService;
    public HttpCacheHandler(HttpMessageHandler invoker, ICacheService cacheService)
    {
        InnerHandler = invoker;
        _cacheService = cacheService;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri?.ToString();
        // The cache is URI-keyed: bodies, credentials, byte ranges and Vary cannot
        // safely share an entry. Conditional requests belong to the caller.
        if (string.IsNullOrEmpty(uri) || request.Method != HttpMethod.Get ||
            request.Headers.Authorization is not null || request.Headers.Range is not null ||
            request.Headers.Contains("Cookie") || request.Headers.IfNoneMatch.Count != 0 ||
            request.Headers.IfModifiedSince is not null || request.Headers.Pragma.Count != 0 ||
            request.Headers.CacheControl is not null)
        {
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }

        // seek cache
        // Separate namespace: old entries contain only bytes and may contain an
        // HTTP error body previously reconstructed as 200 OK.
        var cacheKey = CacheKeys.ApiResponse("http-v2", uri);
        var cached = await _cacheService.GetAsync<CachedResponse>(cacheKey, ct).ConfigureAwait(false);
        if (cached.Found)
        {
            var cachedResponse = _DeserializeResponse(cached.Value!);
            cachedResponse.Headers.Add("X-Cache-Hit", "HIT");
            cachedResponse.RequestMessage = request;
            return cachedResponse;
        }

        // send request
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Headers.CacheControl is { NoStore: true } or { NoCache: true } or { Private: true } ||
            response.Headers.Vary.Count != 0 || response.Headers.Contains("Set-Cookie"))
        {
            return response;
        }

        // cache response
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var ttl = _ComputeTtl(response);

        if (ttl <= TimeSpan.Zero) return response;
        await _cacheService.SetAsync(cacheKey, new CachedResponse
        {
            Body = body,
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Version = response.Version.ToString(),
            Headers = response.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray()),
            ContentHeaders = response.Content.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray())
        }, new CachePolicy
        {
            AbsoluteExpiration = ttl,
            Group = "http",
            Tags = "http-response"
        }, ct).ConfigureAwait(false);

        // not matched, return original response
        response.Headers.Add("X-Cache-Hit", "MISS");
        return response;
    }

    private static TimeSpan? _ComputeTtl(HttpResponseMessage response)
    {
        var cc = response.Headers.CacheControl;
        if (cc?.MaxAge is not null)
        {
            return cc.MaxAge;
        }

        if (cc?.SharedMaxAge is not null)
        {
            return cc.SharedMaxAge;
        }

        return TimeSpan.FromMinutes(5);
    }

    public sealed record CachedResponse
    {
        public byte[] Body { get; init; } = [];
        public int StatusCode { get; init; } = 200;
        public string? ReasonPhrase { get; init; }
        public string Version { get; init; } = "1.1";
        public Dictionary<string, string[]> Headers { get; init; } = [];
        public Dictionary<string, string[]> ContentHeaders { get; init; } = [];
    }

    private static HttpResponseMessage _DeserializeResponse(CachedResponse data)
    {
        var response = new HttpResponseMessage((HttpStatusCode)data.StatusCode)
        {
            Content = new ByteArrayContent(data.Body),
            ReasonPhrase = data.ReasonPhrase,
            Version = Version.Parse(data.Version)
        };
        foreach (var header in data.Headers)
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var header in data.ContentHeaders)
            response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return response;
    }
}
