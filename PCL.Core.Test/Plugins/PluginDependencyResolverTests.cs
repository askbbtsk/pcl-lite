using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;

namespace PCL.Core.Test.Plugins;

[TestClass]
public sealed class PluginDependencyResolverTests
{
    [TestMethod]
    public void VersionRangesSupportExactComparatorsCaretTildeAndPrerelease()
    {
        Assert.IsTrue(PluginVersionRange.Satisfies("1.4.2", "^1.2.0"));
        Assert.IsFalse(PluginVersionRange.Satisfies("2.0.0", "^1.2.0"));
        Assert.IsTrue(PluginVersionRange.Satisfies("0.2.9", "^0.2.3"));
        Assert.IsFalse(PluginVersionRange.Satisfies("0.3.0", "^0.2.3"));
        Assert.IsTrue(PluginVersionRange.Satisfies("1.2.9", "~1.2.3"));
        Assert.IsFalse(PluginVersionRange.Satisfies("1.3.0", "~1.2.3"));
        Assert.IsTrue(PluginVersionRange.Satisfies("1.9.0-beta.2", ">=1.9.0-beta.1 <2.0.0"));
        Assert.IsTrue(PluginVersionRange.Satisfies("1.2.3+build.2", "1.2.3+build.1"));
        Assert.IsFalse(PluginVersionRange.IsValid("^999999999999.0.0"));
        Assert.IsFalse(PluginVersionRange.IsValid("1.02.3"));
        Assert.IsFalse(PluginVersionRange.IsValid(""));
    }

    [TestMethod]
    public void ResolverOrdersDependenciesAndBlocksMissingDisabledMismatchAndCycleChains()
    {
        var consumer = Candidate("alpha.consumer", "1.0.0", true,
            ("zeta.provider", "^1.0.0"));
        var provider = Candidate("zeta.provider", "1.2.0", true);
        var resolution = PluginDependencyResolver.Resolve([consumer, provider]);
        CollectionAssert.AreEqual(new[] { "zeta.provider", "alpha.consumer" },
            resolution.LoadOrder.Select(item => item.Manifest.Id).ToArray());
        Assert.AreEqual(0, resolution.Blocked.Count);

        var missing = PluginDependencyResolver.Resolve([consumer]);
        StringAssert.Contains(missing.Blocked["alpha.consumer"], "missing");

        var disabledProvider = Candidate("zeta.provider", "1.2.0", false);
        var disabled = PluginDependencyResolver.Resolve([consumer, disabledProvider]);
        StringAssert.Contains(disabled.Blocked["alpha.consumer"], "disabled");

        var mismatch = PluginDependencyResolver.Resolve([consumer, Candidate("zeta.provider", "2.0.0", true)]);
        StringAssert.Contains(mismatch.Blocked["alpha.consumer"], "version mismatch");

        var cycleA = Candidate("cycle.a", "1.0.0", true, ("cycle.b", "*"));
        var cycleB = Candidate("cycle.b", "1.0.0", true, ("cycle.a", "*"));
        var cycleChild = Candidate("cycle.child", "1.0.0", true, ("cycle.a", "*"));
        var cycle = PluginDependencyResolver.Resolve([cycleA, cycleB, cycleChild]);
        Assert.AreEqual(0, cycle.LoadOrder.Count);
        StringAssert.Contains(cycle.Blocked["cycle.a"], "cycle");
        StringAssert.Contains(cycle.Blocked["cycle.b"], "cycle");
        StringAssert.Contains(cycle.Blocked["cycle.child"], "could not be loaded");
    }

    private static PluginDependencyCandidate Candidate(string id, string version, bool enabled,
        params (string Id, string Range)[] dependencies)
    {
        var manifest = new PluginManifest
        {
            Id = id,
            Name = id,
            Version = version,
            Author = "Test",
            EntryAssembly = "Plugin.dll",
            Dependencies = dependencies.ToDictionary(item => item.Id, item => item.Range)
        };
        return new(manifest, id, enabled);
    }
}
