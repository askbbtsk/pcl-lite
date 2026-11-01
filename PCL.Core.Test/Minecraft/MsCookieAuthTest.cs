using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile.Authentication;

namespace PCL.Core.Test.Minecraft;

/// <summary>
///     Cookie 凭据的识别与整理。
///     <para>
///         这一组测试盯着一个真实翻车场景：用户从浏览器扩展导出的是 <c>cookies.txt</c>
///         （Netscape 格式，制表符分隔），既没有 <c>Cookie:</c> 前缀也没有分号。
///         旧实现只认「带等号又带分号」，于是整份 cookie 文件掉进 refresh token 分支，
///         连试三个应用身份都回 <c>invalid_grant</c>，界面提示「令牌不归这个应用」——
///         方向完全指错。
///     </para>
/// </summary>
[TestClass]
public sealed class MsCookieAuthTest
{
    /// <summary>一行 Netscape Cookie。用制表符拼，别在编辑器里被替换成空格。</summary>
    private static string Line(string domain, string name, string value, long expiresAt = 1871153455L) =>
        string.Join('\t', domain, "TRUE", "/", "TRUE", expiresAt.ToString(), name, value);

    private static string File(params string[] lines) =>
        string.Join('\n', ["# Netscape HTTP Cookie File", string.Empty, .. lines]);

    [TestMethod]
    public void LooksLikeCookieDetectsNetscapeCookieFile()
    {
        var text = File(
            Line(".live.com", "MUID", "b93f4699244d4378869622ca278ff778"),
            Line(".login.live.com", "MSPCID", "a666aa01af159773"));

        Assert.IsTrue(MsCookieAuth.LooksLikeCookie(text));
    }

    [TestMethod]
    public void LooksLikeCookieStillRejectsRefreshToken()
    {
        // refresh token 里没有制表符、没有分号，只有一串字母数字
        Assert.IsFalse(MsCookieAuth.LooksLikeCookie("M.C558_BL2.0.U.MsaArtifacts.Cl6Shgl1Grxi*quWWX"));
    }

    [TestMethod]
    public void NormalizeCookieHeaderKeepsOnlyCookiesForLoginLive()
    {
        var text = File(
            Line(".microsoft.com", "MC1", "GUID=def713e3647a48a4a9305234921eaf56"),
            Line(".live.com", "MUID", "b93f4699244d4378869622ca278ff778"),
            Line(".login.live.com", "MSPCID", "a666aa01af159773"),
            Line("login.live.com", "__Host-MSAAUTHP", "secret-value"),
            Line("login.microsoftonline.com", "buid", "1.AS8AL6eh"));

        var header = MsCookieAuth.NormalizeCookieHeader(text);

        StringAssert.Contains(header, "MUID=b93f4699244d4378869622ca278ff778");
        StringAssert.Contains(header, "MSPCID=a666aa01af159773");
        StringAssert.Contains(header, "__Host-MSAAUTHP=secret-value");
        // 别的域的 Cookie 不该被带上：浏览器请求 login.live.com 时也不会带
        Assert.IsFalse(header.Contains("MC1", StringComparison.Ordinal));
        Assert.IsFalse(header.Contains("buid", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NormalizeCookieHeaderPrefersHostSpecificCookie()
    {
        var text = File(
            Line(".live.com", "MSPAuth", "broad"),
            Line("login.live.com", "MSPAuth", "specific"));

        var header = MsCookieAuth.NormalizeCookieHeader(text);

        StringAssert.Contains(header, "MSPAuth=specific");
        Assert.IsFalse(header.Contains("MSPAuth=broad", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NormalizeCookieHeaderHandlesHttpOnlyPrefix()
    {
        // #HttpOnly_ 是 Netscape 格式给 HttpOnly Cookie 加的前缀，不能当成注释整行丢掉
        var text = File("#HttpOnly_login.live.com\tFALSE\t/\tTRUE\t1871153455\tMSPOK\thttp-only-value");

        var header = MsCookieAuth.NormalizeCookieHeader(text);

        StringAssert.Contains(header, "MSPOK=http-only-value");
    }

    [TestMethod]
    public void NormalizeCookieHeaderDropsExpiredCookies()
    {
        var text = File(
            Line(".live.com", "OLD", "stale", 1000000000L),
            Line(".live.com", "MUID", "fresh"));

        var header = MsCookieAuth.NormalizeCookieHeader(text);

        Assert.IsFalse(header.Contains("OLD", StringComparison.Ordinal));
        StringAssert.Contains(header, "MUID=fresh");
    }

    [TestMethod]
    public void NormalizeCookieHeaderThrowsWhenFileHasNoLiveCookie()
    {
        var text = File(Line(".microsoft.com", "MC1", "GUID=def7"));

        var ex = Assert.ThrowsExactly<IdentityModelAuthenticationException>(
            () => MsCookieAuth.NormalizeCookieHeader(text));
        Assert.AreEqual("invalid_cookie", ex.Error);
    }

    [TestMethod]
    public void NormalizeCookieHeaderThrowsWhenEveryLiveCookieExpired()
    {
        var text = File(Line(".live.com", "MSPAuth", "stale", 1000000000L));

        var ex = Assert.ThrowsExactly<IdentityModelAuthenticationException>(
            () => MsCookieAuth.NormalizeCookieHeader(text));
        Assert.AreEqual("invalid_cookie", ex.Error);
    }

    [TestMethod]
    public void NormalizeCookieHeaderStillHandlesPlainPairString()
    {
        var header = MsCookieAuth.NormalizeCookieHeader("Cookie: MSPAuth=a; MSPCID=b; ");

        Assert.AreEqual("MSPAuth=a; MSPCID=b", header);
    }
}
