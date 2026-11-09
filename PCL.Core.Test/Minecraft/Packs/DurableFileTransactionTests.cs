using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Test.Minecraft.Packs;

[TestClass]
public sealed class DurableFileTransactionTests
{
    private string _root = null!;
    private string _game = null!;
    private string _metadata = null!;
    private string _storage = null!;
    private Dictionary<string, string> Roots => new() { ["game"] = _game, ["metadata"] = _metadata };
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Hash(string text) => DurableFileTransaction.Hash(Bytes(text));

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLDurableTransactions", Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_root, "game"); _metadata = Path.Combine(_root, "metadata");
        _storage = Path.Combine(_game, "PCL", "Enhancements", "Transactions");
        Directory.CreateDirectory(_game); Directory.CreateDirectory(_metadata);
        File.WriteAllText(Path.Combine(_game, "mod.jar"), "old game");
        File.WriteAllText(Path.Combine(_metadata, "instance.json"), "old metadata");
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private TransactionFileChange[] Changes() =>
    [
        new("metadata", "instance.json", Hash("old metadata"), Hash("new metadata"), Content: Bytes("new metadata"), IsMetadata: true),
        new("game", "mod.jar", Hash("old game"), Hash("new game"), Content: Bytes("new game")),
    ];

    [TestMethod]
    public void GameAndMetadataAreCommittedTogetherWithMetadataLast()
    {
        DurableFileTransaction.Apply(_storage, Roots, Changes(), progress: (done, _) =>
        {
            if (done == 1) Assert.AreEqual("old metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
        });
        Assert.AreEqual("new game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.AreEqual("new metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
        Assert.AreEqual(0, DurableFileTransaction.Recover(_storage, Roots));
    }

    [TestMethod]
    public void FailureAfterFirstWriteRestoresBothKindsOfOriginalContent()
    {
        Assert.ThrowsExactly<IOException>(() => DurableFileTransaction.Apply(_storage, Roots, Changes(),
            progress: (done, _) => { if (done == 1) throw new IOException("simulated failure"); }));
        Assert.AreEqual("old game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.AreEqual("old metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
    }

    [TestMethod]
    public void ExternalEditIsNeverOverwrittenDuringRollbackAndRecoveryEvidenceRemains()
    {
        Assert.ThrowsExactly<AggregateException>(() => DurableFileTransaction.Apply(_storage, Roots, Changes(), progress: (done, _) =>
        {
            if (done != 1) return;
            File.WriteAllText(Path.Combine(_game, "mod.jar"), "player edit");
            throw new IOException("simulated failure");
        }));
        Assert.AreEqual("player edit", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.IsTrue(Directory.EnumerateDirectories(_storage, "tx-*").Any());
        Assert.ThrowsExactly<IOException>(() => DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("player edit", File.ReadAllText(Path.Combine(_game, "mod.jar")));
    }

    [TestMethod]
    public void InterruptedUncommittedJournalRestoresBytesAndCanBeRecoveredTwice()
    {
        PrepareInterrupted(committed: false);
        Assert.AreEqual(1, DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("old game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.AreEqual("old metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
        Assert.AreEqual(0, DurableFileTransaction.Recover(_storage, Roots));
    }

    [TestMethod]
    public void ValidCommitDoesNotRollbackEvenWhenCleanupAlreadyRemovedBackups()
    {
        var directory = PrepareInterrupted(committed: true);
        File.Delete(Path.Combine(directory, "old-00000000.bin"));
        File.WriteAllText(Path.Combine(_game, "mod.jar"), "later player edit");
        Assert.AreEqual(1, DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("later player edit", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.AreEqual("new metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
    }

    [TestMethod]
    public void CorruptCommitMarkerPreservesLiveFilesAndRecoveryMaterial()
    {
        var directory = PrepareInterrupted(committed: false);
        File.WriteAllText(Path.Combine(directory, "commit.json"), "invalid");
        Assert.ThrowsExactly<JsonException>(() => DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("new game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "old-00000000.bin")));
    }

    [TestMethod]
    public void PathEscapeAndStalePreviewAreRejectedBeforeLiveChanges()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Apply(_storage, Roots,
            [new("game", "../outside", null, Hash("bad"), Content: Bytes("bad"))]));
        Assert.ThrowsExactly<IOException>(() => DurableFileTransaction.Apply(_storage, Roots,
            [new("game", "mod.jar", Hash("wrong"), Hash("bad"), Content: Bytes("bad"))]));
        Assert.AreEqual("old game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.AreEqual("old metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
    }

    [TestMethod]
    public void CancellationAfterGameOrLastMetadataWriteRestoresBothRoots()
    {
        foreach (var stopAt in new[] { 1, 2 })
        {
            using var cancellation = new CancellationTokenSource();
            Assert.ThrowsExactly<OperationCanceledException>(() => DurableFileTransaction.Apply(_storage, Roots, Changes(),
                cancellation.Token, (done, _) => { if (done == stopAt) cancellation.Cancel(); }));
            Assert.AreEqual("old game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
            Assert.AreEqual("old metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
            Assert.AreEqual(0, DurableFileTransaction.Recover(_storage, Roots));
        }
    }

    [TestMethod]
    public void NullChangesAndDuplicateDestinationsCannotStartLiveMutations()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => DurableFileTransaction.Apply(_storage, Roots, null!));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Apply(_storage, Roots, [null!]));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Apply(_storage, Roots,
            [Changes()[1], Changes()[1]]));
        Assert.AreEqual("old game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
    }

    [TestMethod]
    public void UnknownRecoveryRootsAndCopiedJournalAreRejectedBeforeWriting()
    {
        var directory = PrepareInterrupted(committed: false, schema: 2);
        var otherMetadata = Path.Combine(_root, "other-metadata");
        Directory.CreateDirectory(otherMetadata);
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Recover(_storage,
            new Dictionary<string, string> { ["game"] = _game, ["metadata"] = otherMetadata }));
        var copiedStorage = Path.Combine(_game, "copied-transactions");
        var copiedDirectory = Path.Combine(copiedStorage, Path.GetFileName(directory));
        Directory.CreateDirectory(copiedDirectory);
        foreach (var file in Directory.EnumerateFiles(directory)) File.Copy(file, Path.Combine(copiedDirectory, Path.GetFileName(file)));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Recover(copiedStorage, Roots));
        Assert.AreEqual("new game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.AreEqual("new metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
    }

    [TestMethod]
    public void RecoveryRejectsOwnStorageAndAssetAliases()
    {
        var directory = PrepareInterrupted(committed: false);
        var path = Path.Combine(directory, "transaction.json");
        var original = File.ReadAllText(path);
        var journal = JsonNode.Parse(original)!.AsObject();
        journal["Entries"]![0]!["RelativePath"] = Path.GetRelativePath(_game, Path.Combine(_storage, ".journal.lock")).Replace('\\', '/');
        File.WriteAllText(path, journal.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Recover(_storage, Roots));
        journal = JsonNode.Parse(original)!.AsObject();
        journal["Entries"]![0]!["Backup"] = "old-00000001.bin";
        File.WriteAllText(path, journal.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("new game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
    }

    [TestMethod]
    public void DuplicateJsonPropertiesAndCorruptBackupsFailClosed()
    {
        var directory = PrepareInterrupted(committed: false);
        var journalPath = Path.Combine(directory, "transaction.json");
        var original = File.ReadAllText(journalPath);
        File.WriteAllText(journalPath, "{\"Schema\":999," + original[1..]);
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Recover(_storage, Roots));
        File.WriteAllText(journalPath, original);
        File.WriteAllText(Path.Combine(directory, "old-00000000.bin"), "damaged backup");
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("new game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.AreEqual("new metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
    }

    [TestMethod]
    public void RecoveryNeverReadsStagedPayloadOrResumesDownloads()
    {
        var directory = PrepareInterrupted(committed: false);
        // No new-* staging files exist: recovery must require only the original backups.
        Assert.AreEqual(0, Directory.EnumerateFiles(directory, "new-*").Count());
        Assert.AreEqual(1, DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("old game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.AreEqual("old metadata", File.ReadAllText(Path.Combine(_metadata, "instance.json")));
    }

    [TestMethod]
    public void CleanupPreservesAnExternalSubtreeAndSimilarLookingFile()
    {
        var directory = PrepareInterrupted(committed: true);
        var external = Path.Combine(directory, "user-folder");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "keep.txt"), "user content");
        File.WriteAllText(Path.Combine(directory, "old-player-data.bin"), "also user content");
        Assert.AreEqual(1, DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("user content", File.ReadAllText(Path.Combine(external, "keep.txt")));
        Assert.AreEqual("also user content", File.ReadAllText(Path.Combine(directory, "old-player-data.bin")));
        Assert.AreEqual("new game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
    }

    [TestMethod]
    public void MissingJournalLeavesEvidenceUntouched()
    {
        var directory = Path.Combine(_storage, "tx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "old-00000000.bin"), "valuable original");
        Assert.ThrowsExactly<FileNotFoundException>(() => DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("valuable original", File.ReadAllText(Path.Combine(directory, "old-00000000.bin")));
        Assert.AreEqual("old game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
    }

    [TestMethod]
    public void CommitMarkerDirectoryCannotBeTreatedAsAnUncommittedTransaction()
    {
        var directory = PrepareInterrupted(committed: false);
        Directory.CreateDirectory(Path.Combine(directory, "commit.json"));
        Assert.ThrowsExactly<InvalidDataException>(() => DurableFileTransaction.Recover(_storage, Roots));
        Assert.AreEqual("new game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
    }

    [TestMethod]
    public void PackAdapterRejectsUnknownActionsBeforeMetadataPreparation()
    {
        var store = new PackBaselineStore(_metadata);
        var plan = new PackUpdatePlan
        {
            GameRoot = _game, Previous = null, Target = new PackBaseline(),
            Actions = [new PackUpdateAction { Kind = (PackUpdateActionKind)999, Ownership = PackFileOwnership.Unknown,
                DestinationPath = "mod.jar", PayloadPath = "not-read.jar", ExpectedSha256 = Hash("bad") }],
        };
        Assert.ThrowsExactly<InvalidDataException>(() => PackUpdateDurableTransaction.Apply(plan, store, _game));
        Assert.AreEqual("old game", File.ReadAllText(Path.Combine(_game, "mod.jar")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_metadata, "PackBaselines")));
    }

    private string PrepareInterrupted(bool committed, int schema = 1)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(_storage, "tx-" + id.ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "old-00000000.bin"), "old game");
        File.WriteAllText(Path.Combine(directory, "old-00000001.bin"), "old metadata");
        var entries = new[]
        {
            new { RootId = "game", RelativePath = "mod.jar", Before = Hash("old game"), After = Hash("new game"), Backup = "old-00000000.bin", Staged = "new-00000000.bin", IsMetadata = false },
            new { RootId = "metadata", RelativePath = "instance.json", Before = Hash("old metadata"), After = Hash("new metadata"), Backup = "old-00000001.bin", Staged = "new-00000001.bin", IsMetadata = true },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = schema, Id = id, Roots, Entries = entries,
            StorageDirectory = schema == 2 ? _storage : null });
        File.WriteAllBytes(Path.Combine(directory, "transaction.json"), bytes);
        File.WriteAllText(Path.Combine(_game, "mod.jar"), "new game");
        File.WriteAllText(Path.Combine(_metadata, "instance.json"), "new metadata");
        if (committed) File.WriteAllBytes(Path.Combine(directory, "commit.json"),
            JsonSerializer.SerializeToUtf8Bytes(new { Id = id, JournalSha256 = DurableFileTransaction.Hash(bytes) }));
        return directory;
    }
}
