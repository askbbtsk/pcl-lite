using System;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Http;

namespace PCL.Core.Minecraft.Yggdrasil;

public static class ApiLocation
{
    public static async Task<string> TryRequestAsync(string address)
    {
        var originAddr = address.StartsWith("http") ? address : $"https://{address}";
        var originUri = new Uri(originAddr);
        using var response = await HttpRequest
            .CreateHead(originAddr)
            .SendAsync();

        
        if (!response.TryGetHeader("X-Authlib-Injector-Api-Location", out var location)
            || location.Length == 0) 
            return originAddr;

        var resultAddr = location[0];

        if (string.IsNullOrEmpty(resultAddr)) return originAddr;
        return ResolveRedirect(originUri, resultAddr);
    }

    internal static string ResolveRedirect(Uri origin, string location)
    {
        var target = new Uri(origin, location);
        if (target.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Authentication API redirects must use HTTP or HTTPS.");
        if (origin.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp)
            target = new UriBuilder(target)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = target.IsDefaultPort ? -1 : target.Port
            }.Uri;
        return target.AbsoluteUri;
    }
}

