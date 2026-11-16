using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test.App.Lunar;

[TestClass]
public class LunarInstallationIdentityTests
{
    private string _root = null!;
    private string Official => Path.Combine(_root, "official");
    private string Assets => Path.Combine(_root, "assets");
    private static string IdFile(string root) => Path.Combine(root, "launcher-cache", "installation-id");

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_root =
        Path.Combine(Path.GetTempPath(), "pcl-lunar-identity-" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private static void WriteId(string root, string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IdFile(root))!);
        File.WriteAllText(IdFile(root), id);
    }

    [TestMethod]
    public void OfficialIdentifierWinsWithoutChangingEitherExistingFile()
    {
        string expected = Guid.NewGuid().ToString().ToUpperInvariant();
        string custom = Guid.NewGuid().ToString();
        WriteId(Official, expected);
        WriteId(Assets, custom);
        Assert.AreEqual(expected, LunarInstallationIdentity.ReadOrCreate(Official, Assets));
        Assert.AreEqual(expected, File.ReadAllText(IdFile(Official)));
        Assert.AreEqual(custom, File.ReadAllText(IdFile(Assets)));
    }

    [TestMethod]
    public void ExistingCustomIdentitySeedsTheMissingSharedFile()
    {
        string expected = Guid.NewGuid().ToString();
        WriteId(Assets, expected);
        Assert.AreEqual(expected, LunarInstallationIdentity.ReadOrCreate(Official, Assets));
        Assert.AreEqual(expected, File.ReadAllText(IdFile(Official)));
        Assert.AreEqual(expected, File.ReadAllText(IdFile(Assets)));
    }

    [TestMethod]
    public void NewIdentityIsPersistedAndReused()
    {
        string first = LunarInstallationIdentity.ReadOrCreate(Official, Assets);
        Assert.IsTrue(Guid.TryParseExact(first, "D", out _));
        Assert.AreEqual(first, LunarInstallationIdentity.ReadOrCreate(Official, Assets));
        Assert.IsFalse(File.Exists(IdFile(Assets)));
    }

    [TestMethod]
    public void ConcurrentCreationConvergesWithoutPartialOrTemporaryFiles()
    {
        var values = new string[12];
        Parallel.For(0, values.Length, i =>
            values[i] = LunarInstallationIdentity.ReadOrCreate(Official, Assets));
        Assert.AreEqual(1, values.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(values[0], File.ReadAllText(IdFile(Official)));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(IdFile(Official))!).Length);
    }

    [TestMethod]
    public void InvalidOfficialIdentityIsNotReplacedUnderARunningLauncher()
    {
        const string invalid = "user-controlled-invalid-value";
        WriteId(Official, invalid);
        WriteId(Assets, Guid.NewGuid().ToString());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            LunarInstallationIdentity.ReadOrCreate(Official, Assets));
        Assert.AreEqual(invalid, File.ReadAllText(IdFile(Official)));
    }

    [TestMethod]
    public void OfficialEnvironmentOverrideUsesTheOfficialPathRules()
    {
        Assert.AreEqual(Path.GetFullPath(Official),
            LunarInstallationIdentity.ResolveOfficialRoot(Official, null, _root));
        Assert.AreEqual(Path.Combine(_root, "relative-lunar"),
            LunarInstallationIdentity.ResolveOfficialRoot(Official, "relative-lunar", _root));
        Assert.AreEqual(Path.GetFullPath(Assets),
            LunarInstallationIdentity.ResolveOfficialRoot(Official, Assets, _root));
    }

    [TestMethod]
    public void LaterCallsReadTheCurrentOfficialIdentityInsteadOfACachedValue()
    {
        string first = LunarInstallationIdentity.ReadOrCreate(Official, Assets);
        string changed = Guid.NewGuid().ToString();
        WriteId(Official, changed);
        Assert.AreNotEqual(first, changed);
        Assert.AreEqual(changed, LunarInstallationIdentity.ReadOrCreate(Official, Assets));
    }

    [TestMethod]
    public void UnwritableSharedLocationDoesNotReturnAnEphemeralIdentity()
    {
        Directory.CreateDirectory(Official);
        File.WriteAllText(Path.Combine(Official, "launcher-cache"), "not a directory");
        Assert.Throws<IOException>(() => LunarInstallationIdentity.ReadOrCreate(Official, Assets));
        Assert.AreEqual("not a directory", File.ReadAllText(Path.Combine(Official, "launcher-cache")));
    }

    [TestMethod]
    public void InvalidAssetIdentityDoesNotContaminateTheNewSharedIdentity()
    {
        WriteId(Assets, "invalid");
        string identity = LunarInstallationIdentity.ReadOrCreate(Official, Assets);
        Assert.IsTrue(Guid.TryParseExact(identity, "D", out _));
        Assert.AreEqual(identity, File.ReadAllText(IdFile(Official)));
        Assert.AreEqual("invalid", File.ReadAllText(IdFile(Assets)));
    }

    [TestMethod]
    public void SameRootCanInitializeAndReuseItsIdentity()
    {
        string first = LunarInstallationIdentity.ReadOrCreate(Official, Official);
        Assert.AreEqual(first, LunarInstallationIdentity.ReadOrCreate(Official, Official));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("12")]
    [DataRow("00000000000000000000000000000000")]
    public void MalformedOfficialFileIsPreservedAndRejected(string invalid)
    {
        WriteId(Official, invalid);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            LunarInstallationIdentity.ReadOrCreate(Official, Assets));
        Assert.AreEqual(invalid, File.ReadAllText(IdFile(Official)));
    }
}
