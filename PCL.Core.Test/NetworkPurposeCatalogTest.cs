using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net.Consent;

namespace PCL.Core.Test;

/// <summary>
///     网络授权白名单与真实请求的一致性测试。
///     <para>
///         白名单是用户点「允许」时的唯一依据：登记漏了，守门人会在运行时弹出「未登记目标」；
///         登记多了，用户会为根本不存在的连接授权。两种都算回归，所以在这里钉死。
///     </para>
///     <para>
///         类上挂 <see cref="DoNotParallelizeAttribute" />：<c>NetworkPurposeCatalog</c> 是进程级静态表，
///         动态登记的用例会改全局状态，与只读用例并行跑会互相打架。
///     </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public class NetworkPurposeCatalogTest
{
    /// <summary>
    ///     更新链真实走 GitHub 的三种入口（<c>PCL.Core/App/ProjectLinks.cs</c> 的
    ///     <c>UpdateManifest</c> / <c>UpdateManifestMirror</c> / <c>UpdateManifestApi</c>），
    ///     这几个域名必须都在「启动器更新」名下，否则关掉「全部允许」后更新直接卡在弹窗上。
    /// </summary>
    [TestMethod]
    public void LauncherUpdateHostsCoverRealUpdateEndpoints()
    {
        var info = NetworkPurposeCatalog.ByPurpose(NetworkPurpose.LauncherUpdate);
        Assert.IsNotNull(info);

        foreach (var host in new[]
                 {
                     "raw.githubusercontent.com", "cdn.jsdelivr.net", "api.github.com", "github.com"
                 })
            CollectionAssert.Contains(info.Hosts, host, $"更新用途缺少域名 {host}");
    }

    /// <summary>下载源里的镜像与官方兜底地址都真实会被请求，不能漏登记。</summary>
    [TestMethod]
    public void MinecraftDownloadHostsCoverMirrorSources()
    {
        var info = NetworkPurposeCatalog.ByPurpose(NetworkPurpose.MinecraftDownload);
        Assert.IsNotNull(info);

        foreach (var host in new[] { "zkitefly.github.io", "alist.8mi.tech", "bmclapi2.bangbang93.com" })
            CollectionAssert.Contains(info.Hosts, host, $"下载用途缺少域名 {host}");
    }

    /// <summary>加载器用途里那些「看起来像官网、实际是下载地址」的域名同样要登记。</summary>
    [TestMethod]
    public void LoaderMetadataHostsCoverDownloadEndpoints()
    {
        var info = NetworkPurposeCatalog.ByPurpose(NetworkPurpose.LoaderMetadata);
        Assert.IsNotNull(info);

        foreach (var host in new[]
                 {
                     "maven.minecraftforge.net", "releases.labymod.net",
                     "files.minecraftforge.net", "meta.fabricmc.net"
                 })
            CollectionAssert.Contains(info.Hosts, host, $"加载器用途缺少域名 {host}");
    }

    /// <summary>登记表里不能再留从来没人请求过的域名。</summary>
    [TestMethod]
    public void CatalogHasNoGhostHosts()
    {
        // 这些地址已从用途表移除：幽灵源以及无法连接的 LiteLoader Jenkins 主机
        Assert.AreEqual(0, NetworkPurposeCatalog.ByHost("s3.pysio.online").Count);
        Assert.AreEqual(0, NetworkPurposeCatalog.ByHost("staticassets.naids.com").Count);
        Assert.AreEqual(0, NetworkPurposeCatalog.ByHost("jenkins.liteloader.com").Count);
    }

    /// <summary>每个用途名下的域名都能反查回它自己，否则 <c>ByHost</c> 的映射建错了。</summary>
    [TestMethod]
    public void EveryHostResolvesBackToItsPurpose()
    {
        foreach (var info in NetworkPurposeCatalog.All)
        foreach (var host in info.Hosts)
        {
            var resolved = NetworkPurposeCatalog.ByHost(host);
            CollectionAssert.Contains(resolved.Select(x => x.Purpose).ToList(), info.Purpose,
                $"域名 {host} 反查不到用途 {info.Purpose}");
        }
    }

    /// <summary>域名大小写不该影响授权结果。</summary>
    [TestMethod]
    public void ByHostIsCaseInsensitive()
    {
        var lower = NetworkPurposeCatalog.ByHost("api.modrinth.com");
        var upper = NetworkPurposeCatalog.ByHost("API.Modrinth.COM");
        Assert.AreEqual(lower.Count, upper.Count);
        Assert.AreEqual(1, lower.Count);
    }

    /// <summary>未登记的域名要返回空集合，交给 <c>EnsureUnknownHostAsync</c> 单独询问。</summary>
    [TestMethod]
    public void UnknownHostReturnsEmpty()
    {
        Assert.AreEqual(0, NetworkPurposeCatalog.ByHost("example.invalid").Count);
        Assert.AreEqual(0, NetworkPurposeCatalog.ByHost("").Count);
        Assert.AreEqual(0, NetworkPurposeCatalog.ByHost(null).Count);
    }

    /// <summary>
    ///     反代这类由上层持有的地址清单可以运行时补登记，
    ///     重复登记要幂等，且补完之后 <c>ByHost</c> 立刻认得。
    /// </summary>
    [TestMethod]
    public void RegisterExtraHostsIsIdempotent()
    {
        const string host = "proxy.test-only.invalid";

        Assert.AreEqual(0, NetworkPurposeCatalog.ByHost(host).Count);

        NetworkPurposeCatalog.RegisterExtraHosts(NetworkPurpose.LauncherUpdate, [host]);
        NetworkPurposeCatalog.RegisterExtraHosts(NetworkPurpose.LauncherUpdate, [host, "  " + host.ToUpperInvariant() + " "]);

        var resolved = NetworkPurposeCatalog.ByHost(host);
        Assert.AreEqual(1, resolved.Count);
        Assert.AreEqual(NetworkPurpose.LauncherUpdate, resolved[0].Purpose);

        var info = NetworkPurposeCatalog.ByPurpose(NetworkPurpose.LauncherUpdate);
        Assert.IsNotNull(info);
        Assert.AreEqual(1, info.Hosts.Count(x => string.Equals(x, host, System.StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>URL 清单要能直接喂进来，自动取主机名。</summary>
    [TestMethod]
    public void RegisterExtraHostsFromUrlsExtractsHost()
    {
        NetworkPurposeCatalog.RegisterExtraHostsFromUrls(NetworkPurpose.LauncherUpdate,
        [
            "https://mirror.test-only.invalid/some/path",
            "https://another.test-only.invalid:8443/",
            "not a url",
            ""
        ]);

        Assert.AreEqual(1, NetworkPurposeCatalog.ByHost("mirror.test-only.invalid").Count);
        Assert.AreEqual(1, NetworkPurposeCatalog.ByHost("another.test-only.invalid").Count);
    }

    /// <summary>空输入不能把登记表搞坏。</summary>
    [TestMethod]
    public void RegisterExtraHostsIgnoresEmptyInput()
    {
        var before = NetworkPurposeCatalog.All.Sum(x => x.Hosts.Length);

        NetworkPurposeCatalog.RegisterExtraHosts(NetworkPurpose.ModRepository, null);
        NetworkPurposeCatalog.RegisterExtraHosts(NetworkPurpose.ModRepository, []);
        NetworkPurposeCatalog.RegisterExtraHostsFromUrls(NetworkPurpose.ModRepository, null);

        Assert.AreEqual(before, NetworkPurposeCatalog.All.Sum(x => x.Hosts.Length));
    }

    /// <summary>用途 Id 是持久化白名单的键，改了会让老用户的白名单全部失效。</summary>
    [TestMethod]
    public void PurposeIdsAreStable()
    {
        Assert.AreEqual("update", NetworkPurposeCatalog.ByPurpose(NetworkPurpose.LauncherUpdate)?.Id);
        Assert.AreEqual("mc-download", NetworkPurposeCatalog.ByPurpose(NetworkPurpose.MinecraftDownload)?.Id);
        Assert.AreEqual("loader-meta", NetworkPurposeCatalog.ByPurpose(NetworkPurpose.LoaderMetadata)?.Id);
        Assert.AreEqual("mod-repo", NetworkPurposeCatalog.ByPurpose(NetworkPurpose.ModRepository)?.Id);
        Assert.AreEqual("doh", NetworkPurposeCatalog.ByPurpose(NetworkPurpose.DnsResolve)?.Id);
    }
}
