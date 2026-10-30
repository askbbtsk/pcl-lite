using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;

namespace PCL.Core.Test;

/// <summary>
/// 版本比较规则的测试。
/// <para>
/// 核心只有一条但极其容易写反：<b>hotfix 打在同号正式版之上，比正式版新</b>；
/// 而 pre / snapshot / nightly 比正式版旧。SemVer 的规则是「带后缀一律更低」，
/// 直接用会把热修复判成「旧版本」，于是 1.6.0 的用户永远收不到它。
/// </para>
/// </summary>
[TestClass]
public class UpdateVersionTest
{
    private static bool Newer(string a, string b) => UpdateVersion.IsNewer(a, b);

    // ─────────────────────────────────────────────────────────────
    //  三段版本号
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldCompareByNumericParts()
    {
        Assert.IsTrue(Newer("1.6.1", "1.6.0"));
        Assert.IsTrue(Newer("1.7.0", "1.6.99"));
        Assert.IsTrue(Newer("2.0.0", "1.99.99"));
        Assert.IsFalse(Newer("1.6.0", "1.6.0"));
        // 字符串比较会把 1.5.9 排在 1.5.10 后面，这里必须按数字比
        Assert.IsTrue(Newer("1.5.10", "1.5.9"));
    }

    // ─────────────────────────────────────────────────────────────
    //  hotfix：本次新增的语义
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void HotfixShouldBeatSameVersionStable()
    {
        // 这条就是整个改动的理由：1.6.0 的用户必须能收到 1.6.0-hotfix-1
        Assert.IsTrue(Newer("1.6.0-hotfix-1", "1.6.0"));
        Assert.IsFalse(Newer("1.6.0", "1.6.0-hotfix-1"));
    }

    [TestMethod]
    public void HigherHotfixNumberShouldWin()
    {
        Assert.IsTrue(Newer("1.6.0-hotfix-2", "1.6.0-hotfix-1"));
        Assert.IsTrue(Newer("1.6.0-hotfix-10", "1.6.0-hotfix-9"),
            "hotfix-10 比 hotfix-9 新：按数字比，不是按字典序");
        Assert.IsFalse(Newer("1.6.0-hotfix-1", "1.6.0-hotfix-2"));
    }

    [TestMethod]
    public void NextPatchShouldBeatPreviousHotfix()
    {
        // 1.6.1 比 1.6.0-hotfix-9 新，否则发下一个正式版会被旧热修复挡住
        Assert.IsTrue(Newer("1.6.1", "1.6.0-hotfix-9"));
    }

    [TestMethod]
    public void HotfixShouldBeatPrereleaseOfSameVersion()
    {
        Assert.IsTrue(Newer("1.6.0-hotfix-1", "1.6.0-pre-1"));
        Assert.IsTrue(Newer("1.6.0-hotfix-1", "1.6.0-snapshot"));
    }

    [TestMethod]
    public void MissingHotfixNumberShouldEqualOne()
    {
        // 没写序号的按 1 算，跟 pre 的老写法保持同一套心智
        Assert.AreEqual(0, UpdateVersion.Compare("1.6.0-hotfix", "1.6.0-hotfix-1"));
    }

    // ─────────────────────────────────────────────────────────────
    //  预发布：仍然比同号正式版旧
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void PrereleaseShouldLoseToSameVersionStable()
    {
        Assert.IsFalse(Newer("1.6.0-pre", "1.6.0"));
        Assert.IsFalse(Newer("1.6.0-snapshot", "1.6.0"));
        Assert.IsFalse(Newer("1.6.0-nightly", "1.6.0"));
        Assert.IsTrue(Newer("1.6.0", "1.6.0-pre"));
    }

    [TestMethod]
    public void HigherPrereleaseNumberShouldWin()
    {
        Assert.IsTrue(Newer("1.6.0-pre-2", "1.6.0-pre-1"));
        Assert.IsTrue(Newer("1.6.0-snapshot-3", "1.6.0-snapshot-2"));
    }

    [TestMethod]
    public void PrereleaseFamiliesShouldStayOrdered()
    {
        // nightly 最激进 → 最旧；snapshot 居中；pre 最接近正式版
        Assert.IsTrue(Newer("1.6.0-pre-1", "1.6.0-snapshot-1"));
        Assert.IsTrue(Newer("1.6.0-snapshot-1", "1.6.0-nightly-1"));
    }

    [TestMethod]
    public void BetaShouldStayOlderThanPre()
    {
        // 同档同号时退回 SemVer 的后缀比较，老顺序不能丢
        Assert.IsTrue(Newer("1.6.0-pre", "1.6.0-beta"));
    }

    [TestMethod]
    public void PrereleaseOfNewerBaseStillWins()
    {
        // 1.6.1-pre 比 1.6.0 新：段位优先于后缀档位
        Assert.IsTrue(Newer("1.6.1-pre", "1.6.0"));
    }

    // ─────────────────────────────────────────────────────────────
    //  脏数据
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void UnparsableShouldSortLast()
    {
        Assert.IsFalse(Newer("不是版本号", "1.6.0"));
        Assert.IsTrue(Newer("1.6.0", "不是版本号"));
        Assert.IsFalse(Newer("", "1.6.0"));
    }

    [TestMethod]
    public void ShouldAcceptLeadingV()
    {
        Assert.IsTrue(Newer("v1.6.1", "1.6.0"));
        Assert.IsTrue(Newer("V1.6.0-hotfix-1", "1.6.0"));
    }

    [TestMethod]
    public void ShouldIgnoreBuildMetadata()
    {
        Assert.AreEqual(0, UpdateVersion.Compare("1.6.0+build.5", "1.6.0+build.9"));
    }

    // ─────────────────────────────────────────────────────────────
    //  通道等级：热修复属于正式版通道
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void HotfixShouldBeStableChannel()
    {
        Assert.AreEqual(UpdateChannelLevels.Stable, UpdateChannelLevels.FromVersionName("1.6.0-hotfix-1"));
        Assert.AreEqual(UpdateChannelLevels.Stable, UpdateChannelLevels.FromVersionName("1.6.0-hotfix"));
    }

    [TestMethod]
    public void OtherSuffixesShouldKeepTheirLevels()
    {
        Assert.AreEqual(UpdateChannelLevels.Stable, UpdateChannelLevels.FromVersionName("1.6.0"));
        Assert.AreEqual(UpdateChannelLevels.Preview, UpdateChannelLevels.FromVersionName("1.6.0-pre-1"));
        Assert.AreEqual(UpdateChannelLevels.Snapshot, UpdateChannelLevels.FromVersionName("1.6.0-snapshot-1"));
        Assert.AreEqual(UpdateChannelLevels.Latest, UpdateChannelLevels.FromVersionName("1.6.0-nightly"));
    }

    // ─────────────────────────────────────────────────────────────
    //  「已是最新」的判定方向
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void HotfixShouldNotBeConsideredUpToDate()
    {
        // 装了 1.6.1，仓库里最新是 1.6.1-hotfix-1：这不是「已是最新」，必须提示更新。
        // 曾经这里写成 IsAtLeast(remote, current)，1.6.1-hotfix-1 ≥ 1.6.1 成立，
        // 于是热修复永远发不出去（界面上一直显示「已是最新版本」）。
        Assert.IsFalse(UpdateVersion.IsUpToDate("1.6.1-hotfix-1", "1.6.1"));
        Assert.IsFalse(UpdateVersion.IsUpToDate("1.6.1-hotfix-2", "1.6.1-hotfix-1"));
        Assert.IsFalse(UpdateVersion.IsUpToDate("1.6.2", "1.6.1"));
    }

    [TestMethod]
    public void SameVersionShouldBeUpToDate()
    {
        Assert.IsTrue(UpdateVersion.IsUpToDate("1.6.1", "1.6.1"));
        Assert.IsTrue(UpdateVersion.IsUpToDate("1.6.1-hotfix-1", "1.6.1-hotfix-1"));
        Assert.IsTrue(UpdateVersion.IsUpToDate("v1.6.1", "1.6.1"));
    }

    [TestMethod]
    public void NewerLocalShouldNotPromptDowngrade()
    {
        // 从激进通道切回稳定版：手里的 nightly 比稳定版最新还新，不能提示「降级」。
        Assert.IsTrue(UpdateVersion.IsUpToDate("1.6.1", "1.6.2-nightly-1"));
        Assert.IsTrue(UpdateVersion.IsUpToDate("1.6.1-hotfix-1", "1.6.2"));
    }
}
