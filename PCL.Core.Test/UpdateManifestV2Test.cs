using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;
using System;
using System.Linq;

namespace PCL.Core.Test;

/// <summary>
/// 多源清单 <c>updatev2.json</c> 的解析测试。
/// <para>
/// 这份文件的头号要求不是「能解析」，而是「<b>解析不了也不能让更新挂掉</b>」：
/// 它只是一个锦上添花的文件，写坏了、被反代回了一页 HTML、或者压根没推上去，
/// 都必须能静默退回 v1 的单指针。所以这里的用例分成两组：
/// 能解析的写法（宽容度）、和必须被判定为不可用的写法（防呆）。
/// </para>
/// </summary>
[TestClass]
public class UpdateManifestV2Test
{
    // ─────────────────────────────────────────────────────────────
    //  能解析的写法
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseShouldReadSourceArray()
    {
        var result = UpdateManifestV2.Parse("""
            {
              "schema": 2,
              "sources": [
                { "repo": "askbbtsk/pcl-lite", "tier": "official" },
                { "repo": "backup/pcl-lite", "tier": "mirror" }
              ]
            }
            """);

        Assert.AreEqual(2, result.Sources.Count);
        // 权重降序：official(100) 在 mirror(70) 前面
        Assert.AreEqual("askbbtsk", result.Sources[0].Owner);
        Assert.AreEqual("pcl-lite", result.Sources[0].Repo);
        Assert.AreEqual(100, result.Sources[0].Weight);
        Assert.AreEqual(70, result.Sources[1].Weight);
    }

    [TestMethod]
    public void ParseShouldAcceptBareStrings()
    {
        var result = UpdateManifestV2.Parse("""
            ["askbbtsk/pcl-lite", "other/pcl-lite"]
            """);

        Assert.AreEqual(2, result.Sources.Count);
        // 没写 tier 时按镜像源算，权重 70
        Assert.AreEqual(UpdateManifestV2.WeightOfTier(UpdateManifestV2.TierMirror), result.Sources[0].Weight);
    }

    [TestMethod]
    public void ParseShouldAcceptTopLevelArrayOfObjects()
    {
        var result = UpdateManifestV2.Parse("""
            [
              { "repo": "askbbtsk/pcl-lite", "tier": "official", "weight": 999 },
              { "repo": "other/pcl-lite" }
            ]
            """);

        Assert.AreEqual(2, result.Sources.Count);
        Assert.AreEqual(999, result.Sources[0].Weight, "显式 weight 应当盖过 tier 推导值");
    }

    [TestMethod]
    public void ParseShouldAcceptV1ShapeAsSingleOfficialSource()
    {
        // 有人可能把 updatev2.json 直接写成 v1 的样子。不能因此炸掉
        var result = UpdateManifestV2.Parse("{\"update\": \"askbbtsk/pcl-lite/releases/latest\"}");

        Assert.AreEqual(1, result.Sources.Count);
        Assert.AreEqual("askbbtsk", result.Sources[0].Owner);
        Assert.AreEqual("pcl-lite", result.Sources[0].Repo);
    }

    [TestMethod]
    public void ParseShouldAcceptFullUrl()
    {
        var result = UpdateManifestV2.Parse("""
            { "sources": [ { "url": "https://github.com/askbbtsk/pcl-lite/releases/latest" } ] }
            """);

        Assert.AreEqual("askbbtsk", result.Sources[0].Owner);
        Assert.AreEqual("pcl-lite", result.Sources[0].Repo);
    }

    [TestMethod]
    public void ParseShouldAcceptWeightAsString()
    {
        var result = UpdateManifestV2.Parse("""
            { "sources": [ { "repo": "a/b", "weight": "88" } ] }
            """);

        Assert.AreEqual(88, result.Sources[0].Weight);
    }

    [TestMethod]
    public void ParseShouldTolerateBomAndNewlines()
    {
        var result = UpdateManifestV2.Parse("\uFEFF\n{\n  \"sources\": [\"a/b\"]\n}\n");
        Assert.AreEqual(1, result.Sources.Count);
    }

    [TestMethod]
    public void ParseShouldAcceptAlternativeListKeys()
    {
        foreach (var key in new[] { "repositories", "repos", "mirrors", "servers", "nodes" })
        {
            var result = UpdateManifestV2.Parse($"{{ \"{key}\": [\"a/b\"] }}");
            Assert.AreEqual(1, result.Sources.Count, $"键名 {key} 应该被认作源列表");
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  容错：一条坏条目不该拖垮整份清单
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseShouldSkipBadEntriesButKeepGoodOnes()
    {
        var result = UpdateManifestV2.Parse("""
            {
              "sources": [
                "askbbtsk/pcl-lite",
                "404: Not Found",
                { "note": "没有仓库地址" },
                "other/pcl-lite"
              ]
            }
            """);

        Assert.AreEqual(2, result.Sources.Count);
        Assert.IsTrue(result.Problems.Count >= 2, "被跳过的条目要留下痕迹");
    }

    [TestMethod]
    public void ParseShouldDeduplicateByRepository()
    {
        var result = UpdateManifestV2.Parse("""
            {
              "sources": [
                { "repo": "askbbtsk/pcl-lite", "weight": 10 },
                { "repo": "Askbbtsk/PCL-Lite", "weight": 90 }
              ]
            }
            """);

        Assert.AreEqual(1, result.Sources.Count);
        Assert.AreEqual(90, result.Sources[0].Weight, "重复的仓库取权重高的那个");
    }

    [TestMethod]
    public void ParseShouldIgnoreSchemaField()
    {
        // 顶层除了 sources 还有别的标量字段，不该被当成源
        var result = UpdateManifestV2.Parse("""
            { "schema": 2, "generatedAt": "2026-10-30", "sources": ["a/b"] }
            """);

        Assert.AreEqual(1, result.Sources.Count);
    }

    // ─────────────────────────────────────────────────────────────
    //  必须判定为不可用（上层据此回退 v1）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ParseShouldRejectEmptyBody()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestV2.Parse(""));
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestV2.Parse(null));
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestV2.Parse("   \r\n  "));
    }

    [TestMethod]
    public void ParseShouldRejectHtmlErrorPage()
    {
        // 被墙 / 反代挂了时的典型响应体
        var ex = Assert.ThrowsExactly<FormatException>(
            () => UpdateManifestV2.Parse("<!DOCTYPE html><html><body>404 Not Found</body></html>"));
        StringAssert.Contains(ex.Message, "网页");
    }

    [TestMethod]
    public void ParseShouldRejectBrokenJson()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestV2.Parse("{ \"sources\": [ "));
    }

    [TestMethod]
    public void ParseShouldRejectWhenNoValidSource()
    {
        var ex = Assert.ThrowsExactly<FormatException>(() => UpdateManifestV2.Parse("""
            { "sources": ["404: Not Found", "", "<html>"] }
            """));
        StringAssert.Contains(ex.Message, "没有一个可用的仓库地址");
    }

    [TestMethod]
    public void ParseShouldRejectUnknownTopLevel()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestV2.Parse("123"));
    }

    // ─────────────────────────────────────────────────────────────
    //  权重
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void OfficialTierShouldHaveHighestWeight()
    {
        var official = UpdateManifestV2.WeightOfTier(UpdateManifestV2.TierOfficial);

        Assert.IsTrue(official > UpdateManifestV2.WeightOfTier(UpdateManifestV2.TierMirror));
        Assert.IsTrue(official > UpdateManifestV2.WeightOfTier(UpdateManifestV2.TierBackup));
        Assert.IsTrue(official > UpdateManifestV2.WeightOfTier(UpdateManifestV2.TierCommunity));
        Assert.AreEqual(100, official);
    }

    [TestMethod]
    public void UnknownTierShouldFallBackToDefaultWeight()
    {
        Assert.AreEqual(UpdateManifestV2.DefaultWeight, UpdateManifestV2.WeightOfTier("whatever"));
        Assert.AreEqual(UpdateManifestV2.DefaultWeight, UpdateManifestV2.WeightOfTier(null));
    }

    [TestMethod]
    public void ParseShouldSortByWeightDescending()
    {
        var result = UpdateManifestV2.Parse("""
            {
              "sources": [
                { "repo": "low/x", "weight": 5 },
                { "repo": "askbbtsk/pcl-lite", "tier": "official" },
                { "repo": "mid/x", "weight": 60 }
              ]
            }
            """);

        Assert.AreEqual(100, result.Sources[0].Weight);
        Assert.AreEqual(60, result.Sources[1].Weight);
        Assert.AreEqual(5, result.Sources[2].Weight);
    }
}
