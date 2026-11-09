using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.ResourceProject;

namespace PCL.Core.Test.Minecraft.ResourceProject;

[TestClass]
public sealed class DependencyProjectMatcherTests
{
    [TestMethod]
    public void FindUniqueExactSlug_IsCaseInsensitiveAndIgnoresNearMatches()
    {
        var match = DependencyProjectMatcher.FindUniqueExactSlug(
            new List<DependencyProjectCandidate>
            {
                new("abc123", "Modrinth", "fabric-api"),
                new("different", "CurseForge", "fabric-api-extra"),
            }, "Fabric-Api");

        Assert.IsNotNull(match);
        Assert.AreEqual("abc123", match.Id);
        Assert.AreEqual("Modrinth", match.Source);
    }

    [TestMethod]
    public void FindUniqueExactSlug_RejectsAmbiguousCrossPlatformSlug()
    {
        var match = DependencyProjectMatcher.FindUniqueExactSlug(
            new List<DependencyProjectCandidate>
            {
                new("123", "CurseForge", "jei"),
                new("modrinth-id", "Modrinth", "jei"),
            }, "jei");

        Assert.IsNull(match);
    }

    [TestMethod]
    public void FindUniqueExactSlug_DeduplicatesRepeatedObservationOfSameProject()
    {
        var match = DependencyProjectMatcher.FindUniqueExactSlug(
            new List<DependencyProjectCandidate>
            {
                new("abc123", "Modrinth", "sodium"),
                new("ABC123", "modrinth", "Sodium"),
            }, "sodium");

        Assert.IsNotNull(match);
        Assert.AreEqual("abc123", match.Id);
    }
}
