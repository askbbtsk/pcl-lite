using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.InstanceMigration;

namespace PCL.Core.Test.Minecraft.InstanceMigration;

[TestClass]
public sealed class InstanceMigrationTests
{
    private string _root = null!;
    private string _source = null!;
    private string _target = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLMigrationTests", Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "source"); _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_source);
        File.WriteAllText(Path.Combine(_source, "source.json"), "{\"id\":\"source\"}");
        File.WriteAllText(Path.Combine(_source, "user.txt"), "unchanged");
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [TestMethod]
    public void CandidatesRequireExactProjectTargetMcLoaderAndValidArtifact()
    {
        var candidate = Candidate();
        var source = Source() with
        {
            Candidates = [candidate with { ProjectId = "same-name-other-project" },
                candidate with { MinecraftVersions = ["1.20.1"] }, candidate with { Loaders = [MigrationLoader.Fabric] },
                candidate with { FileName = "../escaped.jar" }, candidate with { Hash = "invalid" }],
        };
        var plan = InstanceMigrationPlanner.Create("1.21.1", MigrationLoader.NeoForge, [source]);
        Assert.AreEqual(MigrationModAction.NeedsReview, plan.Mods.Single().Action);
    }

    [TestMethod]
    public void DisabledSuffixIsRestoredOnDifferentBuildAndSourceBytesAreNotMistakenForTargetCompatibility()
    {
        var source = Source() with { Path = "sodium-fabric.jar.disabled", Candidates = [Candidate()] };
        var plan = InstanceMigrationPlanner.Create("1.21.1", MigrationLoader.NeoForge, [source]);
        Assert.AreEqual(MigrationModAction.DownloadSameProject, plan.Mods[0].Action);
        Assert.AreEqual("target.jar.disabled", plan.Mods[0].TargetFileName);
        Assert.IsTrue(plan.Mods[0].Disabled);
        Assert.AreEqual("target.jar.old", InstanceMigrationPlanner.PreserveDisabledName("target.jar.disabled", "source.jar.old"));
        Assert.AreEqual("target.jar", InstanceMigrationPlanner.PreserveDisabledName("target.jar.disabled", "source.jar"));
    }

    [TestMethod]
    public void SameVerifiedBytesCanBeReusedOnlyWhenTheirOwnMetadataSupportsTarget()
    {
        var candidate = Candidate() with { Hash = Hash("source") };
        var source = Source() with { Candidates = [candidate] };
        Assert.AreEqual(MigrationModAction.CopyVerifiedLocal,
            InstanceMigrationPlanner.Create("1.21.1", MigrationLoader.NeoForge, [source]).Mods[0].Action);
        source = source with { Candidates = [candidate with { Loaders = [MigrationLoader.Fabric] }] };
        Assert.AreEqual(MigrationModAction.NeedsReview,
            InstanceMigrationPlanner.Create("1.21.1", MigrationLoader.NeoForge, [source]).Mods[0].Action);
    }

    [TestMethod]
    public void AmbiguousLatestBuildNeedsReviewAndStableBuildsArePreferred()
    {
        var first = Candidate(); var second = first with { FileId = "second" };
        var source = Source() with { Candidates = [first, second] };
        Assert.AreEqual(MigrationModAction.NeedsReview,
            InstanceMigrationPlanner.Create("1.21.1", MigrationLoader.NeoForge, [source]).Mods[0].Action);
        source = source with { Candidates = [first, second with { Stable = false, PublishedAt = second.PublishedAt.AddDays(1) }] };
        Assert.AreEqual(first.FileId,
            InstanceMigrationPlanner.Create("1.21.1", MigrationLoader.NeoForge, [source]).Mods[0].Candidate!.FileId);
    }

    [TestMethod]
    public async Task FreshTargetUsesInstallerThenMaterializerAndValidationBeforeRenameAndSourceIsUntouched()
    {
        var mod = Path.Combine(_source, "mod.jar.disabled"); File.WriteAllText(mod, "source");
        var planned = InstanceMigrationPlanner.Create("1.21.1", MigrationLoader.NeoForge,
            [Source() with { Path = mod, Candidates = [Candidate()] }]);
        var stages = new List<string>();
        var result = await InstanceMigrationTransaction.ExecuteAsync(_source, _target, planned,
            (stage, _) => { stages.Add("install"); return Install(stage); },
            (stage, item, _) =>
            {
                stages.Add("materialize"); Directory.CreateDirectory(Path.Combine(stage.StageDirectory, "mods"));
                File.WriteAllText(Path.Combine(stage.StageDirectory, "mods", item.TargetFileName!), "downloaded"); return Task.CompletedTask;
            },
            (stage, _) => { stages.Add("validate"); return Task.FromResult<IReadOnlyList<string>>([]); });
        Assert.IsTrue(result.Success, string.Join(",", result.Problems));
        CollectionAssert.AreEqual(new[] { "install", "materialize", "validate" }, stages);
        Assert.AreEqual("unchanged", File.ReadAllText(Path.Combine(_source, "user.txt")));
        Assert.AreEqual("source", File.ReadAllText(mod));
        Assert.AreEqual("downloaded", File.ReadAllText(Path.Combine(_target, "mods", "target.jar.disabled")));
        Assert.IsTrue(File.Exists(Path.Combine(_target, "target.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_target, "target.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(_target, ".pclignore")));
        Assert.IsFalse(Directory.GetFiles(_target, ".pcl-migration-*.json").Any());
    }

    [TestMethod]
    public async Task ValidationFailureOrCancellationNeverPublishesPartialTargetOrDeletesSource()
    {
        var result = await InstanceMigrationTransaction.ExecuteAsync(_source, _target, EmptyPlan(), InstallWithToken,
            (_, _, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["missing dependency"]));
        Assert.IsFalse(result.Success); Assert.IsFalse(Directory.Exists(_target));
        Assert.AreEqual("unchanged", File.ReadAllText(Path.Combine(_source, "user.txt")));
        using var cancel = new CancellationTokenSource();
        result = await InstanceMigrationTransaction.ExecuteAsync(_source, _target, EmptyPlan(),
            async (stage, _) => { await Install(stage); cancel.Cancel(); }, (_, _, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]), cancellationToken: cancel.Token);
        Assert.IsTrue(result.Cancelled); Assert.IsFalse(Directory.Exists(_target));
        Assert.AreEqual(0, Directory.GetDirectories(_root, ".pcl-migration-*").Length);
    }

    [TestMethod]
    public async Task TargetCreatedDuringValidationIsPreservedInsteadOfOverwritten()
    {
        var result = await InstanceMigrationTransaction.ExecuteAsync(_source, _target, EmptyPlan(), InstallWithToken,
            (_, _, _) => Task.CompletedTask,
            (_, _) =>
            {
                Directory.CreateDirectory(_target); File.WriteAllText(Path.Combine(_target, "user.txt"), "created-later");
                return Task.FromResult<IReadOnlyList<string>>([]);
            });
        Assert.IsFalse(result.Success);
        Assert.AreEqual("created-later", File.ReadAllText(Path.Combine(_target, "user.txt")));
        Assert.AreEqual("unchanged", File.ReadAllText(Path.Combine(_source, "user.txt")));
    }

    [TestMethod]
    public async Task SourceChangeAfterPreviewRejectsCommitBeforeMaterialization()
    {
        var mod = Path.Combine(_source, "mod.jar"); File.WriteAllText(mod, "modified");
        var plan = InstanceMigrationPlanner.Create("1.21.1", MigrationLoader.NeoForge,
            [Source() with { Path = mod, Candidates = [Candidate()] }]);
        var materialized = false;
        var result = await InstanceMigrationTransaction.ExecuteAsync(_source, _target, plan, InstallWithToken,
            (_, _, _) => { materialized = true; return Task.CompletedTask; },
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]));
        Assert.IsFalse(result.Success); Assert.IsFalse(materialized);
        Assert.AreEqual("modified", File.ReadAllText(mod)); Assert.IsFalse(Directory.Exists(_target));
    }

    private static InstanceMigrationPlan EmptyPlan() => new("1.21.1", MigrationLoader.NeoForge, []);
    private static Task InstallWithToken(MigrationStage stage, CancellationToken _) => Install(stage);
    private static Task Install(MigrationStage stage)
    {
        File.WriteAllText(Path.Combine(stage.StageDirectory, stage.StageName + ".json"),
            "{\"id\":\"" + stage.StageName + "\",\"libraries\":[]}");
        File.WriteAllText(Path.Combine(stage.StageDirectory, stage.StageName + ".jar"), "core");
        return Task.CompletedTask;
    }

    private static MigrationSourceMod Source() => new()
    { Path = "source.jar", Sha256 = Hash("source"), Platform = "Modrinth", ProjectId = "project" };
    private static MigrationCandidate Candidate() => new()
    {
        Platform = "Modrinth", ProjectId = "project", FileId = "one", FileName = "target.jar", Hash = Hash("target"),
        MinecraftVersions = ["1.21.1"], Loaders = [MigrationLoader.NeoForge], Stable = true,
        PublishedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
