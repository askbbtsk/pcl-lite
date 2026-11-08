using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Test.Minecraft.Packs;

[TestClass]
public sealed class LauncherBundleResolverTest
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLLauncherBundleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void SelectBundledLauncher_PrefersPclLiteOverUpstreamPclAndThirdParty()
    {
        var upstream = Path.Combine(_root, "Plain Craft Launcher.exe");
        var other = Path.Combine(_root, "OtherLauncher.exe");
        var lite = Path.Combine(_root, "PCL Lite.exe");
        var selected = LauncherBundleResolver.SelectBundledLauncher(
        [
            new(upstream, "Plain Craft Launcher"),
            new(other, "Other Launcher"),
            new(lite, "PCL Lite"),
        ]);
        Assert.AreEqual(lite, selected);
    }

    [TestMethod]
    public void CopyCurrentExecutable_RequiresLiteProductIdentityAndCopiesExactBytes()
    {
        var source = Path.Combine(_root, "PCL Lite.exe");
        var target = Path.Combine(_root, "out");
        var bytes = Enumerable.Range(0, 1024).Select(static i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(source, bytes);

        var output = LauncherBundleResolver.CopyCurrentExecutable(source, target, _ => "PCL Lite");

        Assert.AreEqual(Path.Combine(target, LauncherBundleResolver.BundledFileName), output);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(output));
    }

    [TestMethod]
    public void CopyCurrentExecutable_RejectsUpstreamPclBinary()
    {
        var source = Path.Combine(_root, "PCL.exe");
        File.WriteAllBytes(source, [1, 2, 3]);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            LauncherBundleResolver.CopyCurrentExecutable(source, Path.Combine(_root, "out"), _ => "Plain Craft Launcher"));
    }
}
