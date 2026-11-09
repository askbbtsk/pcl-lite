using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.ResourceProject;

namespace PCL.Core.Test.Minecraft.ResourceProject;

[TestClass]
public sealed class ModLoaderCompatibilityTests
{
    [TestMethod]
    public void UniversalBuild_UsesAllDeclarationsAndIgnoresCase()
    {
        Assert.AreEqual(ModLoaderCompatibilityResult.DeclaredCompatible,
            ModLoaderCompatibility.Evaluate(["Fabric", "NeoForge"], ["neoforge"]));
    }

    [TestMethod]
    public void QuiltAcceptsFabric_ButFabricDoesNotAssumeQuiltCompatibility()
    {
        Assert.AreEqual(ModLoaderCompatibilityResult.DeclaredCompatible,
            ModLoaderCompatibility.Evaluate(["Fabric"], ["Quilt"]));
        Assert.AreEqual(ModLoaderCompatibilityResult.DifferentLoader,
            ModLoaderCompatibility.Evaluate(["Quilt"], ["Fabric"]));
    }

    [TestMethod]
    public void BridgeAndUnknownMetadata_DoNotBecomeErrors()
    {
        Assert.AreEqual(ModLoaderCompatibilityResult.Unknown,
            ModLoaderCompatibility.Evaluate(["Fabric"], ["NeoForge"], hasCompatibilityBridge: true));
        Assert.AreEqual(ModLoaderCompatibilityResult.Unknown,
            ModLoaderCompatibility.Evaluate([], ["Fabric"]));
        Assert.AreEqual(ModLoaderCompatibilityResult.Unknown,
            ModLoaderCompatibility.Evaluate(["Fabric", "CustomBridge"], ["Forge"]));
        Assert.AreEqual(ModLoaderCompatibilityResult.Unknown,
            ModLoaderCompatibility.Evaluate(["Forge"], []));
    }

    [TestMethod]
    public void ForgeAndNeoForge_RequireVersionEvidenceBeforeRejecting()
    {
        Assert.AreEqual(ModLoaderCompatibilityResult.Unknown,
            ModLoaderCompatibility.Evaluate(["Forge"], ["NeoForge"]));
        Assert.AreEqual(ModLoaderCompatibilityResult.Unknown,
            ModLoaderCompatibility.Evaluate(["NeoForge"], ["Forge"]));
        Assert.AreEqual(ModLoaderCompatibilityResult.DifferentLoader,
            ModLoaderCompatibility.Evaluate(["Forge"], ["Fabric"]));
    }
}
