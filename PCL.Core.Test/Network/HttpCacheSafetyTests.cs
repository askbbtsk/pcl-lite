using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net.Http.Cache;
using PCL.Core.IO.Storage;
using PCL.Core.IO.Storage.Cache;

namespace PCL.Core.Test.Network;

[TestClass]
public class HttpCacheSafetyTests
{
    [TestMethod]
    public async Task ErrorAndNotModifiedResponsesAreNeverReplayedAsSuccess()
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable, HttpStatusCode.NotModified })
        {
            var calls = 0;
            using var client = new HttpClient(new HttpCacheHandler(new Handler(_ =>
            {
                calls++;
                return new HttpResponseMessage(status) { Content = new StringContent("failure") };
            }), new MemoryCache()));
            using var first = await client.GetAsync("https://cache.test/data");
            using var second = await client.GetAsync("https://cache.test/data");
            Assert.AreEqual(status, second.StatusCode);
            Assert.AreEqual(2, calls);
        }
    }

    [TestMethod]
    public async Task CachedResponsePreservesBodyContentTypeAndEtag()
    {
        var calls = 0;
        using var client = new HttpClient(new HttpCacheHandler(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
            return response;
        }), new MemoryCache()));
        using var first = await client.GetAsync("https://cache.test/data");
        using var second = await client.GetAsync("https://cache.test/data");
        Assert.AreEqual(1, calls);
        Assert.AreEqual("{\"ok\":true}", await second.Content.ReadAsStringAsync());
        Assert.AreEqual("application/json", second.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("\"abc\"", second.Headers.ETag?.Tag);
    }

    [TestMethod]
    public async Task PostsAuthorizationAndVaryBypassUriCache()
    {
        var calls = 0;
        using var client = new HttpClient(new HttpCacheHandler(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(calls.ToString()) };
            response.Headers.Vary.Add("Accept-Language");
            return response;
        }), new MemoryCache()));
        using var post1 = await client.PostAsync("https://cache.test/data", new StringContent("one"));
        using var post2 = await client.PostAsync("https://cache.test/data", new StringContent("two"));
        using var get1 = await client.GetAsync("https://cache.test/data");
        using var get2 = await client.GetAsync("https://cache.test/data");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user");
        using var private1 = await client.GetAsync("https://cache.test/private");
        using var private2 = await client.GetAsync("https://cache.test/private");
        Assert.AreEqual(6, calls);
    }

    [TestMethod]
    public async Task ExistingAnonymousCacheDoesNotConsumePostCredentialsOrConditionalRequests()
    {
        var calls = 0;
        using var client = new HttpClient(new HttpCacheHandler(new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(calls.ToString()) };
        }), new MemoryCache()));
        using var anonymous = await client.GetAsync("https://cache.test/data");
        using var hit = await client.GetAsync("https://cache.test/data");
        Assert.AreEqual("1", await hit.Content.ReadAsStringAsync());
        using var post = await client.PostAsync("https://cache.test/data", new StringContent("update"));
        Assert.AreEqual("2", await post.Content.ReadAsStringAsync());
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "https://cache.test/data");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"client-etag\""));
        using var revalidated = await client.SendAsync(conditional);
        Assert.AreEqual("3", await revalidated.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user");
        using var authenticated = await client.GetAsync("https://cache.test/data");
        Assert.AreEqual("4", await authenticated.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task NoStoreNoCacheAndPartialBodiesAreNeverStored()
    {
        foreach (var mode in new[] { "no-store", "no-cache", "private", "range" })
        {
            var calls = 0;
            using var client = new HttpClient(new HttpCacheHandler(new Handler(_ =>
            {
                calls++;
                var response = new HttpResponseMessage(mode == "range" ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                    { Content = new StringContent(calls.ToString()) };
                if (mode != "range") response.Headers.CacheControl = CacheControlHeaderValue.Parse(mode);
                return response;
            }), new MemoryCache()));
            using var first = await client.GetAsync("https://cache.test/data");
            using var second = await client.GetAsync("https://cache.test/data");
            Assert.AreEqual(2, calls, mode);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(respond(request));
    }

    private sealed class MemoryCache : ICacheService
    {
        private readonly Dictionary<string, object> _values = [];
        public Task SetAsync<T>(string key, T value, CachePolicy? policy = null, CancellationToken ct = default)
        { _values[key] = value!; return Task.CompletedTask; }
        public Task<CacheResult<T>> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) && value is T typed
                ? CacheResult<T>.Hit(typed, DateTime.UtcNow) : CacheResult<T>.Miss);
        public Task<bool> ExistsAsync(string key) => Task.FromResult(_values.ContainsKey(key));
        public Task<bool> DeleteAsync(string key) => Task.FromResult(_values.Remove(key));
        public Task<string?> GetCachedFilePathAsync(string key) => throw new NotSupportedException();
        public Task<string> CacheFileAsync(string key, Stream source, CachePolicy? policy = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteByGroupAsync(string groupName) => throw new NotSupportedException();
        public Task<int> DeleteByTagAsync(string tag) => throw new NotSupportedException();
        public Task<int> DeleteExpiredAsync() => throw new NotSupportedException();
        public Task<CacheStats> GetStatsAsync() => throw new NotSupportedException();
        public Task ClearAsync() => throw new NotSupportedException();
        public Task CompactAsync() => throw new NotSupportedException();
    }
}
