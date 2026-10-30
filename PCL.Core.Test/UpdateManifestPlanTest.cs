using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;
using System;
using System.Linq;

namespace PCL.Core.Test;

/// <summary>
/// 「这次用哪份清单」的决策测试。
/// <para>
/// 这是整个更新功能最不能出错的一段：<c>updatev2.json</c> 是新加的文件，
/// 它不在线、被反代回了一页 HTML、或者手改坏了，<b>任何一种都必须静默退化成单源</b>，
/// 而不是让用户看到「检查更新失败」。所以这里把「v2 坏 → 用 v1」的每一种坏法都钉住。
/// </para>
/// </summary>
[TestClass]
public class UpdateManifestPlanTest
{
    private const string V1Body = "{\"update\": \"askbbtsk/pcl-lite/releases/latest\"}";

    private const string V2Body = """
        {
          "schema": 2,
          "sources": [
            { "repo": "askbbtsk/pcl-lite", "tier": "official" },
            { "repo": "backup/pcl-lite", "tier": "mirror" }
          ]
        }
        """;

    [TestMethod]
    public void ShouldPreferV2WhenAvailable()
    {
        var plan = UpdateManifestPlanner.Build(V2Body, V1Body);

        Assert.IsTrue(plan.UsedV2);
        Assert.IsNull(plan.V2Failure);
        Assert.AreEqual(2, plan.Sources.Count);
    }

    [TestMethod]
    public void ShouldNotEvenNeedV1WhenV2Works()
    {
        var plan = UpdateManifestPlanner.Build(V2Body, null);

        Assert.IsTrue(plan.UsedV2);
        Assert.AreEqual(2, plan.Sources.Count);
    }

    // ─────────────────────────────────────────────────────────────
    //  v2 的各种坏法 → 必须退到 v1
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldFallBackWhenV2Missing()
    {
        // 最正常的回退场景：updatev2.json 还没推上去，请求 404
        var plan = UpdateManifestPlanner.Build(null, V1Body);

        Assert.IsFalse(plan.UsedV2);
        Assert.AreEqual(1, plan.Sources.Count);
        Assert.AreEqual("askbbtsk", plan.Sources[0].Owner);
        Assert.AreEqual("pcl-lite", plan.Sources[0].Repo);
        Assert.AreEqual(UpdateManifestV2.TierOfficial, plan.Sources[0].Tier);
        Assert.IsNotNull(plan.V2Failure);
    }

    [TestMethod]
    public void ShouldFallBackWhenV2IsHtml()
    {
        var plan = UpdateManifestPlanner.Build("<html><body>404: Not Found</body></html>", V1Body);

        Assert.IsFalse(plan.UsedV2);
        Assert.AreEqual(1, plan.Sources.Count);
        StringAssert.Contains(plan.V2Failure, "网页");
    }

    [TestMethod]
    public void ShouldFallBackWhenV2IsBrokenJson()
    {
        var plan = UpdateManifestPlanner.Build("{ \"sources\": [ ", V1Body);

        Assert.IsFalse(plan.UsedV2);
        Assert.AreEqual(1, plan.Sources.Count);
        Assert.IsNotNull(plan.V2Failure);
    }

    [TestMethod]
    public void ShouldFallBackWhenV2HasNoValidSource()
    {
        var plan = UpdateManifestPlanner.Build("""{ "sources": ["404: Not Found"] }""", V1Body);

        Assert.IsFalse(plan.UsedV2);
        Assert.AreEqual(1, plan.Sources.Count);
        Assert.IsNotNull(plan.V2Failure);
    }

    [TestMethod]
    public void ShouldFallBackWhenV2IsEmpty()
    {
        var plan = UpdateManifestPlanner.Build("   ", V1Body);

        Assert.IsFalse(plan.UsedV2);
        Assert.AreEqual("askbbtsk", plan.Sources[0].Owner);
    }

    // ─────────────────────────────────────────────────────────────
    //  两份都不行 → 抛，交给上层换下一个更新源
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ShouldThrowWhenBothUnavailable()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPlanner.Build(null, null));
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPlanner.Build("<html/>", null));
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPlanner.Build("<html/>", "<html/>"));
    }

    [TestMethod]
    public void ErrorMessageShouldMentionBothFiles()
    {
        var ex = Assert.ThrowsExactly<FormatException>(
            () => UpdateManifestPlanner.Build("<html/>", "<html/>"));

        StringAssert.Contains(ex.Message, "多源清单");
        StringAssert.Contains(ex.Message, "单指针清单");
    }

    [TestMethod]
    public void V1ShouldWinEvenIfV2HasProblems()
    {
        // v2 里有一条坏条目但整体可用：应该用 v2，并记下问题，不要因为一条坏条目退回 v1
        var plan = UpdateManifestPlanner.Build("""
            { "sources": ["askbbtsk/pcl-lite", "404: Not Found"] }
            """, V1Body);

        Assert.IsTrue(plan.UsedV2);
        Assert.AreEqual(1, plan.Sources.Count);
        Assert.AreEqual(1, plan.Problems.Count);
    }
}
