using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.InstanceMigration;

namespace PCL.Core.Test.Minecraft.InstanceMigration;

[TestClass]
public sealed class ModificationDependencyPlannerTests
{
    [TestMethod]
    public void DifferentOldSourcesMappingToOneTargetCannotQueueTheSameArtifactTwice()
    {
        var target = File("library", "same-version");
        var result = Plan([Root("old-v1.jar", target), Root("old-v2.jar", target)]);
        CollectionAssert.AreEqual(new[] { "old-v1.jar" }, result.AcceptedSources.ToArray());
        Assert.AreEqual("old-v2.jar", result.Rejected.Single().SourcePath);
        Assert.AreEqual(0, result.Downloads.Count);
    }

    [TestMethod]
    public void SelectedDependencyAfterConsumerIsNotDownloadedOrRequestedAgain()
    {
        var dependency = File("dependency", "chosen");
        var first = File("first", "v1", Ref("dependency", "chosen"));
        var second = File("second", "v1", Ref("dependency", "chosen"));
        var requests = 0;
        var result = ModificationDependencyPlanner.Plan([
            Root("first.jar", first), Root("second.jar", second), Root("dependency.jar", dependency)
        ], Minecraft, Loader, (_, _) =>
        {
            requests++;
            throw new InvalidOperationException("A chosen target is already available to the whole graph.");
        });
        Assert.AreEqual(3, result.AcceptedSources.Count);
        Assert.AreEqual(0, result.Downloads.Count);
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public void ManyModsRequestingOneUnselectedDependencyProduceOneDownload()
    {
        var dependency = File("shared");
        var roots = Enumerable.Range(0, 100).Select(index => Root("mod" + index + ".jar",
            File("mod" + index, "v1", Ref("shared")))).ToArray();
        var queries = 0;
        var result = ModificationDependencyPlanner.Plan(roots, Minecraft, Loader, (_, _) =>
        {
            queries++;
            return [dependency];
        });
        Assert.AreEqual(100, result.AcceptedSources.Count);
        Assert.AreEqual(1, result.Downloads.Count);
        Assert.AreEqual(1, queries);
    }

    private const string Minecraft = "26.2";
    private const string Loader = "NeoForge";
    private const string Platform = "Modrinth";

    private static ModificationDependencyFile File(string project, string id = "v1",
        params ModificationDependencyReference[] dependencies) => new()
    {
        Platform = Platform,
        ProjectId = project,
        FileId = id,
        FileName = project + "-" + id + ".jar",
        Hash = new string('a', 64),
        MinecraftVersions = [Minecraft],
        Loaders = [Loader],
        Dependencies = dependencies,
        Stable = true,
        PublishedAt = DateTimeOffset.UnixEpoch,
    };

    private static ModificationDependencyReference Ref(string project, string? file = null)
        => new(Platform, project, file);

    private static ModificationDependencyRoot Root(string path, ModificationDependencyFile file, bool disabled = false)
        => new(path, file, disabled);

    private static ModificationDependencyBatch Plan(IReadOnlyList<ModificationDependencyRoot> roots,
        params ModificationDependencyFile[] available)
        => ModificationDependencyPlanner.Plan(roots, Minecraft, Loader,
            (platform, project) => available.Where(file => file.Platform == platform && file.ProjectId == project).ToArray());

    [TestMethod]
    public void ReportedUniversalZoomifyDoesNotInventNeoForgeKotlinOrAbortIndependentMods()
    {
        // Offline reproduction of the supplied log: Zoomify D64cvU6G advertises both
        // loaders, but its required project Ha28R6CL has Fabric-only builds.
        var kotlin = File("Ha28R6CL", "fabric-kotlin") with { Loaders = ["Fabric"] };
        var zoomify = File("w7ThoJFB", "D64cvU6G", Ref("Ha28R6CL")) with
        {
            Loaders = ["Fabric", "NeoForge"],
            FileName = "zoomify-2.17.0+mc26.2-universal.jar",
        };
        var independent = File("independent");
        var result = Plan([Root("zoomify.jar", zoomify), Root("independent.jar", independent)], kotlin);

        CollectionAssert.AreEqual(new[] { "independent.jar" }, result.AcceptedSources.ToArray());
        Assert.AreEqual("zoomify.jar", result.Rejected.Single().SourcePath);
        StringAssert.Contains(result.Rejected[0].Reason, "Ha28R6CL");
        Assert.AreEqual(0, result.Downloads.Count, "A rejected root must not leak a partially resolved dependency download.");
    }

    [TestMethod]
    public void PinnedRequiredFileIsRespectedEvenWhenAnotherBuildIsNewer()
    {
        var requested = File("dependency", "old");
        var newest = File("dependency", "new") with { PublishedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        var root = File("root", "v1", Ref("dependency", "old"));
        var result = Plan([Root("root.jar", root)], newest, requested);
        Assert.AreEqual("old", result.Downloads.Single().FileId);
    }

    [TestMethod]
    public void MissingPinnedFileCannotFallBackToUnpinnedCompatibleBuild()
    {
        var root = File("root", "v1", Ref("dependency", "absent"));
        var result = Plan([Root("root.jar", root)], File("dependency", "different"));
        Assert.AreEqual(0, result.AcceptedSources.Count);
        Assert.AreEqual(0, result.Downloads.Count);
        StringAssert.Contains(result.Rejected.Single().Reason, "absent");
    }

    [TestMethod]
    public void ConflictingPinsInsideOneClosureRejectOnlyThatRoot()
    {
        var root = File("root", "v1", Ref("left"), Ref("right"));
        var left = File("left", "v1", Ref("shared", "one"));
        var right = File("right", "v1", Ref("shared", "two"));
        var result = Plan([Root("broken.jar", root), Root("good.jar", File("good"))],
            left, right, File("shared", "one"), File("shared", "two"));
        CollectionAssert.AreEqual(new[] { "good.jar" }, result.AcceptedSources.ToArray());
        Assert.AreEqual(0, result.Downloads.Count);
        Assert.AreEqual("broken.jar", result.Rejected.Single().SourcePath);
    }

    [TestMethod]
    public void DisabledRootIsPreservedWithoutLookingUpOrActivatingItsDependencies()
    {
        var disabled = Root("disabled.jar.disabled", File("disabled", "v1", Ref("missing")), disabled: true);
        var lookups = 0;
        var result = ModificationDependencyPlanner.Plan([disabled], Minecraft, Loader, (_, _) =>
        {
            lookups++;
            throw new InvalidOperationException("Disabled roots must not fetch dependencies.");
        });
        CollectionAssert.AreEqual(new[] { disabled.SourcePath }, result.AcceptedSources.ToArray());
        Assert.AreEqual(0, lookups);
        Assert.AreEqual(0, result.Downloads.Count);
    }

    [TestMethod]
    public void DisabledSelectedRootCannotSatisfyAnEnabledModsRequiredDependency()
    {
        var dependency = File("dependency");
        var result = Plan([
            Root("dependency.jar.disabled", dependency, disabled: true),
            Root("root.jar", File("root", "v1", Ref("dependency"))),
        ], dependency);
        Assert.AreEqual(2, result.AcceptedSources.Count);
        Assert.AreEqual("dependency", result.Downloads.Single().ProjectId,
            "An enabled required artifact is still needed even when the same build is stored disabled.");
    }

    [TestMethod]
    public void CompatibleDependencyCycleTerminatesAndDownloadsEachArtifactOnce()
    {
        var a = File("a", "v1", Ref("b"));
        var b = File("b", "v1", Ref("a"));
        var calls = new Dictionary<string, int>();
        var result = ModificationDependencyPlanner.Plan([Root("a.jar", a)], Minecraft, Loader, (_, project) =>
        {
            calls[project] = calls.GetValueOrDefault(project) + 1;
            return project == "b" ? [b] : [a];
        });
        Assert.AreEqual(1, result.AcceptedSources.Count);
        CollectionAssert.AreEqual(new[] { "b" }, result.Downloads.Select(file => file.ProjectId).ToArray());
        Assert.IsTrue(calls.Values.All(count => count == 1));
    }

    [TestMethod]
    public void ExcessiveDepthIsReportedAndDoesNotKeepPartialDownloads()
    {
        var result = ModificationDependencyPlanner.Plan(
            [Root("root.jar", File("p0", "v1", Ref("p1")))], Minecraft, Loader,
            (_, project) =>
            {
                var number = int.Parse(project[1..]);
                return [File(project, "v1", Ref("p" + (number + 1)))];
            });
        Assert.AreEqual(0, result.AcceptedSources.Count);
        Assert.AreEqual(0, result.Downloads.Count);
        StringAssert.Contains(result.Rejected.Single().Reason.ToLowerInvariant(), "limit");
    }

    [TestMethod]
    public void FailedDependencyLookupDoesNotEraseOtherRootsOrLeakPartialClosure()
    {
        var broken = File("broken", "v1", Ref("downloadable"), Ref("offline"));
        var result = ModificationDependencyPlanner.Plan(
            [Root("broken.jar", broken), Root("good.jar", File("good"))], Minecraft, Loader,
            (_, project) => project == "offline"
                ? throw new InvalidOperationException("network unavailable")
                : [File(project)]);
        CollectionAssert.AreEqual(new[] { "good.jar" }, result.AcceptedSources.ToArray());
        Assert.AreEqual(0, result.Downloads.Count);
        StringAssert.Contains(result.Rejected.Single().Reason, "offline");
    }

    [TestMethod]
    public void CancellationDuringLookupPropagatesInsteadOfBeingConvertedToAnUnresolvedMod()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => ModificationDependencyPlanner.Plan(
            [Root("root.jar", File("root", "v1", Ref("dependency")))], Minecraft, Loader,
            (_, _) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return [];
            }, cancellation.Token));
    }

    [TestMethod]
    public void AlreadyCancelledPlanDoesNotQueryAnyProject()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModificationDependencyPlanner.Plan(
            [Root("root.jar", File("root", "v1", Ref("dependency")))], Minecraft, Loader,
            (_, _) => throw new InvalidOperationException("Must not be queried."), cancellation.Token));
    }

    [TestMethod]
    public void DiamondGraphDeduplicatesSharedArtifactAndProjectQueries()
    {
        var root = File("root", "v1", Ref("left"), Ref("right"));
        var files = new[]
        {
            File("left", "v1", Ref("shared")),
            File("right", "v1", Ref("shared")),
            File("shared"),
        };
        var calls = new Dictionary<string, int>();
        var result = ModificationDependencyPlanner.Plan([Root("root.jar", root)], Minecraft, Loader, (_, project) =>
        {
            calls[project] = calls.GetValueOrDefault(project) + 1;
            return files.Where(file => file.ProjectId == project).ToArray();
        });
        Assert.AreEqual(3, result.Downloads.Count);
        Assert.AreEqual(1, result.Downloads.Count(file => file.ProjectId == "shared"));
        Assert.IsTrue(calls.Values.All(count => count == 1));
    }

    [TestMethod]
    public void SelectedRootCanSatisfyDependencyWithoutAnotherNetworkLookup()
    {
        var result = ModificationDependencyPlanner.Plan([
            Root("root.jar", File("root", "v1", Ref("dependency"))),
            Root("dependency.jar", File("dependency")),
        ], Minecraft, Loader, (_, _) => throw new InvalidOperationException("Selected build must be used."));
        Assert.AreEqual(2, result.AcceptedSources.Count);
        Assert.AreEqual(0, result.Downloads.Count);
    }

    [TestMethod]
    public void TwoAcceptedRootsShareOneCompatibleRequiredArtifact()
    {
        var result = Plan([
            Root("one.jar", File("one", "v1", Ref("shared"))),
            Root("two.jar", File("two", "v1", Ref("shared"))),
        ], File("shared"));
        Assert.AreEqual(2, result.AcceptedSources.Count);
        Assert.AreEqual(1, result.Downloads.Count);
    }

    [TestMethod]
    public void ConflictingSelectedVersionsDoNotSilentlyPickTheFirstForAnotherRoot()
    {
        var result = Plan([
            Root("one.jar", File("shared", "one")),
            Root("two.jar", File("shared", "two")),
            Root("consumer.jar", File("consumer", "v1", Ref("shared"))),
        ]);
        Assert.IsFalse(result.AcceptedSources.Contains("consumer.jar"));
        Assert.AreEqual(1, result.AcceptedSources.Count(path => path is "one.jar" or "two.jar"));
        Assert.AreEqual(0, result.Downloads.Count);
    }

    [TestMethod]
    public void RootAndDependencyArtifactPathsOrDigestsMustBeSafeBeforeTheyEnterDownloads()
    {
        var unsafeFiles = new[]
        {
            File("a") with { FileName = "../escape.jar" },
            File("a") with { FileName = @"..\escape.jar" },
            File("a") with { FileName = "a.jar " },
            File("a") with { Hash = "not-a-digest" },
        };
        foreach (var file in unsafeFiles)
        {
            var rootResult = Plan([Root("bad.jar", file)]);
            Assert.AreEqual(0, rootResult.AcceptedSources.Count);
            Assert.AreEqual(1, rootResult.Rejected.Count);
            Assert.AreEqual(0, rootResult.Downloads.Count);
            var dependencyResult = Plan([Root("root.jar", File("root", "v1", Ref("a")))], file);
            Assert.AreEqual(0, dependencyResult.AcceptedSources.Count);
            Assert.AreEqual(0, dependencyResult.Downloads.Count);
        }
    }

    [TestMethod]
    public void WrongMinecraftOrLoaderDoesNotBecomeCompatibleByFilename()
    {
        var wrongMinecraft = File("dependency") with { MinecraftVersions = ["1.21.9"] };
        var wrongLoader = File("dependency") with { Loaders = ["Fabric"] };
        foreach (var file in new[] { wrongMinecraft, wrongLoader })
        {
            var result = Plan([Root("root.jar", File("root", "v1", Ref("dependency")))], file);
            Assert.AreEqual(0, result.AcceptedSources.Count);
            Assert.AreEqual(0, result.Downloads.Count);
        }
    }

    [TestMethod]
    public void StableExactTargetBuildWinsOverNewerBetaOrWrongPlatform()
    {
        var stable = File("dependency", "stable");
        var beta = File("dependency", "beta") with { Stable = false, PublishedAt = DateTimeOffset.UnixEpoch.AddDays(5) };
        var otherPlatform = File("dependency", "other") with { Platform = "CurseForge", PublishedAt = DateTimeOffset.UnixEpoch.AddDays(10) };
        var result = Plan([Root("root.jar", File("root", "v1", Ref("dependency")))], beta, otherPlatform, stable);
        Assert.AreEqual("stable", result.Downloads.Single().FileId);
    }

    [TestMethod]
    public void CaseInsensitiveArtifactNameCollisionRejectsOnlyConflictingRoot()
    {
        var first = File("first") with { FileName = "shared.jar" };
        var second = File("second") with { FileName = "SHARED.jar", Hash = new string('b', 64) };
        var result = Plan([Root("first.jar", first), Root("second.jar", second)]);
        CollectionAssert.AreEqual(new[] { "first.jar" }, result.AcceptedSources.ToArray());
        Assert.AreEqual("second.jar", result.Rejected.Single().SourcePath);
        StringAssert.Contains(result.Rejected.Single().Reason.ToLowerInvariant(), "filename");
    }

    [TestMethod]
    public void DependencyFileNameCollisionCannotEscapeThroughDifferentProjectIds()
    {
        var firstDependency = File("one") with { FileName = "shared.jar" };
        var secondDependency = File("two") with { FileName = "shared.jar", Hash = new string('b', 64) };
        var result = Plan([
            Root("first.jar", File("first", "v1", Ref("one"))),
            Root("second.jar", File("second", "v1", Ref("two"))),
        ], firstDependency, secondDependency);
        CollectionAssert.AreEqual(new[] { "first.jar" }, result.AcceptedSources.ToArray());
        Assert.AreEqual("second.jar", result.Rejected.Single().SourcePath);
        CollectionAssert.AreEqual(new[] { "one" }, result.Downloads.Select(file => file.ProjectId).ToArray());
    }

    [TestMethod]
    public void RejectedRootThatAnotherRootRequiresDoesNotDisappearFromItsDependencyClosure()
    {
        var root = File("broken", "v1", Ref("missing"));
        var consumer = File("consumer", "v1", Ref("broken"));
        var result = Plan([Root("broken.jar", root), Root("consumer.jar", consumer)]);
        Assert.AreEqual(0, result.AcceptedSources.Count);
        Assert.AreEqual(2, result.Rejected.Count);
        Assert.AreEqual(0, result.Downloads.Count);
    }

    [TestMethod]
    public void PinnedDependencyCannotBeSatisfiedByDifferentSelectedRootVersion()
    {
        var result = Plan([
            Root("selected.jar", File("dependency", "selected")),
            Root("consumer.jar", File("consumer", "v1", Ref("dependency", "pinned"))),
        ], File("dependency", "pinned"));
        CollectionAssert.AreEqual(new[] { "selected.jar" }, result.AcceptedSources.ToArray());
        Assert.AreEqual("consumer.jar", result.Rejected.Single().SourcePath);
        Assert.AreEqual(0, result.Downloads.Count);
    }

    [TestMethod]
    public void InvalidInputCollectionsFailClearlyRatherThanNullDereferencing()
    {
        Assert.Throws<ArgumentNullException>(() => ModificationDependencyPlanner.Plan(null!, Minecraft, Loader, (_, _) => []));
        Assert.Throws<ArgumentNullException>(() => ModificationDependencyPlanner.Plan([], Minecraft, Loader, null!));
        Assert.Throws<ArgumentException>(() => Plan([null!]));
        Assert.Throws<ArgumentException>(() => Plan([Root("broken.jar", null!)]));
        Assert.Throws<ArgumentException>(() => ModificationDependencyPlanner.Plan([], "", Loader, (_, _) => []));
        Assert.Throws<ArgumentException>(() => ModificationDependencyPlanner.Plan([], Minecraft, "", (_, _) => []));
    }

    [TestMethod]
    public void MalformedRequiredReferenceIsRejectedBeforeQueryingUntrustedIdentity()
    {
        foreach (var reference in new[]
        {
            new ModificationDependencyReference("", "dependency"),
            new ModificationDependencyReference(Platform, ""),
            new ModificationDependencyReference("unrecognized", "dependency"),
        })
        {
            var queries = 0;
            var result = ModificationDependencyPlanner.Plan(
                [Root("root.jar", File("root", "v1", reference))], Minecraft, Loader,
                (_, _) => { queries++; return []; });
            Assert.AreEqual(0, queries);
            Assert.AreEqual(0, result.AcceptedSources.Count);
            Assert.AreEqual(1, result.Rejected.Count);
        }
    }

    [TestMethod]
    public void InvalidNewestDependencyArtifactDoesNotHideOlderValidCompatibleBuild()
    {
        var invalid = File("dependency", "new") with
        {
            PublishedAt = DateTimeOffset.UnixEpoch.AddDays(1),
            FileName = "../escape.jar",
        };
        var valid = File("dependency", "valid");
        var result = Plan([Root("root.jar", File("root", "v1", Ref("dependency")))], invalid, valid);
        Assert.AreEqual(1, result.AcceptedSources.Count);
        Assert.AreEqual("valid", result.Downloads.Single().FileId);
    }

    [TestMethod]
    public void NullOrMalformedResolverResultsAreReportedWithoutCrashingTheBatch()
    {
        foreach (var response in new IReadOnlyList<ModificationDependencyFile>?[] { null, [null!] })
        {
            var result = ModificationDependencyPlanner.Plan([
                Root("dependent.jar", File("dependent", "v1", Ref("missing"))),
                Root("independent.jar", File("independent")),
            ], Minecraft, Loader, (_, _) => response!);
            CollectionAssert.AreEqual(new[] { "independent.jar" }, result.AcceptedSources.ToArray());
            Assert.AreEqual("dependent.jar", result.Rejected.Single().SourcePath);
            Assert.AreEqual(0, result.Downloads.Count);
        }
    }
}
