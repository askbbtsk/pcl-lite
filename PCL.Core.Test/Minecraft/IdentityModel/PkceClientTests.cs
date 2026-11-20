using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.IdentityModel.Extensions.Pkce;
using PCL.Core.Minecraft.IdentityModel.OAuth;

namespace PCL.Core.Test.Minecraft.IdentityModel;

[TestClass]
public sealed class PkceClientTests
{
    [TestMethod]
    public void Sha256ChallengeUsesBase64UrlHashOfVerifier()
    {
        var client = new PkceClient(new OAuthClientOptions
        {
            Meta = new EndpointMeta
            {
                AuthorizeEndpoint = "https://login.example/authorize",
                TokenEndpoint = "https://login.example/token"
            },
            GetClient = static () => new HttpClient(),
            RedirectUri = "http://localhost/callback",
            ClientId = "test-client"
        });

        var parameters = new Uri(client.GetAuthorizeUrl(["scope"], "state", null))
            .Query.TrimStart('?')
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair[1]));
        var challengeCode = (byte[])typeof(PkceClient)
            .GetProperty("_ChallengeCode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;
        var verifier = Base64Url(challengeCode);
        var expectedChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Assert.AreEqual("S256", parameters["code_challenge_method"]);
        Assert.AreEqual(expectedChallenge, parameters["code_challenge"]);
        Assert.AreEqual(43, parameters["code_challenge"].Length);
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
