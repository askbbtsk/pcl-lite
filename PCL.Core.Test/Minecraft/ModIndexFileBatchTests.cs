using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Resources;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class ModIndexFileBatchTests
{
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private static IProgress<int> IgnoreProgress() => new InlineProgress(_ => { });

    [TestMethod]
    [DataRow(48, 3)]
    [DataRow(49, 1)]
    [DataRow(56, 2)]
    [DataRow(50, 4)]
    public async Task SuppliedLogBatchesKeepEveryGoodEntryAndReportOnlyBadFiles(int total, int bad)
    {
        var files = Enumerable.Range(0, total).Select(index => $"mod-{index:D3}.jar").ToArray();
        var indexed = new ConcurrentDictionary<string, bool>();
        var persisted = Array.Empty<string>();
        var saves = 0;
        var result = await ModIndexFileBatch.RunAsync(files, 8, (file, _) =>
        {
            if (Array.IndexOf(files, file) < bad) return "unsupported nested metadata";
            indexed[file] = true;
            return null;
        }, () =>
        {
            Interlocked.Increment(ref saves);
            persisted = indexed.Keys.Order().ToArray();
            return true;
        }, IgnoreProgress());

        Assert.IsTrue(result.CacheSaved, "Partial success must remain eligible for shared-cache publication.");
        Assert.IsFalse(result.IsComplete, "Unknown files must not be presented as fully parsed.");
        Assert.AreEqual(total - bad, result.IndexedFiles);
        Assert.AreEqual(bad, result.SkippedFiles.Count);
        Assert.AreEqual(total - bad, persisted.Length);
        CollectionAssert.AreEquivalent(files.Skip(bad).ToArray(), persisted);
        CollectionAssert.AreEquivalent(files.Take(bad).ToArray(), result.SkippedFiles.Select(item => item.Path).ToArray());
        Assert.AreEqual(1, saves);
    }

    [TestMethod]
    public async Task OneCorruptArchiveDoesNotStopFilesBeforeOrAfterItAndDoesNotWriteSources()
    {
        var root = Path.Combine(Path.GetTempPath(), "PCLIndexPartialTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = Enumerable.Range(0, 100).Select(index => Path.Combine(root, $"mod-{index:D3}.jar")).ToArray();
            foreach (var path in files) File.WriteAllText(path, Path.GetFileName(path));
            var original = files.ToDictionary(path => path, File.ReadAllBytes);
            var manifest = Path.Combine(root, "index.txt");
            var successful = new ConcurrentDictionary<string, bool>();
            var result = await ModIndexFileBatch.RunAsync(files, 8, (file, _) =>
            {
                if (file == files[42]) throw new InvalidDataException("Not a supported ZIP archive");
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                Assert.IsTrue(stream.ReadByte() >= 0);
                successful[file] = true;
                return null;
            }, () => { File.WriteAllLines(manifest, successful.Keys); return true; }, IgnoreProgress());

            Assert.IsTrue(result.CacheSaved);
            Assert.AreEqual(99, result.IndexedFiles);
            Assert.AreEqual(99, File.ReadAllLines(manifest).Length);
            Assert.AreEqual(files[42], result.SkippedFiles.Single().Path);
            StringAssert.Contains(result.SkippedFiles.Single().Reason, "ZIP");
            foreach (var file in files) CollectionAssert.AreEqual(original[file], File.ReadAllBytes(file));
            // Completed callbacks must have released their file handles.
            foreach (var file in files) File.Delete(file);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ReadPermissionMissingFileFormatAndUnrelatedCancellationAreAllPerFileFailures()
    {
        var exceptions = new Exception[] { new UnauthorizedAccessException("denied"),
            new FileNotFoundException("removed"), new IOException("file changed"),
            new FormatException("unsupported metadata"), new OperationCanceledException("file parser stopped") };
        var files = Enumerable.Range(0, exceptions.Length + 2).Select(index => index.ToString()).ToArray();
        var result = await ModIndexFileBatch.RunAsync(files, 4, (file, _) =>
        {
            var index = int.Parse(file);
            if (index < exceptions.Length) throw exceptions[index];
            return null;
        }, () => true, IgnoreProgress());
        Assert.AreEqual(2, result.IndexedFiles);
        Assert.AreEqual(exceptions.Length, result.SkippedFiles.Count);
        Assert.IsTrue(result.CacheSaved);
        foreach (var failure in result.SkippedFiles) Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Reason));
    }

    [TestMethod]
    public async Task AllUnsupportedFilesStillProduceAnHonestSkipReportWithoutBlockingTheNextBatch()
    {
        var result = await ModIndexFileBatch.RunAsync(["a.jar", "b.jar"], 2,
            (_, _) => "unsupported", () => true, IgnoreProgress());
        Assert.AreEqual(0, result.IndexedFiles);
        Assert.AreEqual(2, result.SkippedFiles.Count);
        Assert.IsFalse(result.IsComplete);
        var next = await ModIndexFileBatch.RunAsync(["working.jar"], 2, (_, _) => null, () => true, IgnoreProgress());
        Assert.AreEqual(1, next.IndexedFiles);
        Assert.IsTrue(next.IsComplete);
    }

    [TestMethod]
    public async Task CacheSaveFailureIsNotReportedAsSuccessfulIndexCreation()
    {
        foreach (var throws in new[] { false, true })
        {
            var result = await ModIndexFileBatch.RunAsync(["working.jar"], 1, (_, _) => null,
                () => throws ? throw new IOException("disk full") : false, IgnoreProgress());
            Assert.IsFalse(result.CacheSaved);
            Assert.IsFalse(result.IsComplete);
            Assert.AreEqual(1, result.IndexedFiles);
            if (throws) StringAssert.Contains(result.SaveError!, "disk full");
        }
    }

    [TestMethod]
    public async Task UserCancellationPersistsCompletedEntriesAndDoesNotScanRemainingFiles()
    {
        using var cancelled = new CancellationTokenSource();
        var scanned = new ConcurrentQueue<string>();
        string[] persisted = [];
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModIndexFileBatch.RunAsync(
            ["good.jar", "cancel.jar", "never.jar"], 1, (file, token) =>
            {
                if (file == "cancel.jar") { cancelled.Cancel(); token.ThrowIfCancellationRequested(); }
                scanned.Enqueue(file);
                return null;
            }, () => { persisted = scanned.ToArray(); return true; }, IgnoreProgress(), cancelled.Token));
        CollectionAssert.AreEqual(new[] { "good.jar" }, persisted);
    }

    [TestMethod]
    public async Task AlreadyCancelledBatchDoesNotTouchFilesOrCache()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModIndexFileBatch.RunAsync(["a.jar"], 1,
            (_, _) => throw new AssertFailedException("must not read"),
            () => throw new AssertFailedException("must not save"), IgnoreProgress(), cancelled.Token));
    }

    [TestMethod]
    public async Task ConcurrencyIsBoundedAndReportIsDeterministicallyOrdered()
    {
        var files = Enumerable.Range(0, 1000).Select(index => $"mod-{index:D4}.jar").Reverse().ToArray();
        var active = 0;
        var progress = new ConcurrentBag<int>();
        var result = await ModIndexFileBatch.RunAsync(files, 4, (file, _) =>
        {
            Assert.IsTrue(Interlocked.Increment(ref active) <= 4);
            try { return "unsupported"; }
            finally { Interlocked.Decrement(ref active); }
        }, () => true, new InlineProgress(progress.Add));
        Assert.AreEqual(1000, result.SkippedFiles.Count);
        CollectionAssert.AreEqual(files.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            result.SkippedFiles.Select(item => item.Path).ToArray());
        CollectionAssert.AreEquivalent(Enumerable.Range(1, 1000).ToArray(), progress.ToArray());
        Assert.AreEqual(0, active);
    }

    [TestMethod]
    public async Task EmptyFolderSavesOnceAndIsComplete()
    {
        var saves = 0;
        var result = await ModIndexFileBatch.RunAsync([], 2,
            (_, _) => throw new AssertFailedException("no files"),
            () => { saves++; return true; }, IgnoreProgress());
        Assert.IsTrue(result.IsComplete);
        Assert.AreEqual(0, result.IndexedFiles);
        Assert.AreEqual(1, saves);
    }
}
