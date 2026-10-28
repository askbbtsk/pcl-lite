using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net;

namespace PCL.Core.Test;

/// <summary>
///     下载源镜像改写的回归测试。
///     <para>
///         只测纯函数（<see cref="DownloadSource.Order(bool, System.Collections.Generic.IEnumerable{string}, System.Collections.Generic.IEnumerable{string})" />、
///         <see cref="DownloadSource.ToMirror" />），不碰 <c>Config</c> ——
///         配置服务在单元测试里起不来，<c>PreferMojang</c> 会走兜底分支。
///     </para>
/// </summary>
[TestClass]
public class DownloadSourceTest
{
    private const string OfficialAsset = "https://resources.download.minecraft.net/ab/abcdef0123456789";

    [TestMethod]
    public void Order_PrefersMirror()
    {
        var ordered = DownloadSource.Order(false, ["official"], ["mirror"]).ToList();
        CollectionAssert.AreEqual(new[] { "mirror", "official" }, ordered);
    }

    [TestMethod]
    public void Order_PrefersOfficial()
    {
        var ordered = DownloadSource.Order(true, ["official"], ["mirror"]).ToList();
        CollectionAssert.AreEqual(new[] { "official", "mirror" }, ordered);
    }

    [TestMethod]
    public void Order_Deduplicates()
    {
        // launcher/meta 那条会把原地址也塞进镜像列表，去重后不能出现两次
        var ordered = DownloadSource.Order(false, ["same"], ["same"]).ToList();
        CollectionAssert.AreEqual(new[] { "same" }, ordered);
    }

    [TestMethod]
    public void Assets_RewritesToBmclapi()
    {
        var candidates = DownloadSource.Assets(OfficialAsset);

        Assert.AreEqual(2, candidates.Count);
        Assert.IsTrue(candidates.Contains("https://bmclapi2.bangbang93.com/assets/ab/abcdef0123456789"));
        Assert.IsTrue(candidates.Contains(OfficialAsset));
    }

    [TestMethod]
    public void Assets_UpgradesPlainHttp()
    {
        var candidates = DownloadSource.Assets("http://resources.download.minecraft.net/ab/abcdef0123456789");
        Assert.IsTrue(candidates.All(x => x.StartsWith("https://")));
    }

    [TestMethod]
    public void Libraries_ForgeHasNoOfficialCandidate()
    {
        // Forge / Fabric / NeoForge 的构建产物官方源在境外，加官方源只是白等一轮
        var candidates = DownloadSource.Libraries(
            "https://libraries.minecraft.net/net/minecraftforge/forge/1.20.1/forge-1.20.1.jar");

        Assert.AreEqual(2, candidates.Count);
        Assert.IsTrue(candidates.All(x => x.Contains("bmclapi2.bangbang93.com")));
    }

    [TestMethod]
    public void Libraries_VanillaKeepsOfficial()
    {
        // 原版库给三条：官方 + 镜像的两条路径（bmclapi 的 maven 与 libraries 都能取）
        var candidates = DownloadSource.Libraries(
            "https://libraries.minecraft.net/com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar");

        Assert.AreEqual(3, candidates.Count);
        Assert.IsTrue(candidates.Contains(
            "https://libraries.minecraft.net/com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar"));
        Assert.IsTrue(candidates.Contains(
            "https://bmclapi2.bangbang93.com/maven/com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar"));
        Assert.IsTrue(candidates.Contains(
            "https://bmclapi2.bangbang93.com/libraries/com/mojang/brigadier/1.0.18/brigadier-1.0.18.jar"));
    }

    [TestMethod]
    public void LauncherOrMeta_RewritesLaunchermeta()
    {
        var candidates = DownloadSource.LauncherOrMeta(
            "https://launchermeta.mojang.com/v1/packages/f6ad102b/1.8.json");

        Assert.AreEqual(2, candidates.Count);
        Assert.IsTrue(candidates.Contains("https://bmclapi2.bangbang93.com/v1/packages/f6ad102b/1.8.json"));
    }

    [TestMethod]
    public void ToMirror_MapsAllKnownHosts()
    {
        Assert.AreEqual("https://bmclapi2.bangbang93.com/assets/x",
            DownloadSource.ToMirror("https://resources.download.minecraft.net/x", "assets"));
        Assert.AreEqual("https://bmclapi2.bangbang93.com/maven/x",
            DownloadSource.ToMirror("https://libraries.minecraft.net/x", "maven"));
        Assert.AreEqual("https://bmclapi2.bangbang93.com/libraries/x",
            DownloadSource.ToMirror("https://piston-data.mojang.com/x", "libraries"));
        Assert.AreEqual("https://bmclapi2.bangbang93.com/maven/x",
            DownloadSource.ToMirror("https://piston-meta.mojang.com/x", "maven"));
    }
}
