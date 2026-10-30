using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;
using System.Linq;

namespace PCL.Core.Test;

/// <summary>
/// 多源都报了版本时「挑哪一个」的测试。
/// <para>
/// 规则只有一条：<b>权重优先，权重打平才比版本号</b>。
/// </para>
/// <para>
/// 为什么权重必须压过版本号：权重是发布者亲手写在清单里的，版本号是远端仓库说了算的。
/// 一旦改成「版本最高的赢」，清单里只要混进一个别的项目（手滑粘错仓库，
/// 或者某个仓库被接管后发了个 2.x 的假包），它就会因为版本号更高直接赢，
/// 客户端于是去下载并执行别人的 exe。这组测试就是钉死这一点。
/// </para>
/// </summary>
[TestClass]
public class UpdateSourceSelectorTest
{
    private static UpdateSourceSelector.Candidate C(string version, int weight, int index) =>
        new(version, weight, index);

    // ─────────────────────────────────────────────────────────────
    //  权重优先
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldKeepOfficialEvenIfBackupReportsNewerVersion()
    {
        // 官方源(100) 报 1.5.12，备用源(40) 报 1.5.13。
        // 官方源没失败，就用官方的——不能因为备用源版本号高就跳过去
        var index = UpdateSourceSelector.PickBestIndex([C("1.5.12", 100, 0), C("1.5.13", 40, 1)]);

        Assert.AreEqual(0, index);
    }

    [TestMethod]
    public void ShouldNeverLetForeignMajorWin()
    {
        // 清单里混进了一个 2.15.1 的别家仓库，权重给到最低。
        // 它绝不能赢，否则就是「下载并执行别人的 exe」
        var index = UpdateSourceSelector.PickBestIndex([C("2.15.1", 10, 0), C("1.5.12", 100, 1)]);

        Assert.AreEqual(1, index);
    }

    [TestMethod]
    public void HigherWeightShouldBeatLowerWeightRegardlessOfVersionOrder()
    {
        // 权重高的那个版本更低，照样赢
        var index = UpdateSourceSelector.PickBestIndex(
            [C("1.9.0", 40, 0), C("1.2.0", 70, 1), C("1.5.0", 25, 2)]);

        Assert.AreEqual(1, index);
    }

    // ─────────────────────────────────────────────────────────────
    //  权重打平 → 版本号决胜
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldCompareVersionWhenWeightsTie()
    {
        var index = UpdateSourceSelector.PickBestIndex([C("1.5.12", 70, 0), C("1.5.13", 70, 1)]);

        Assert.AreEqual(1, index);
    }

    [TestMethod]
    public void ShouldRespectSemVerNotLexicalOrder()
    {
        // 字符串比较会把 "1.5.9" 排在 "1.5.10" 后面，这里必须按 SemVer 比
        var index = UpdateSourceSelector.PickBestIndex([C("1.5.9", 70, 0), C("1.5.10", 70, 1)]);

        Assert.AreEqual(1, index);
    }

    [TestMethod]
    public void PrereleaseShouldLoseToSameStableVersion()
    {
        // 1.5.13-pre < 1.5.13
        var index = UpdateSourceSelector.PickBestIndex([C("1.5.13-pre", 70, 0), C("1.5.13", 70, 1)]);

        Assert.AreEqual(1, index);
    }

    // ─────────────────────────────────────────────────────────────
    //  脏数据
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldSkipUnparsableVersions()
    {
        // 版本号解析不了的源直接丢掉：拿它比大小没有意义，
        // 而且它要是排到最前面，后面所有源都没机会了
        var index = UpdateSourceSelector.PickBestIndex([C("不是版本号", 999, 0), C("1.5.12", 40, 1)]);

        Assert.AreEqual(1, index);
    }

    [TestMethod]
    public void ShouldReturnMinusOneWhenNoCandidates()
    {
        Assert.AreEqual(-1, UpdateSourceSelector.PickBestIndex([]));
        Assert.AreEqual(-1, UpdateSourceSelector.PickBestIndex([C("垃圾", 100, 0)]));
    }

    [TestMethod]
    public void ShouldReturnSingleCandidateIndex()
    {
        Assert.AreEqual(7, UpdateSourceSelector.PickBestIndex([C("1.0.0", 50, 7)]));
    }

    // ─────────────────────────────────────────────────────────────
    //  完整顺序（调用方要「第一个身份校验不过就顺延」）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void OrderShouldPutHighestWeightFirst()
    {
        var candidates = new[]
        {
            C("1.4.0", 25, 0),
            C("1.5.13", 70, 1),
            C("1.5.12", 100, 2),
            C("1.5.9", 40, 3)
        };

        var order = UpdateSourceSelector.OrderByPreference(candidates);

        // 100 的排第一（哪怕它的版本不是最高的），然后 70、40、25
        CollectionAssert.AreEqual(new[] { 2, 1, 3, 0 }, order.ToArray());
    }

    [TestMethod]
    public void OrderShouldBeStableWithinSameWeightAndVersion()
    {
        // 权重、版本都一样时按调用方下标升序，保证结果可复现
        var order = UpdateSourceSelector.OrderByPreference(
            [C("1.5.12", 70, 5), C("1.5.12", 70, 2), C("1.5.12", 70, 9)]);

        CollectionAssert.AreEqual(new[] { 2, 5, 9 }, order.ToArray());
    }

    [TestMethod]
    public void OrderShouldDropUnparsableEntries()
    {
        var order = UpdateSourceSelector.OrderByPreference([C("垃圾", 100, 0), C("1.5.12", 70, 1)]);

        CollectionAssert.AreEqual(new[] { 1 }, order.ToArray());
    }

    // ─────────────────────────────────────────────────────────────
    //  分档：高权重档回来了，低权重档连问都不问
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void GroupShouldPutHighestWeightTierFirst()
    {
        var tiers = UpdateSourceSelector.GroupByWeight([(40, 0), (100, 1), (70, 2), (100, 3)]);

        Assert.AreEqual(3, tiers.Count);
        Assert.AreEqual(100, tiers[0].Weight);
        CollectionAssert.AreEqual(new[] { 1, 3 }, tiers[0].Indices.ToArray());
        Assert.AreEqual(70, tiers[1].Weight);
        CollectionAssert.AreEqual(new[] { 2 }, tiers[1].Indices.ToArray());
        Assert.AreEqual(40, tiers[2].Weight);
        CollectionAssert.AreEqual(new[] { 0 }, tiers[2].Indices.ToArray());
    }

    [TestMethod]
    public void GroupShouldKeepSingleTierWhenAllWeightsMatch()
    {
        // 同一档内的几个镜像：它们是一起被问的，谁新用谁
        var tiers = UpdateSourceSelector.GroupByWeight([(70, 0), (70, 1), (70, 2)]);

        Assert.AreEqual(1, tiers.Count);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, tiers[0].Indices.ToArray());
    }

    [TestMethod]
    public void GroupShouldBeEmptyWhenThereIsNoSource()
    {
        Assert.AreEqual(0, UpdateSourceSelector.GroupByWeight([]).Count);
    }

    [TestMethod]
    public void HigherTierAloneDecidesEvenIfLowerTierHasBiggerVersion()
    {
        // 这是整条规则的核心：把分档结果按顺序走一遍，第一档里挑出来的就是最终答案。
        // 第二档的 9.9.9 连参与比较的机会都没有
        var tiers = UpdateSourceSelector.GroupByWeight([(100, 0), (40, 1)]);
        var first = tiers[0].Indices.ToArray();

        var winner = UpdateSourceSelector.PickBestIndex(first.Select(i => i == 0 ? C("1.5.12", 100, 0) : C("9.9.9", 40, 1)));

        Assert.AreEqual(0, winner);
    }
}
