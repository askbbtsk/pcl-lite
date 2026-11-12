using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.ResourceCollections;

namespace PCL.Core.Test.Minecraft.ResourceCollections;

[TestClass]
public sealed class ResourceCollectionTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLResourceCollections", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [TestMethod]
    public void IndexingKeepsAliasesMissingAndUnsupportedWithoutCreatingDataDirectories()
    {
        var source = CreateArchive("source/mod.jar.disabled", "mod");
        var missing = Path.Combine(_root, "missing~1.jar");
        var unsupported = Path.Combine(_root, "notes.txt");
        File.WriteAllText(unsupported, "notes");
        var document = ResourceCollectionIndexer.Capture("Selected mods", [source, source.ToUpperInvariant(), missing, unsupported]);
        Assert.AreEqual(3, document.Entries.Count);
        Assert.AreEqual(ResourceAvailability.Available, document.Entries[0].Availability);
        Assert.AreEqual(2, document.Entries[0].OriginalPaths.Count);
        Assert.AreEqual(1, document.Notices.Count);
        Assert.AreEqual(ResourceAvailability.Missing, document.Entries[1].Availability);
        Assert.IsTrue(document.Entries[1].ShortNameUnresolved);
        Assert.AreEqual(ResourceAvailability.Unsupported, document.Entries[2].Availability);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "ResourceCollections")));
        Assert.AreEqual(source, document.Entries[0].OriginalPaths[0]);
    }

    [TestMethod]
    public void RefreshRetainsMissingRecordsAndCollectionIdentity()
    {
        var source = CreateArchive("source/mod.jar", "mod");
        var captured = ResourceCollectionIndexer.Capture("One", [source]);
        File.Delete(source);
        var refreshed = ResourceCollectionIndexer.Refresh(captured);
        Assert.AreEqual(captured.Id, refreshed.Id);
        Assert.AreEqual(1, refreshed.Entries.Count);
        Assert.AreEqual(ResourceAvailability.Missing, refreshed.Entries[0].Availability);
        Assert.AreEqual(source, refreshed.Entries[0].OriginalPaths[0]);
    }

    [TestMethod]
    public void ExistingShortAndLongNamesMergeWithVisibleAliasNoticeWhenVolumeSupports83()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows 8.3 aliases require Windows."); return; }
        var path = CreateArchive("long source directory/long module filename.jar.disabled", "mod");
        var buffer = new StringBuilder(32768);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        var shortPath = buffer.ToString();
        if (length == 0 || length >= buffer.Capacity || !shortPath.Contains('~'))
        { Assert.Inconclusive("The temporary volume does not expose 8.3 aliases."); return; }
        var document = ResourceCollectionIndexer.Capture("Aliases", [path, shortPath]);
        Assert.AreEqual(1, document.Entries.Count);
        Assert.AreEqual(2, document.Entries[0].OriginalPaths.Count);
        Assert.AreEqual(1, document.Notices.Count);
        Assert.IsFalse(document.Entries[0].ShortNameUnresolved);
    }

    [TestMethod]
    public void StoreIsLazyAndPreservesUnknownSchemaAndConcurrentEdits()
    {
        var data = Path.Combine(_root, "data");
        var store = new ResourceCollectionStore(data);
        Assert.AreEqual(0, store.List().Count);
        Assert.IsFalse(Directory.Exists(data));
        var collection = ResourceCollectionIndexer.Capture("One", [Path.Combine(_root, "missing.jar")]);
        store.Save(collection);
        var loaded = store.Load(collection.Id);
        Assert.AreEqual(ResourceCollectionReadStatus.Valid, loaded.Status);
        Assert.AreEqual(collection.Entries[0].CanonicalPath, loaded.Document!.Entries[0].CanonicalPath);
        store.Save(loaded.Document with { Name = "Renamed" }, loaded.ContentSha256);
        Assert.ThrowsExactly<IOException>(() => store.Save(collection, loaded.ContentSha256));
        var path = Path.Combine(store.DirectoryPath, collection.Id.ToString("N") + ".json");
        File.WriteAllText(path, "{\"schemaVersion\":99}");
        loaded = store.Load(collection.Id);
        Assert.AreEqual(ResourceCollectionReadStatus.UnsupportedSchema, loaded.Status);
        Assert.ThrowsExactly<IOException>(() => store.Save(collection, loaded.ContentSha256));
        Assert.AreEqual("{\"schemaVersion\":99}", File.ReadAllText(path));
    }

    [TestMethod]
    public void CopyRetainsSourceAndDisabledSuffixAndRechecksActualDestinationBytes()
    {
        var disabled = CreateArchive("source/mod.jar.disabled", "disabled");
        var old = CreateArchive("source/old.jar.old", "old");
        var target = Path.Combine(_root, "target", "mods");
        var original = File.ReadAllBytes(disabled);
        var report = ResourceCollectionCopyService.Copy([disabled, old], target);
        Assert.AreEqual(2, report.CopiedCount);
        Assert.IsFalse(report.Cancelled);
        Assert.IsFalse(File.Exists(Path.Combine(target, "mod.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "old.jar")));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(target, "mod.jar.disabled")));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(disabled));
        report = ResourceCollectionCopyService.Copy([disabled, old], target);
        Assert.AreEqual(2, report.AlreadyPresentCount);
        File.WriteAllText(Path.Combine(target, "mod.jar.disabled"), "user-change");
        report = ResourceCollectionCopyService.Copy([disabled], target);
        Assert.AreEqual(ResourceCopyFailure.NameConflict, report.Items[0].Failure);
        Assert.AreEqual("user-change", File.ReadAllText(Path.Combine(target, "mod.jar.disabled")));
    }

    [TestMethod]
    public void MissingOccupiedUnsupportedAndCorruptItemsDoNotPreventOtherCopies()
    {
        var occupied = CreateArchive("source/occupied.jar", "occupied");
        var valid = CreateArchive("source/valid.jar", "valid");
        var corrupt = Path.Combine(_root, "bad.jar"); File.WriteAllText(corrupt, "not-a-jar");
        var unsupported = Path.Combine(_root, "notes.txt"); File.WriteAllText(unsupported, "notes");
        var missing = Path.Combine(_root, "missing.jar");
        var destination = Path.Combine(_root, "target");
        using var held = new FileStream(occupied, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var report = ResourceCollectionCopyService.Copy([missing, occupied, unsupported, corrupt, valid], destination);
        Assert.AreEqual(4, report.FailedCount);
        Assert.AreEqual(1, report.CopiedCount);
        Assert.AreEqual(ResourceCopyFailure.Missing, report.Items[0].Failure);
        Assert.AreEqual(ResourceCopyFailure.OccupiedOrUnreadable, report.Items[1].Failure);
        Assert.AreEqual(ResourceCopyFailure.Unsupported, report.Items[2].Failure);
        Assert.AreEqual(ResourceCopyFailure.InvalidArchive, report.Items[3].Failure);
        Assert.IsTrue(File.Exists(Path.Combine(destination, "valid.jar")));
        Assert.AreEqual(0, Directory.GetFiles(destination, ".pcl-resource-copy-*.tmp").Count(path => !path.EndsWith("-lock.tmp", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CancellationKeepsCompletedFilesAndStopsRemainingCopies()
    {
        var first = CreateArchive("source/first.jar", "first");
        var second = CreateArchive("source/second.jar", "second");
        var target = Path.Combine(_root, "target");
        using var cancel = new CancellationTokenSource();
        var report = ResourceCollectionCopyService.Copy([first, second], target, cancel.Token,
            new InlineProgress(_ => cancel.Cancel()));
        Assert.IsTrue(report.Cancelled);
        Assert.AreEqual(1, report.CopiedCount);
        Assert.AreEqual(ResourceCopyStatus.Cancelled, report.Items[1].Status);
        Assert.IsTrue(File.Exists(Path.Combine(target, "first.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "second.jar")));
        Assert.IsTrue(File.Exists(first)); Assert.IsTrue(File.Exists(second));
        var untouched = Path.Combine(_root, "untouched");
        report = ResourceCollectionCopyService.Copy([first], untouched, cancel.Token);
        Assert.IsTrue(report.Cancelled);
        Assert.IsFalse(Directory.Exists(untouched));
    }

    [TestMethod]
    public void LinksAreIndexedButNeitherTheirContentsNorLinkedDestinationsAreCopied()
    {
        var source = CreateArchive("source/mod.jar", "mod");
        var linked = Path.Combine(_root, "link");
        try { Directory.CreateSymbolicLink(linked, Path.GetDirectoryName(source)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { Assert.Inconclusive("Symbolic link creation is unavailable: " + ex.Message); return; }
        var alias = Path.Combine(linked, "mod.jar");
        var collection = ResourceCollectionIndexer.Capture("Linked", [alias]);
        Assert.AreEqual(ResourceAvailability.Linked, collection.Entries.Single().Availability);
        Assert.IsNull(collection.Entries[0].PhysicalIdentity);
        var target = Path.Combine(_root, "target");
        var report = ResourceCollectionCopyService.Copy([alias], target);
        Assert.AreEqual(ResourceCopyFailure.Linked, report.Items[0].Failure);
        Assert.IsFalse(File.Exists(Path.Combine(target, "mod.jar")));
        report = ResourceCollectionCopyService.Copy([source], linked);
        Assert.AreEqual(ResourceCopyFailure.Linked, report.Items[0].Failure);
    }

    [TestMethod]
    public void ReviewedTargetNameDoesNotTouchAnUnrelatedSourceNameInTheDestination()
    {
        var source = CreateArchive("source/old.jar.disabled", "payload");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var collision = Path.Combine(target, "old.jar.disabled");
        File.WriteAllText(collision, "another-selected-mod");
        var copied = ResourceCollectionCopyService.CopyAs(source, target, "new.jar.disabled");
        Assert.AreEqual(ResourceCopyStatus.Copied, copied.Items.Single().Status);
        Assert.AreEqual("another-selected-mod", File.ReadAllText(collision));
        CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(target, "new.jar.disabled")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "new.jar")));
        var again = ResourceCollectionCopyService.CopyAs(source, target, "new.jar.disabled");
        Assert.AreEqual(ResourceCopyStatus.AlreadyPresent, again.Items.Single().Status);
        File.WriteAllText(Path.Combine(target, "new.jar.disabled"), "player-edit");
        var conflict = ResourceCollectionCopyService.CopyAs(source, target, "new.jar.disabled");
        Assert.AreEqual(ResourceCopyFailure.NameConflict, conflict.Items.Single().Failure);
        Assert.AreEqual("player-edit", File.ReadAllText(Path.Combine(target, "new.jar.disabled")));
    }

    [TestMethod]
    public void ReviewedTargetNameRejectsTraversalBeforeCreatingAnyDestination()
    {
        var source = CreateArchive("source/mod.jar", "payload");
        var target = Path.Combine(_root, "not-created");
        Assert.ThrowsExactly<ArgumentException>(() => ResourceCollectionCopyService.CopyAs(source, target, "../escape.jar"));
        Assert.IsFalse(Directory.Exists(target));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "escape.jar")));
    }

    private string CreateArchive(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("test.txt").Open()); writer.Write(content);
        return path;
    }

    private sealed class InlineProgress(Action<ResourceCopyItem> report) : IProgress<ResourceCopyItem>
    { public void Report(ResourceCopyItem value) => report(value); }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);
}
