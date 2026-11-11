using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Navigation;

namespace PCL.Core.Test.UI.Navigation;

[TestClass]
public class CommandPaletteSearchTests
{
    [TestMethod]
    public void TitleMatchesOutrankMetadataAndPreserveTies()
    {
        CommandPaletteEntry[] entries =
        [
            new("metadata", "Other", "", "", "fabric"),
            new("substring", "My Fabric pack", "", ""),
            new("prefix", "Fabric API", "", ""),
            new("exact", "Fabric", "", ""),
            new("tie", "Fabric", "", "")
        ];
        CollectionAssert.AreEqual(new[] { "exact", "tie", "prefix", "substring", "metadata" },
            CommandPaletteSearch.Search(entries, "FABRIC").Select(entry => entry.Id).ToArray());
    }

    [TestMethod]
    public void EveryWordMustMatchButMayMatchDifferentFields()
    {
        CommandPaletteEntry[] entries =
        [
            new("sodium", "Sodium", "sodium-fabric.jar.disabled", "已安装模组", "渲染"),
            new("iris", "Iris", "iris-fabric.jar", "已安装模组")
        ];
        Assert.AreEqual("sodium", CommandPaletteSearch.Search(entries, "  sodium\tDISABLED\n模组  ").Single().Id);
        Assert.AreEqual(0, CommandPaletteSearch.Search(entries, "sodium forge").Count);
        Assert.AreEqual("sodium", CommandPaletteSearch.Search(entries, "渲染").Single().Id);
    }

    [TestMethod]
    public void EmptyQueriesKeepCatalogOrderAndLimitResults()
    {
        var entries = Enumerable.Range(0, 1000).Select(index => new CommandPaletteEntry(index.ToString(), "Same", "", "")).ToArray();
        var matches = CommandPaletteSearch.Search(entries, " \t");
        Assert.AreEqual(60, matches.Count);
        Assert.AreEqual("0", matches[0].Id);
        Assert.AreEqual("59", matches[^1].Id);
        Assert.AreEqual(0, CommandPaletteSearch.Search(entries, null, 0).Count);
        Assert.AreEqual(0, CommandPaletteSearch.Search([], "missing").Count);
    }

    [TestMethod]
    public void SameTitlesRemainSeparateCommands()
    {
        CommandPaletteEntry[] entries = [new("instance:a", "Pack", "", ""), new("instance:b", "Pack", "", "")];
        Assert.AreEqual(2, CommandPaletteSearch.Search(entries, "pack").Count);
    }
}
