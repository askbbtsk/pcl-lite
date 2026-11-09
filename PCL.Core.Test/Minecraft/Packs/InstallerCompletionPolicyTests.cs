using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Test.Minecraft.Packs;

[TestClass]
public sealed class InstallerCompletionPolicyTests
{
    [TestMethod]
    public void SuccessfulLogCannotOverrideFailedOrTerminatedInstallerProcess()
    {
        Assert.IsFalse(InstallerCompletionPolicy.IsSuccessful(1, ["true"]));
        Assert.IsFalse(InstallerCompletionPolicy.IsSuccessful(-1, ["true"]));
        Assert.IsFalse(InstallerCompletionPolicy.IsSuccessful(0, ["false", "still running"]));
        Assert.IsTrue(InstallerCompletionPolicy.IsSuccessful(0, ["true", "cleanup"]));
    }
}
