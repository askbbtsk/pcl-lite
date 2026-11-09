using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Enhancements;

namespace PCL.Core.Test.Minecraft.Enhancements;

[TestClass]
public sealed class InstanceEnhancementMaintenanceTests
{
    private string _root = null!;
    private string _instance = null!;
    private InstanceEnhancementStore Store => new(_instance);

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLEnhancementMaintenance", Guid.NewGuid().ToString("N"));
        _instance = Path.Combine(_root, "versions", "first");
        Directory.CreateDirectory(_instance);
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private InstanceResolvedReport SaveReport(string fingerprint = "report-one")
    {
        var report = new InstanceResolvedReport { Fingerprint = fingerprint };
        Assert.IsTrue(Store.TrySaveResolved(report, out var error), error);
        return report;
    }

    [TestMethod]
    public void CleanupDeletesOnlySupportedResolvedReportAndRetainsAllOtherSidecars()
    {
        Store.SaveDeclaration(new() { InstanceId = Guid.NewGuid() });
        SaveReport();
        var paths = new[] { Store.DeclarationPath, Path.Combine(Store.DirectoryPath, "PersonalOptions", "memory.json"),
            Path.Combine(Store.DirectoryPath, "PackTransactions", "tx-user", "old-00000000.bin"),
            Path.Combine(_root, "ResourceCollections", "collection.json") };
        foreach (var path in paths.Skip(1))
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "user data"); }
        var original = paths.Select(File.ReadAllBytes).ToArray();
        var preview = Store.ReadResolved();
        Assert.IsTrue(Store.ClearResolved(preview.ContentSha256!));
        Assert.IsFalse(File.Exists(Store.ResolvedPath));
        for (var index = 0; index < paths.Length; index++) CollectionAssert.AreEqual(original[index], File.ReadAllBytes(paths[index]));
        Assert.IsFalse(Store.ClearResolved(preview.ContentSha256!));
    }

    [TestMethod]
    public void CleanupMissingDirectoryDoesNotCreateOne()
    {
        Assert.IsFalse(Store.ClearResolved("unused"));
        Assert.IsFalse(Directory.Exists(Path.Combine(_instance, "PCL")));
    }

    [TestMethod]
    public void CleanupRejectsStalePreviewUnknownSchemaAndCorruptCache()
    {
        SaveReport();
        var preview = Store.ReadResolved();
        SaveReport("report-two");
        Assert.ThrowsExactly<IOException>(() => Store.ClearResolved(preview.ContentSha256!));
        Assert.AreEqual("report-two", Store.ReadResolved().Value!.Fingerprint);
        foreach (var content in new[] { "{\"schemaVersion\":77}", "corrupt" })
        {
            File.WriteAllText(Store.ResolvedPath, content);
            Assert.ThrowsExactly<IOException>(() => Store.ClearResolved(preview.ContentSha256!));
            Assert.AreEqual(content, File.ReadAllText(Store.ResolvedPath));
        }
    }

    [TestMethod]
    public void ReaderWriterAndCleanupUseTheSameCoordinationFile()
    {
        var report = SaveReport();
        var hash = Store.ReadResolved().ContentSha256!;
        using (var busy = new FileStream(Path.Combine(Store.DirectoryPath, ".write.lock"), FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
        {
            Assert.AreEqual(EnhancementReadStatus.Unavailable, Store.ReadResolved().Status);
            Assert.IsFalse(Store.TrySaveResolved(report, out _));
            Assert.ThrowsExactly<IOException>(() => Store.ClearResolved(hash));
        }
        Assert.IsTrue(Store.ClearResolved(hash));
    }

    [TestMethod]
    public void CancelledCleanupPreservesTheReport()
    {
        SaveReport();
        var hash = Store.ReadResolved().ContentSha256!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => Store.ClearResolved(hash, cancellation.Token));
        Assert.IsTrue(File.Exists(Store.ResolvedPath));
    }

    [TestMethod]
    public void DuplicateIdentityReportNeverPicksAnOriginalOrChangesRecords()
    {
        var identity = Guid.NewGuid();
        Store.SaveDeclaration(new() { InstanceId = identity });
        var other = Path.Combine(_root, "versions", "second-long-name"); Directory.CreateDirectory(other);
        var sibling = new InstanceEnhancementStore(other); sibling.SaveDeclaration(new() { InstanceId = identity });
        var original = File.ReadAllBytes(Store.DeclarationPath);
        var otherOriginal = File.ReadAllBytes(sibling.DeclarationPath);
        var report = InstanceIdentityInspector.InspectSiblings(_instance);
        Assert.AreEqual(identity, report.InstanceId);
        CollectionAssert.AreEqual(new[] { other }, report.DuplicateInstances.ToArray());
        CollectionAssert.AreEqual(new[] { _instance }, InstanceIdentityInspector.InspectSiblings(other).DuplicateInstances.ToArray());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(Store.DeclarationPath));
        CollectionAssert.AreEqual(otherOriginal, File.ReadAllBytes(sibling.DeclarationPath));
    }

    [TestMethod]
    public void IdentityScanPreservesUnknownSchemasAndDoesNotCreateMissingSidecars()
    {
        Store.SaveDeclaration(new() { InstanceId = Guid.NewGuid() });
        var missing = Path.Combine(_root, "versions", "no-sidecar"); Directory.CreateDirectory(missing);
        var newer = new InstanceEnhancementStore(Path.Combine(_root, "versions", "newer"));
        Directory.CreateDirectory(newer.DirectoryPath);
        const string bytes = "{\"schemaVersion\":88,\"custom\":true}";
        File.WriteAllText(newer.DeclarationPath, bytes, new UTF8Encoding(false));
        var report = InstanceIdentityInspector.InspectSiblings(_instance);
        Assert.AreEqual(0, report.DuplicateInstances.Count);
        Assert.IsTrue(report.Issues.Any(issue => issue.Status == EnhancementReadStatus.UnsupportedSchema));
        Assert.AreEqual(bytes, File.ReadAllText(newer.DeclarationPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(missing, "PCL")));
    }

    [TestMethod]
    public void DirectoryLinkIsReportedWithoutReadingItsTarget()
    {
        Store.SaveDeclaration(new() { InstanceId = Guid.NewGuid() });
        var target = Path.Combine(_root, "outside"); Directory.CreateDirectory(target);
        var linked = Path.Combine(_root, "versions", "link");
        try { Directory.CreateSymbolicLink(linked, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { Assert.Inconclusive("Creating a directory link is unavailable in this environment."); return; }
        try
        {
            var report = InstanceIdentityInspector.InspectSiblings(_instance);
            Assert.IsTrue(report.Issues.Any(issue => issue.InstanceDirectory == linked));
            Assert.IsFalse(Directory.Exists(Path.Combine(target, "PCL")));
        }
        finally { Directory.Delete(linked); }
    }
}
