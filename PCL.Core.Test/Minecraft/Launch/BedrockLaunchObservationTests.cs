using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Bedrock;
using PCL.Core.Minecraft.Launch;

namespace PCL.Core.Test.Minecraft.Launch;

[TestClass]
public sealed class BedrockLaunchObservationTests
{
    [TestMethod]
    public void UwpNewAliveGameCountsWithoutRequiringAGameOwnedWindow()
    {
        // UWP can expose HWND 0 because ApplicationFrameHost owns the visible window.
        // Window handles are intentionally not an input to the success policy.
        Assert.IsTrue(BedrockLaunchObservation.CanCount(new HashSet<int> { 100 }, 200,
            "Minecraft.Windows", hasExited: false));
    }

    [TestMethod]
    public void ShellAndFrameHostProcessesNeverProveGameActivation()
    {
        foreach (var name in new[] { "explorer", "ApplicationFrameHost", "RuntimeBroker", "cmd" })
            Assert.IsFalse(BedrockLaunchObservation.CanCount(new HashSet<int>(), 200,
                name, hasExited: false, activatedProcessId: 200));
    }

    [TestMethod]
    public void ExistingOrExitedGamesDoNotCountAsSuccessfulNewLaunches()
    {
        var previous = new HashSet<int> { 200 };
        Assert.IsFalse(BedrockLaunchObservation.CanCount(previous, 200, "Minecraft.Windows", false));
        Assert.IsFalse(BedrockLaunchObservation.CanCount(previous, 300, "Minecraft.Windows", true));
        Assert.IsFalse(BedrockLaunchObservation.CanCount(previous, 200, "Minecraft.Windows", false, 200));
    }

    [TestMethod]
    public void DirectGdkPidDoesNotAttributeAnotherConcurrentGameToThisLaunch()
    {
        var previous = new HashSet<int>();
        Assert.IsTrue(BedrockLaunchObservation.CanCount(previous, 200, "Minecraft.Windows", false, 200));
        Assert.IsFalse(BedrockLaunchObservation.CanCount(previous, 300, "Minecraft.Windows", false, 200));
    }

    [TestMethod]
    public void FailedSnapshotOnlyAllowsAnActualKnownGamePid()
    {
        Assert.IsFalse(BedrockLaunchObservation.CanCount(null, 200, "Minecraft.Windows", false));
        Assert.IsTrue(BedrockLaunchObservation.CanCount(null, 200, "Minecraft.Windows", false, 200));
        Assert.IsFalse(BedrockLaunchObservation.CanCount(null, 200, "Minecraft.Windows", false, 300));
        Assert.IsFalse(BedrockLaunchObservation.CanCount(null, 200, "explorer", false, 200));
    }

    [TestMethod]
    public void EditorModeAndLegacyBedrockUseTheSameSupportedGameProcesses()
    {
        // Editor is a creator-protocol mode of Minecraft.Windows, not a separate Editor.exe.
        foreach (var name in new[] { "Minecraft.Windows", "minecraft.windows", "Minecraft.Win10.DX11" })
            Assert.IsTrue(BedrockLaunchObservation.CanCount(new HashSet<int>(), 200, name, false));
    }

    [TestMethod]
    public void RepeatedUwpAndDirectObservationsShareTheSameSuccessIdentity()
    {
        var count = 17;
        var counter = new GameLaunchCounter(() => count, value => count = value);
        var previous = new HashSet<int>();
        if (BedrockLaunchObservation.CanCount(previous, 200, "Minecraft.Windows", false))
            Assert.IsTrue(counter.RecordSuccess("200:638998000000000000"));
        if (BedrockLaunchObservation.CanCount(previous, 200, "Minecraft.Windows", false, 200))
            Assert.IsFalse(counter.RecordSuccess("200:638998000000000000"));
        Assert.AreEqual(18, count);
    }

    [TestMethod]
    public void InvalidProcessIdsCannotBecomeSuccessEvidence()
    {
        foreach (var id in new[] { -1, 0 })
            Assert.IsFalse(BedrockLaunchObservation.CanCount(new HashSet<int>(), id, "Minecraft.Windows", false, id));
    }
}
