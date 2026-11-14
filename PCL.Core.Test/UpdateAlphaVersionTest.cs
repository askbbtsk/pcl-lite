using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;

namespace PCL.Core.Test;

[TestClass]
public class UpdateAlphaVersionTest
{
    [TestMethod]
    [DataRow("1.8.0-alpha-4", "1.8.0-snapshot-1")]
    [DataRow("v1.8.0-alpha-999", "1.8.0-snapshot-2")]
    [DataRow("1.8.0-ALPHA.10+build.99", "1.8.0-snapshot-1")]
    [DataRow("1.8.0-nightly-999", "1.8.0-alpha-1")]
    [DataRow("1.8.0-alpha-9", "1.8.0-alpha-10")]
    public void EarlierStageCannotOutrankLaterStage(string older, string newer)
    {
        Assert.IsTrue(UpdateVersion.IsNewer(newer, older));
        Assert.IsFalse(UpdateVersion.IsUpToDate(newer, older));
        Assert.IsTrue(UpdateVersion.IsUpToDate(older, newer));
    }

    [TestMethod]
    public void AlphaIsUnstableButCoreVersionStillWins()
    {
        Assert.AreEqual(UpdateChannelLevels.Latest, UpdateChannelLevels.FromVersionName("1.8.0-alpha-4"));
        Assert.IsTrue(UpdateVersion.IsNewer("1.8.1-alpha-1", "1.8.0-snapshot-99"));
    }
}
