using System.Net;
using System.Net.Http;
using PCL.Core.App;

namespace PCL.Network;

public static class RequestSigning
{
    internal static string SecretCdnSign(string urlWithMark)
    {
        if (!urlWithMark.EndsWithF("{CDN}"))
            return urlWithMark;
        return urlWithMark.Replace("{CDN}", "").Replace(" ", "%20");
    }
    
    /// <summary>
    ///     设置 Headers 的 UA、Referer。
    /// </summary>
    internal static void SecretHeadersSign(string url, ref HttpRequestMessage client, bool useBrowserUserAgent = false,
        string customUserAgent = "")
    {
        client.Version = HttpVersion.Version20;
        client.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) 
            && (parsedUri.Host == "api.curseforge.com"
            || parsedUri.Host == "edge.forgecdn.net"
            || parsedUri.Host == "mediafilez.forgecdn.net"))
        {
            client.Headers.Add("x-api-key", Secrets.CurseForgeAPIKey);
        }
        // 不带任何版本号：UA 里塞构建号等于每个请求都在上报「谁在用哪个版本」，可以据此统计用户数量。
        // 这里只保留产品名，需要伪装成浏览器时再补浏览器段。
        var userAgent = !string.IsNullOrEmpty(customUserAgent)
            ? customUserAgent
            : useBrowserUserAgent
                ? "PCL2 PCLCE Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0"
                : "PCL2 PCLCE";
        client.Headers.Add("User-Agent", userAgent);
    }
}