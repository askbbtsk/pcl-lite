using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.App.Updates;

namespace PCL.Core.Test;

[TestClass]
public class UpdateManifestPlanTest
{
    private const string V1 = """{"update":"askbbtsk/pcl-lite/releases/latest"}""";
    private const string V2 = """
        {"schema":2,"sources":[
            {"repo":"askbbtsk/pcl-lite","tier":"official"},
            {"repo":"backup/pcl-lite","tier":"mirror"}]}
        """;

    [TestMethod]
    public void ValidV2LoadsAllSourcesAndKeepsWeightOrder()
    {
        var plan = UpdateManifestPlanner.Build(V2);
        Assert.IsTrue(plan.UsedV2);
        Assert.IsNull(plan.V2Failure);
        Assert.AreEqual(2, plan.Sources.Count);
        Assert.AreEqual("askbbtsk", plan.Sources[0].Owner);
        Assert.AreEqual("pcl-lite", plan.Sources[0].Repo);
        Assert.IsTrue(plan.Sources[0].Weight > plan.Sources[1].Weight);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("<html>504 Gateway Timeout</html>")]
    [DataRow("{\"sources\":[")]
    [DataRow("{\"sources\":[]}")]
    [DataRow("{\"sources\":[\"404: Not Found\"]}")]
    [DataRow("{\"update\":\"askbbtsk/pcl-lite/releases/latest\"}")]
    [DataRow("\"askbbtsk/pcl-lite/releases/latest\"")]
    [DataRow("askbbtsk/pcl-lite/releases/latest")]
    public void InvalidV2CannotRecoverFromLegacyBody(string? body)
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPlanner.Build(body));
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPlanner.Build(body, V1));
    }

    [TestMethod]
    public void LegacyArgumentIsIgnoredEvenWhenValidV2IsAvailable()
    {
        var plan = UpdateManifestPlanner.Build(V2, """{"update":"unrelated/legacy"}""");
        Assert.AreEqual(2, plan.Sources.Count);
        Assert.IsFalse(plan.Sources.Any(source => source.Owner == "unrelated"));
    }

    [TestMethod]
    public void MixedV2EntriesKeepGoodSourcesAndReportSkippedEntries()
    {
        var plan = UpdateManifestPlanner.Build("""{"sources":["askbbtsk/pcl-lite","404: Not Found"]}""");
        Assert.AreEqual(1, plan.Sources.Count);
        Assert.AreEqual(1, plan.Problems.Count);
    }

    [TestMethod]
    public void FetchFailureRequestsV2OnlyAndPreservesTheOriginalError()
    {
        var requested = new List<string>();
        var failure = new TimeoutException("v2 request timed out");
        var caught = Assert.ThrowsExactly<TimeoutException>(() => UpdateManifestPlanner.Load(file =>
        {
            requested.Add(file);
            if (file == "update.json") return V1;
            throw failure;
        }));
        Assert.AreSame(failure, caught);
        CollectionAssert.AreEqual(new[] { "updatev2.json" }, requested);
    }

    [TestMethod]
    public void ParseFailureDoesNotIssueASecondLegacyRequest()
    {
        foreach (var body in new[] { "<html/>", "{}", V1 })
        {
            var requested = new List<string>();
            Assert.ThrowsExactly<FormatException>(() => UpdateManifestPlanner.Load(file =>
            {
                requested.Add(file);
                return file == "update.json" ? V1 : body;
            }));
            CollectionAssert.AreEqual(new[] { "updatev2.json" }, requested);
        }
    }

    [TestMethod]
    public void SuccessfulFetchRequestsOnlyTheV2Manifest()
    {
        var requested = new List<string>();
        var plan = UpdateManifestPlanner.Load(file => { requested.Add(file); return V2; });
        Assert.AreEqual(2, plan.Sources.Count);
        CollectionAssert.AreEqual(new[] { "updatev2.json" }, requested);
    }

    [TestMethod]
    public void ServiceUrlsAndDefaultChannelBuildersUseV2WhileRepositoryIdentityRemainsSeparate()
    {
        StringAssert.EndsWith(ProjectLinks.UpdateManifest, "/updatev2.json");
        StringAssert.EndsWith(ProjectLinks.UpdateManifestMirror, "/updatev2.json");
        StringAssert.EndsWith(ProjectLinks.UpdateManifestApi, "/updatev2.json");
        StringAssert.EndsWith(ProjectLinks.ManifestRaw("a", "b"), "/updatev2.json");
        StringAssert.EndsWith(ProjectLinks.ManifestMirror("a", "b"), "/updatev2.json");
        StringAssert.EndsWith(ProjectLinks.ManifestApi("a", "b"), "/updatev2.json");
        StringAssert.EndsWith(ProjectLinks.ManifestRaw("a", "b", ProjectLinks.RepositoryIdentityFileName), "/update.json");
        Assert.AreEqual("askbbtsk/pcl-lite/releases/latest", UpdateManifestPointer.Extract(V1));
    }
}
