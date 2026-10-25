using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;

namespace PCL.Core.Test;

/// <summary>
/// 更新通道的等级判定测试。
/// <para>
/// 这里锁死两件事：后缀怎么算等级，以及**通道是包含式的**。
/// 后者尤其要紧——如果「始终最新」不含正式版，选了它的用户会在正式版发布后
/// 永远卡在手里的 nightly 上，因为他们等的是一个比新正式版号还大的预发布版本。
/// </para>
/// </summary>
[TestClass]
public class UpdateChannelLevelsTest
{
    [DataTestMethod]
    [DataRow("1.3.2", UpdateChannelLevels.Stable)]
    [DataRow("v1.3.2", UpdateChannelLevels.Stable)]
    [DataRow("V1.3.2", UpdateChannelLevels.Stable)]
    [DataRow("1.3.2.0", UpdateChannelLevels.Stable)]
    [DataRow("1.3.2-pre", UpdateChannelLevels.Preview)]
    [DataRow("1.3.2-preview", UpdateChannelLevels.Preview)]
    [DataRow("1.3.2-beta", UpdateChannelLevels.Preview)]
    [DataRow("1.3.2-alpha", UpdateChannelLevels.Preview)]
    [DataRow("1.3.2-rc1", UpdateChannelLevels.Preview)]
    [DataRow("1.3.2-snapshot", UpdateChannelLevels.Snapshot)]
    [DataRow("1.3.2-nightly", UpdateChannelLevels.Latest)]
    public void FromVersionNameShouldClassifySuffix(string versionName, int expected)
    {
        Assert.AreEqual(expected, UpdateChannelLevels.FromVersionName(versionName));
    }

    [DataTestMethod]
    [DataRow("1.3.2-nightly.20261025", UpdateChannelLevels.Latest)]
    [DataRow("1.3.2-snapshot.3", UpdateChannelLevels.Snapshot)]
    [DataRow("1.3.2-beta+build.5", UpdateChannelLevels.Preview)]
    [DataRow("1.3.2+build.5", UpdateChannelLevels.Stable)]
    public void FromVersionNameShouldIgnoreBuildMetadata(string versionName, int expected)
    {
        // '+' 之后是构建元数据，与不稳定等级无关
        Assert.AreEqual(expected, UpdateChannelLevels.FromVersionName(versionName));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("-")]
    public void FromSuffixShouldTreatBlankAsStable(string? suffix)
    {
        Assert.AreEqual(UpdateChannelLevels.Stable, UpdateChannelLevels.FromSuffix(suffix));
    }

    [DataTestMethod]
    [DataRow("SNAPSHOT", UpdateChannelLevels.Snapshot)]
    [DataRow("Nightly", UpdateChannelLevels.Latest)]
    [DataRow("-beta", UpdateChannelLevels.Preview)]
    public void FromSuffixShouldBeCaseInsensitive(string suffix, int expected)
    {
        Assert.AreEqual(expected, UpdateChannelLevels.FromSuffix(suffix));
    }

    [TestMethod]
    public void FromVersionNameShouldTreatNullAsStable()
    {
        Assert.AreEqual(UpdateChannelLevels.Stable, UpdateChannelLevels.FromVersionName(null));
    }

    /// <summary>
    /// 通道的包含关系：版本等级 &lt;= 通道等级 时该版本可见。
    /// </summary>
    [DataTestMethod]
    [DataRow(UpdateChannelLevels.Stable, UpdateChannelLevels.Preview, true, DisplayName = "正式版 → 预览体验：可见")]
    [DataRow(UpdateChannelLevels.Preview, UpdateChannelLevels.Preview, true, DisplayName = "pre → 预览体验：可见")]
    [DataRow(UpdateChannelLevels.Snapshot, UpdateChannelLevels.Preview, false, DisplayName = "snapshot → 预览体验：不可见")]
    [DataRow(UpdateChannelLevels.Latest, UpdateChannelLevels.Preview, false, DisplayName = "nightly → 预览体验：不可见")]
    [DataRow(UpdateChannelLevels.Stable, UpdateChannelLevels.Snapshot, true, DisplayName = "正式版 → 快照尝鲜：可见")]
    [DataRow(UpdateChannelLevels.Preview, UpdateChannelLevels.Snapshot, true, DisplayName = "pre → 快照尝鲜：可见")]
    [DataRow(UpdateChannelLevels.Snapshot, UpdateChannelLevels.Snapshot, true, DisplayName = "snapshot → 快照尝鲜：可见")]
    [DataRow(UpdateChannelLevels.Latest, UpdateChannelLevels.Snapshot, false, DisplayName = "nightly → 快照尝鲜：不可见")]
    [DataRow(UpdateChannelLevels.Stable, UpdateChannelLevels.Latest, true, DisplayName = "正式版 → 始终最新：可见")]
    [DataRow(UpdateChannelLevels.Preview, UpdateChannelLevels.Latest, true, DisplayName = "pre → 始终最新：可见")]
    [DataRow(UpdateChannelLevels.Snapshot, UpdateChannelLevels.Latest, true, DisplayName = "snapshot → 始终最新：可见")]
    [DataRow(UpdateChannelLevels.Latest, UpdateChannelLevels.Latest, true, DisplayName = "nightly → 始终最新：可见")]
    public void ChannelShouldBeInclusive(int versionLevel, int channelLevel, bool visible)
    {
        Assert.AreEqual(visible, versionLevel <= channelLevel);
    }

    /// <summary>
    /// 等级数值是契约的一部分：主项目里的取版本通道枚举按同一组数字定义，
    /// 用户配置枚举也靠这组数字直接强转，改动会静默错位，所以在这里钉死。
    /// </summary>
    [TestMethod]
    public void LevelValuesAreContract()
    {
        Assert.AreEqual(0, UpdateChannelLevels.Stable);
        Assert.AreEqual(1, UpdateChannelLevels.Preview);
        Assert.AreEqual(2, UpdateChannelLevels.Snapshot);
        Assert.AreEqual(3, UpdateChannelLevels.Latest);
    }

    [TestMethod]
    public void DescribeShouldNameEveryLevel()
    {
        Assert.AreEqual("正式版", UpdateChannelLevels.Describe(UpdateChannelLevels.Stable));
        StringAssert.Contains(UpdateChannelLevels.Describe(UpdateChannelLevels.Preview), "pre");
        StringAssert.Contains(UpdateChannelLevels.Describe(UpdateChannelLevels.Snapshot), "snapshot");
        StringAssert.Contains(UpdateChannelLevels.Describe(UpdateChannelLevels.Latest), "nightly");
    }
}
