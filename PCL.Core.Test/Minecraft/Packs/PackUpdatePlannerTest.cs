using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Test.Minecraft.Packs;

[TestClass]
public sealed class PackUpdatePlannerTest
{
    private string _root = null!;
    private string _stage = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLPackTests", Guid.NewGuid().ToString("N"));
        _stage = Path.Combine(_root, "stage");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_stage);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void PlanAndApply_UpdatesAuthorFiles_RemovesUnchangedFiles_AndPreservesUnchangedFiles()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        Write(Path.Combine(game, "mods", "a.jar"), "old-a");
        Write(Path.Combine(game, "mods", "b.jar"), "same-b");
        Write(Path.Combine(game, "mods", "c.jar"), "old-c");
        var payload = Path.Combine(_stage, "new-a.jar");
        Write(payload, "new-a");
        var old = Baseline(Entry("mods/a.jar", "old-a", "project:a:file"), Entry("mods/b.jar", "same-b", "project:b:file"), Entry("mods/c.jar", "old-c", "project:c:file"));
        var target = Baseline(Entry("mods/a.jar", "new-a", "project:a:file"), Entry("mods/b.jar", "same-b", "project:b:file"));
        var plan = PackUpdatePlanner.Build(game, old, target, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mods/a.jar"] = payload });
        PackUpdateTransaction.Apply(plan);
        Assert.AreEqual("new-a", File.ReadAllText(Path.Combine(game, "mods", "a.jar")));
        Assert.AreEqual("same-b", File.ReadAllText(Path.Combine(game, "mods", "b.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(game, "mods", "c.jar")));
    }

    [TestMethod]
    public void CompletePackUpdate_MatchesExpectedInstanceTreeAndPreservesPlayerAndLauncherFiles()
    {
        var game = Path.Combine(_root, "game");
        Write(Path.Combine(game, "mods", "a.jar"), "author-a-v1");
        Write(Path.Combine(game, "mods", "remove.jar"), "author-remove");
        Write(Path.Combine(game, "config", "author.json"), "player-edit");
        Write(Path.Combine(game, "config", "user.json"), "player-file");
        Write(Path.Combine(game, "saves", "World", "level.dat"), "player-world");
        Write(Path.Combine(game, "PCLLiteData", "WorldBackups", "snapshot.zip"), "launcher-backup");
        Write(Path.Combine(game, "libraries", "runtime.jar"), "runtime");
        Directory.CreateDirectory(Path.Combine(game, "mods")); // tracked disabled mod intentionally remains absent

        var targetEntries = new[]
        {
            Entry("mods/a.jar", "author-a-v2", "cf:a"),
            Entry("mods/disabled.jar", "author-disabled", "cf:disabled"),
            Entry("mods/new.jar", "author-new", "cf:new"),
            Entry("config/author.json", "author-setting-v2", "settings:author"),
        };
        var old = Baseline(
            Entry("mods/a.jar", "author-a-v1", "cf:a"),
            Entry("mods/remove.jar", "author-remove", "cf:remove"),
            Entry("mods/disabled.jar", "author-disabled-v1", "cf:disabled"),
            Entry("config/author.json", "author-setting-v1", "settings:author"));
        var target = Baseline(targetEntries);
        var payloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in targetEntries)
        {
            var payload = Path.Combine(_stage, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Write(payload, entry.Identity switch
            {
                "cf:a" => "author-a-v2",
                "cf:disabled" => "author-disabled",
                "cf:new" => "author-new",
                _ => "author-setting-v2",
            });
            payloads.Add(entry.Path, payload);
        }

        var plan = PackUpdatePlanner.Build(game, old, target, payloads);
        PackUpdateTransaction.Apply(plan);

        var expected = Path.Combine(_root, "expected");
        Write(Path.Combine(expected, "mods", "a.jar"), "author-a-v2");
        Write(Path.Combine(expected, "mods", "new.jar"), "author-new");
        Write(Path.Combine(expected, "config", "author.json"), "player-edit");
        Write(Path.Combine(expected, "config", "user.json"), "player-file");
        Write(Path.Combine(expected, "saves", "World", "level.dat"), "player-world");
        Write(Path.Combine(expected, "PCLLiteData", "WorldBackups", "snapshot.zip"), "launcher-backup");
        Write(Path.Combine(expected, "libraries", "runtime.jar"), "runtime");

        var differences = PackArtifactTree.Compare(PackArtifactTree.Capture(expected), PackArtifactTree.Capture(game));
        Assert.AreEqual(0, differences.Count, string.Join(Environment.NewLine, differences));
    }

    [TestMethod]
    public void FirstPackInstall_MatchesTargetTreeWhileRetainingExistingRuntimeFiles()
    {
        var game = Path.Combine(_root, "game");
        Write(Path.Combine(game, "versions", "1.20.1", "1.20.1.json"), "vanilla-json");
        Write(Path.Combine(game, "PCLLiteData", "settings.json"), "launcher-settings");
        var entries = new[] { Entry("mods/a.jar", "mod-a", "cf:a"), Entry("config/pack.json", "pack-config", "mr:c") };
        var target = Baseline(entries);
        var payloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var payload = Path.Combine(_stage, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Write(payload, entry.Identity == "cf:a" ? "mod-a" : "pack-config");
            payloads.Add(entry.Path, payload);
        }

        PackUpdateTransaction.Apply(PackUpdatePlanner.Build(game, null, target, payloads));

        var expected = Path.Combine(_root, "expected-install");
        Write(Path.Combine(expected, "versions", "1.20.1", "1.20.1.json"), "vanilla-json");
        Write(Path.Combine(expected, "PCLLiteData", "settings.json"), "launcher-settings");
        Write(Path.Combine(expected, "mods", "a.jar"), "mod-a");
        Write(Path.Combine(expected, "config", "pack.json"), "pack-config");
        var differences = PackArtifactTree.Compare(PackArtifactTree.Capture(expected), PackArtifactTree.Capture(game));
        Assert.AreEqual(0, differences.Count, string.Join(Environment.NewLine, differences));
    }
    [TestMethod]
    public void ModifiedLocalFile_IsPreservedByDefault_AndAuthorRemovalDoesNotDeleteIt()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "config"));
        Write(Path.Combine(game, "config", "client.json"), "player-setting");
        var old = Baseline(Entry("config/client.json", "author-setting", "settings:client"));
        var plan = PackUpdatePlanner.Build(game, old, Baseline(), new Dictionary<string, string>());
        PackUpdateTransaction.Apply(plan);
        Assert.AreEqual("player-setting", File.ReadAllText(Path.Combine(game, "config", "client.json")));
    }

    [TestMethod]
    public void MissingTrackedAuthorFile_IsPreservedAsDisabledByDefault()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        var old = Baseline(Entry("mods/optional.jar", "author-v1", "project:optional:file"));
        var target = Baseline(Entry("mods/optional.jar", "author-v2", "project:optional:file"));
        var payload = Path.Combine(_stage, "optional.jar");
        Write(payload, "author-v2");

        var plan = PackUpdatePlanner.Build(game, old, target,
            new Dictionary<string, string> { ["mods/optional.jar"] = payload });

        Assert.AreEqual(1, plan.Actions.Count);
        Assert.AreEqual(PackUpdateActionKind.PreservePersonalFile, plan.Actions[0].Kind);
        Assert.AreEqual(PackFileOwnership.Disabled, plan.Actions[0].Ownership);
        PackUpdateTransaction.Apply(plan);
        Assert.IsFalse(File.Exists(Path.Combine(game, "mods", "optional.jar")));
    }

    [TestMethod]
    public void MissingTrackedAuthorFile_CanBeRestoredWhenUserChoosesAuthorVersion()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        var old = Baseline(Entry("mods/optional.jar", "author-v1", "project:optional:file"));
        var target = Baseline(Entry("mods/optional.jar", "author-v2", "project:optional:file"));
        var payload = Path.Combine(_stage, "optional.jar");
        Write(payload, "author-v2");

        var plan = PackUpdatePlanner.Build(game, old, target,
            new Dictionary<string, string> { ["mods/optional.jar"] = payload },
            new Dictionary<string, PackFileChoice> { ["mods/optional.jar"] = PackFileChoice.UseAuthor });

        PackUpdateTransaction.Apply(plan);
        Assert.AreEqual("author-v2", File.ReadAllText(Path.Combine(game, "mods", "optional.jar")));
    }
    [TestMethod]
    public void ModifiedLocalFile_CanExplicitlyAcceptAuthorVersion()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "config"));
        Write(Path.Combine(game, "config", "client.json"), "player-setting");
        var payload = Path.Combine(_stage, "client.json");
        Write(payload, "new-author-setting");
        var old = Baseline(Entry("config/client.json", "author-setting", "settings:client"));
        var target = Baseline(Entry("config/client.json", "new-author-setting", "settings:client"));
        var plan = PackUpdatePlanner.Build(game, old, target, new Dictionary<string, string> { ["config/client.json"] = payload }, new Dictionary<string, PackFileChoice> { ["config/client.json"] = PackFileChoice.UseAuthor });
        PackUpdateTransaction.Apply(plan);
        Assert.AreEqual("new-author-setting", File.ReadAllText(Path.Combine(game, "config", "client.json")));
    }

    [TestMethod]
    public void NewAuthorFileDoesNotOverwriteUntrackedPlayerFileByDefault()
    {
        var game = Path.Combine(_root, "game");
        var destination = Path.Combine(game, "mods", "player.jar");
        Write(destination, "player-owned-bytes");
        var payload = Path.Combine(_stage, "author.jar");
        Write(payload, "author-bytes");
        var target = Baseline(Entry("mods/player.jar", "author-bytes", "cf:new-mod"));

        var plan = PackUpdatePlanner.Build(game, Baseline(), target,
            new Dictionary<string, string> { ["mods/player.jar"] = payload });

        Assert.AreEqual(PackUpdateActionKind.PreservePersonalFile, plan.Actions.Single().Kind);
        Assert.AreEqual(PackFileOwnership.PersonalAdded, plan.Actions.Single().Ownership);
        PackUpdateTransaction.Apply(plan);
        Assert.AreEqual("player-owned-bytes", File.ReadAllText(destination));
    }

    [TestMethod]
    public void AuthorRenameDoesNotOverwriteFileAtNewPathByDefault()
    {
        var game = Path.Combine(_root, "game");
        var oldPath = Path.Combine(game, "mods", "old.jar");
        var newPath = Path.Combine(game, "mods", "new.jar");
        Write(oldPath, "author-v1");
        Write(newPath, "player-file");
        var payload = Path.Combine(_stage, "new-author.jar");
        Write(payload, "author-v2");
        var old = Baseline(Entry("mods/old.jar", "author-v1", "project:mod"));
        var target = Baseline(Entry("mods/new.jar", "author-v2", "project:mod"));

        var plan = PackUpdatePlanner.Build(game, old, target,
            new Dictionary<string, string> { ["mods/new.jar"] = payload });

        Assert.IsTrue(plan.Actions.All(static action => action.Kind == PackUpdateActionKind.PreservePersonalFile));
        PackUpdateTransaction.Apply(plan);
        Assert.AreEqual("author-v1", File.ReadAllText(oldPath));
        Assert.AreEqual("player-file", File.ReadAllText(newPath));
    }

    [TestMethod]
    public void MidTransactionDestinationChangeRollsBackPriorUpdates()
    {
        var game = Path.Combine(_root, "game");
        var a = Path.Combine(game, "mods", "a.jar");
        var b = Path.Combine(game, "mods", "b.jar");
        Write(a, "old-a");
        Write(b, "old-b");
        var payloadA = Path.Combine(_stage, "new-a.jar");
        var payloadB = Path.Combine(_stage, "new-b.jar");
        Write(payloadA, "new-a");
        Write(payloadB, "new-b");
        var old = Baseline(Entry("mods/a.jar", "old-a", "a:file"), Entry("mods/b.jar", "old-b", "b:file"));
        var target = Baseline(Entry("mods/a.jar", "new-a", "a:file"), Entry("mods/b.jar", "new-b", "b:file"));
        var plan = PackUpdatePlanner.Build(game, old, target, new Dictionary<string, string>
        {
            ["mods/a.jar"] = payloadA,
            ["mods/b.jar"] = payloadB,
        });

        Assert.ThrowsExactly<IOException>(() => PackUpdateTransaction.Apply(plan, progress: (completed, _) =>
        {
            if (completed == 1) Write(b, "external-edit");
        }));
        Assert.AreEqual("old-a", File.ReadAllText(a));
        Assert.AreEqual("external-edit", File.ReadAllText(b));
    }

    [TestMethod]
    public void NewAuthorFileCanReplaceOccupiedPathAfterExplicitAuthorChoice()
    {
        var game = Path.Combine(_root, "game");
        var destination = Path.Combine(game, "mods", "player.jar");
        Write(destination, "player-owned-bytes");
        var payload = Path.Combine(_stage, "author.jar");
        Write(payload, "author-bytes");
        var target = Baseline(Entry("mods/player.jar", "author-bytes", "cf:new-mod"));

        var plan = PackUpdatePlanner.Build(game, Baseline(), target,
            new Dictionary<string, string> { ["mods/player.jar"] = payload },
            new Dictionary<string, PackFileChoice> { ["mods/player.jar"] = PackFileChoice.UseAuthor });

        PackUpdateTransaction.Apply(plan);
        Assert.AreEqual("author-bytes", File.ReadAllText(destination));
    }

    [TestMethod]
    public void StalePreviewSnapshotRejectsBeforeChangingAnyDestination()
    {
        var game = Path.Combine(_root, "game");
        var a = Path.Combine(game, "mods", "a.jar");
        var b = Path.Combine(game, "mods", "b.jar");
        Write(a, "old-a");
        Write(b, "old-b");
        var payloadA = Path.Combine(_stage, "new-a.jar");
        var payloadB = Path.Combine(_stage, "new-b.jar");
        Write(payloadA, "new-a");
        Write(payloadB, "new-b");
        var old = Baseline(Entry("mods/a.jar", "old-a", "a:file"), Entry("mods/b.jar", "old-b", "b:file"));
        var target = Baseline(Entry("mods/a.jar", "new-a", "a:file"), Entry("mods/b.jar", "new-b", "b:file"));
        var plan = PackUpdatePlanner.Build(game, old, target, new Dictionary<string, string>
        {
            ["mods/a.jar"] = payloadA,
            ["mods/b.jar"] = payloadB,
        });
        Write(b, "player-edited-after-preview");

        Assert.ThrowsExactly<IOException>(() => PackUpdateTransaction.Apply(plan));
        Assert.AreEqual("old-a", File.ReadAllText(a));
        Assert.AreEqual("player-edited-after-preview", File.ReadAllText(b));
    }

    [TestMethod]
    public void InvalidPayloadHash_FailsBeforeMutatingTheInstance()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        Write(Path.Combine(game, "mods", "existing.jar"), "existing");
        var payload = Path.Combine(_stage, "bad.jar");
        Write(payload, "wrong-payload");
        var target = Baseline(Entry("mods/new.jar", "expected", "new:file"));
        var plan = PackUpdatePlanner.Build(game, null, target, new Dictionary<string, string> { ["mods/new.jar"] = payload });
        Assert.ThrowsExactly<InvalidDataException>(() => PackUpdateTransaction.Apply(plan));
        Assert.AreEqual("existing", File.ReadAllText(Path.Combine(game, "mods", "existing.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(game, "mods", "new.jar")));
    }

    [TestMethod]
    public void CancellationBeforeCommit_LeavesInstanceUntouched()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        var payload = Path.Combine(_stage, "new.jar");
        Write(payload, "new");
        var target = Baseline(Entry("mods/new.jar", "new", "new:file"));
        var plan = PackUpdatePlanner.Build(game, null, target, new Dictionary<string, string> { ["mods/new.jar"] = payload });
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PackUpdateTransaction.Apply(plan, canceled.Token));
        Assert.IsFalse(File.Exists(Path.Combine(game, "mods", "new.jar")));
    }

    [TestMethod]
    public void CancellationDuringCommit_RestoresEarlierReplacements()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        Write(Path.Combine(game, "mods", "a.jar"), "old-a");
        var payloadA = Path.Combine(_stage, "new-a.jar");
        var payloadB = Path.Combine(_stage, "new-b.jar");
        Write(payloadA, "new-a");
        Write(payloadB, "new-b");
        var old = Baseline(Entry("mods/a.jar", "old-a", "a:file"));
        var target = Baseline(Entry("mods/a.jar", "new-a", "a:file"), Entry("mods/b.jar", "new-b", "b:file"));
        var plan = PackUpdatePlanner.Build(game, old, target, new Dictionary<string, string>
        {
            ["mods/a.jar"] = payloadA,
            ["mods/b.jar"] = payloadB,
        });
        using var canceled = new CancellationTokenSource();

        Assert.ThrowsExactly<OperationCanceledException>(() => PackUpdateTransaction.Apply(plan, canceled.Token,
            (completed, _) => { if (completed == 1) canceled.Cancel(); }));

        Assert.AreEqual("old-a", File.ReadAllText(Path.Combine(game, "mods", "a.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(game, "mods", "b.jar")));
        Assert.AreEqual(0, Directory.GetDirectories(Path.GetDirectoryName(game)!, $".{Path.GetFileName(game)}.pcl-update-*").Length);
    }

    [TestMethod]
    public void MetadataCommitFailure_RollsBackAppliedGameFiles()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        Write(Path.Combine(game, "mods", "a.jar"), "old-a");
        var payload = Path.Combine(_stage, "new-a.jar");
        Write(payload, "new-a");
        var plan = PackUpdatePlanner.Build(game,
            Baseline(Entry("mods/a.jar", "old-a", "a:file")),
            Baseline(Entry("mods/a.jar", "new-a", "a:file")),
            new Dictionary<string, string> { ["mods/a.jar"] = payload });

        Assert.ThrowsExactly<IOException>(() => PackUpdateTransaction.Apply(plan, commitMetadata: () => throw new IOException("index write failed")));

        Assert.AreEqual("old-a", File.ReadAllText(Path.Combine(game, "mods", "a.jar")));
    }

    [TestMethod]
    public void TraversalPathsAreRejected()
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var target = Baseline(Entry("../outside.txt", "x", "bad"));
        Assert.ThrowsExactly<InvalidDataException>(() => PackUpdatePlanner.Build(game, null, target, new Dictionary<string, string>()));
    }

    [TestMethod]
    public void ArtifactTreeComparisonUsesPathsAndFileBytesNotTimestamps()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Write(Path.Combine(first, "mods", "a.jar"), "same");
        Write(Path.Combine(second, "mods", "a.jar"), "same");
        File.SetLastWriteTimeUtc(Path.Combine(first, "mods", "a.jar"), DateTime.UnixEpoch);
        File.SetLastWriteTimeUtc(Path.Combine(second, "mods", "a.jar"), DateTime.UtcNow);
        var diffs = PackArtifactTree.Compare(PackArtifactTree.Capture(first), PackArtifactTree.Capture(second));
        Assert.AreEqual(0, diffs.Count);
    }

    private static PackBaseline Baseline(params PackFileEntry[] files)
        => new() { Files = files.ToDictionary(static file => file.Path, StringComparer.OrdinalIgnoreCase) };
    private static PackFileEntry Entry(string path, string content, string identity)
        => new() { Path = path, Sha256 = Hash(content), Length = content.Length, Identity = identity };
    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
