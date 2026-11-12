using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;

namespace PCL.Core.Test;

[TestClass]
public class UpdateExecutableAssetSelectorTest
{
    [TestMethod]
    public void ZipAndSignatureNeverWinOverRequestedExecutable()
    {
        string[] urls = [
            "https://github.test/x64/launcher-x64.zip",
            "https://github.test/x64/launcher-arm64.exe",
            "https://github.test/x64/launcher-x64.exe.sigstore.json",
            "https://github.test/x64/launcher-x64.exe"
        ];
        Assert.AreEqual(urls[3], UpdateExecutableAssetSelector.Pick(urls, false));
        Assert.AreEqual(urls[1], UpdateExecutableAssetSelector.Pick(urls, true));
    }

    [TestMethod]
    public void RejectsExplicitlyDifferentArchitectureButAcceptsLegacyUnlabelledExecutable()
    {
        Assert.IsNull(UpdateExecutableAssetSelector.Pick(["https://host.test/PCL-x64.exe"], true));
        Assert.IsNull(UpdateExecutableAssetSelector.Pick(["https://host.test/PCL-arm64.exe"], false));
        Assert.IsNull(UpdateExecutableAssetSelector.Pick(["https://host.test/PCL-x64.zip"], false));
        Assert.AreEqual("https://host.test/PCL.exe",
            UpdateExecutableAssetSelector.Pick(["https://host.test/PCL.exe"], true));
    }
}
