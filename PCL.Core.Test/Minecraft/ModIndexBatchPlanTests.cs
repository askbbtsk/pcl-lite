using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Resources;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class ModIndexBatchPlanTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "PCLIndexBatchTests");
    private static ModIndexTarget Target(string instance, string folder, string context = "Fabric|1.21|False")
        => new(Path.Combine(Root, instance), Path.Combine(Root, folder), context);

    [TestMethod]
    public void AllInstancesRemainInPlanRegardlessOfWhichOneWasSelected()
    {
        var instances = Enumerable.Range(0, 100)
            .Select(index => Target("versions/" + index, "versions/" + index + "/mods")).ToArray();
        var groups = ModIndexBatchPlan.Group(instances);
        Assert.AreEqual(100, groups.Count);
        CollectionAssert.AreEquivalent(instances, groups.SelectMany(group => group).ToArray());
    }

    [TestMethod]
    public void SharedDirectoryWithSameParserContextScansOnceButKeepsEveryCacheTarget()
    {
        var instances = Enumerable.Range(0, 50).Select(index => Target("versions/" + index, "mods")).ToArray();
        var groups = ModIndexBatchPlan.Group(instances);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(50, groups[0].Count);
    }

    [TestMethod]
    public void DifferentMinecraftLoaderAndLegacyDirectoryRulesNeverShareAParsedTree()
    {
        var contexts = new[] { "Fabric|1.21|False", "Forge|1.21|False", "NeoForge|1.21|False",
            "Fabric|1.20|False", "Forge|1.12.2|True", "Forge|1.12.2|False" };
        var groups = ModIndexBatchPlan.Group(contexts.Select((context, index) =>
            Target("versions/" + index, "mods", context)));
        Assert.AreEqual(contexts.Length, groups.Count);
    }

    [TestMethod]
    public void CaseAndRelativeSegmentsDeduplicateBothInstanceAndSharedDirectory()
    {
        var first = Target("versions/one", "mods");
        var duplicate = first with { InstancePath = Path.Combine(first.InstancePath, "child", "..").ToUpperInvariant() };
        var second = Target("versions/two", "mods") with
        {
            ModsDirectory = Path.Combine(Root, "mods", "child", "..").ToUpperInvariant() + Path.DirectorySeparatorChar
        };
        var groups = ModIndexBatchPlan.Group([first, duplicate, second]);
        Assert.AreEqual(1, groups.Count);
        CollectionAssert.AreEqual(new[] { first, second }, groups[0].ToArray());
    }

    [TestMethod]
    public void SameVersionNamesInDifferentMinecraftRootsRemainIndependent()
    {
        var groups = ModIndexBatchPlan.Group([
            Target("first/versions/1.21", "first/mods"),
            Target("second/versions/1.21", "second/mods"),
            Target("first/versions/1.21-custom", "first/versions/1.21-custom/mods")
        ]);
        Assert.AreEqual(3, groups.Count);
    }

    [TestMethod]
    public void EmptyPlanIsValidAndDoesNotInventAnInstance()
        => Assert.AreEqual(0, ModIndexBatchPlan.Group([]).Count);

    [TestMethod]
    public void PlanSnapshotsInputsSoCallerMutationCannotChangeAnActiveBatch()
    {
        var source = new[] { Target("one", "mods"), Target("two", "mods") };
        var expected = source.ToArray();
        var groups = ModIndexBatchPlan.Group(source);
        source[0] = Target("three", "elsewhere");
        CollectionAssert.AreEqual(expected, groups.Single().ToArray());
    }

    [TestMethod]
    public void InvalidInputsFailBeforeReturningAPartialPlan()
    {
        Assert.Throws<ArgumentNullException>(() => ModIndexBatchPlan.Group(null!));
        Assert.Throws<ArgumentNullException>(() => ModIndexBatchPlan.Group([null!]));
        Assert.Throws<ArgumentException>(() => ModIndexBatchPlan.Group([Target("one", "mods") with { InstancePath = "" }]));
    }
}
