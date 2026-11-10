using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Resources;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class ModIndexingPolicyTests
{
    private const long WorkerMemory = 512L * 1024 * 1024;
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "PCLIndexPolicy", Guid.NewGuid().ToString("N"));

    private static string InstancePath(string name)
        => Path.Combine(TestRoot, name);

    [TestMethod]
    public void WorkerCountRequiresBothCpuAndMemoryCapacityAndAlwaysKeepsOneWorker()
    {
        var cases = new (int Processors, long AvailableMemory, int Expected)[]
        {
            (1, 32 * WorkerMemory, 1),
            (2, 32 * WorkerMemory, 2),
            (32, WorkerMemory, 1),
            (32, 2 * WorkerMemory, 2),
            (4, 8 * WorkerMemory, 4),
            (8, 4 * WorkerMemory, 4),
            (32, 32 * WorkerMemory, 8),
            (0, 32 * WorkerMemory, 1),
            (-1, 32 * WorkerMemory, 1),
            (int.MinValue, long.MaxValue, 1),
            (int.MaxValue, long.MaxValue, 8),
            (8, 1, 1),
            (8, WorkerMemory - 1, 1),
            (8, WorkerMemory, 1),
            (8, 2 * WorkerMemory - 1, 1),
            (8, 2 * WorkerMemory, 2),
            (8, 8 * WorkerMemory - 1, 7),
            (8, 8 * WorkerMemory, 8),
        };
        foreach (var item in cases)
            Assert.AreEqual(item.Expected, ModIndexingPolicy.GetWorkerCount(item.Processors, item.AvailableMemory),
                $"CPU {item.Processors}, available memory {item.AvailableMemory}");
    }

    [TestMethod]
    public void UnknownMemoryUsesConservativeTwoWorkerLimitWithoutIgnoringCpuBound()
    {
        foreach (var unavailable in new[] { 0L, -1L, long.MinValue })
        {
            Assert.AreEqual(1, ModIndexingPolicy.GetWorkerCount(1, unavailable));
            Assert.AreEqual(2, ModIndexingPolicy.GetWorkerCount(2, unavailable));
            Assert.AreEqual(2, ModIndexingPolicy.GetWorkerCount(64, unavailable));
        }
    }

    [TestMethod]
    public void BrowsingPromptingAndResettingCannotSpontaneouslyRequestAnIndexBuild()
    {
        var policy = new ModIndexingPolicy();
        var path = InstancePath("NoDownload");
        Assert.IsFalse(policy.ConsumeDownloadRequest(path));
        Assert.IsTrue(policy.ShouldPrompt(path, hasPending: true, promptEnabled: true));
        Assert.IsFalse(policy.ConsumeDownloadRequest(path));
        policy.AllowPromptAgain(path);
        Assert.IsFalse(policy.ConsumeDownloadRequest(path));
        Assert.IsTrue(policy.ShouldPrompt(path, hasPending: true, promptEnabled: true));
        Assert.IsFalse(policy.ConsumeDownloadRequest(path));
    }

    [TestMethod]
    public void CompletedDownloadsCoalesceToOneRequestForNormalizedCaseInsensitiveDirectory()
    {
        var policy = new ModIndexingPolicy();
        var path = InstancePath("Current");
        var equivalent = Path.Combine(path, "temporary", "..") + Path.DirectorySeparatorChar;
        policy.DownloadCompleted(path);
        policy.DownloadCompleted(equivalent);
        policy.DownloadCompleted(path.ToUpperInvariant());

        Assert.IsTrue(policy.ConsumeDownloadRequest(equivalent.ToUpperInvariant()));
        Assert.IsFalse(policy.ConsumeDownloadRequest(path));
        Assert.IsFalse(policy.ConsumeDownloadRequest(equivalent));
        Assert.IsFalse(Directory.Exists(path), "Policy bookkeeping must not create the instance directory.");
    }

    [TestMethod]
    public void DownloadRequestIsIndependentPerInstanceAndCanBeRequestedAgainAfterConsumption()
    {
        var policy = new ModIndexingPolicy();
        var first = InstancePath("First");
        var second = InstancePath("Second");
        policy.DownloadCompleted(first);
        policy.DownloadCompleted(second);
        Assert.IsTrue(policy.ConsumeDownloadRequest(first));
        Assert.IsFalse(policy.ConsumeDownloadRequest(first));
        Assert.IsTrue(policy.ConsumeDownloadRequest(second));
        policy.DownloadCompleted(first);
        Assert.IsTrue(policy.ConsumeDownloadRequest(first));
        Assert.IsFalse(policy.ConsumeDownloadRequest(first));
    }

    [TestMethod]
    public void ParallelConsumersCanClaimACompletedDownloadOnlyOnce()
    {
        var policy = new ModIndexingPolicy();
        var path = InstancePath("SharedDownload");
        Parallel.For(0, 256, iteration =>
            policy.DownloadCompleted(iteration % 2 == 0 ? path : path.ToUpperInvariant()));
        var claimed = 0;
        Parallel.For(0, 256, iteration =>
        {
            if (policy.ConsumeDownloadRequest(iteration % 2 == 0 ? path : path.ToUpperInvariant()))
                Interlocked.Increment(ref claimed);
        });
        Assert.AreEqual(1, claimed);
        Assert.IsFalse(policy.ConsumeDownloadRequest(path));
    }

    [TestMethod]
    public void DisabledOrUnnecessaryPromptDoesNotUseTheInstancesPromptAllowance()
    {
        var policy = new ModIndexingPolicy();
        var path = InstancePath("Pending");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Assert.IsFalse(policy.ShouldPrompt(path, hasPending: true, promptEnabled: false));
            Assert.IsFalse(policy.ShouldPrompt(path, hasPending: false, promptEnabled: true));
            Assert.IsFalse(policy.ShouldPrompt(path, hasPending: false, promptEnabled: false));
        }
        Assert.IsTrue(policy.ShouldPrompt(path, hasPending: true, promptEnabled: true));
        Assert.IsFalse(policy.ShouldPrompt(path, hasPending: true, promptEnabled: true));
    }

    [TestMethod]
    public void EligiblePromptDeduplicatesAliasesButKeepsSeparateInstancesIndependent()
    {
        var policy = new ModIndexingPolicy();
        var first = InstancePath("PromptFirst");
        var alias = Path.Combine(first, "child", "..") + Path.DirectorySeparatorChar;
        var second = InstancePath("PromptSecond");
        Assert.IsTrue(policy.ShouldPrompt(first, true, true));
        Assert.IsFalse(policy.ShouldPrompt(alias.ToUpperInvariant(), true, true));
        Assert.IsTrue(policy.ShouldPrompt(second, true, true));
        Assert.IsFalse(policy.ShouldPrompt(second, true, true));
        policy.AllowPromptAgain(alias.ToUpperInvariant());
        Assert.IsTrue(policy.ShouldPrompt(first, true, true));
        Assert.IsFalse(policy.ShouldPrompt(second, true, true),
            "Resetting one instance must not reset all prompt history.");
    }

    [TestMethod]
    public void ParallelEligiblePromptsProduceOneWinnerAndResetAllowsExactlyOneMore()
    {
        var policy = new ModIndexingPolicy();
        var path = InstancePath("ConcurrentPrompt");
        for (var round = 0; round < 3; round++)
        {
            var shown = 0;
            Parallel.For(0, 256, iteration =>
            {
                var candidate = iteration % 2 == 0 ? path : path.ToUpperInvariant();
                if (policy.ShouldPrompt(candidate, true, true)) Interlocked.Increment(ref shown);
            });
            Assert.AreEqual(1, shown);
            policy.AllowPromptAgain(path);
        }
    }

    [TestMethod]
    public void PromptBookkeepingDoesNotConsumeDownloadRequestAndDownloadDoesNotResetPromptHistory()
    {
        var policy = new ModIndexingPolicy();
        var path = InstancePath("IndependentReasons");
        policy.DownloadCompleted(path);
        Assert.IsTrue(policy.ShouldPrompt(path, true, true));
        Assert.IsFalse(policy.ShouldPrompt(path, true, true));
        policy.AllowPromptAgain(path);
        Assert.IsTrue(policy.ConsumeDownloadRequest(path));
        Assert.IsTrue(policy.ShouldPrompt(path, true, true));
        policy.DownloadCompleted(path);
        Assert.IsFalse(policy.ShouldPrompt(path, true, true));
        Assert.IsTrue(policy.ConsumeDownloadRequest(path));
    }

    [TestMethod]
    public void PrefixRelatedPathsAreNotMistakenForOneInstance()
    {
        var policy = new ModIndexingPolicy();
        var parent = InstancePath("Version");
        var nested = Path.Combine(parent, "Other");
        var prefixed = InstancePath("Version2");
        foreach (var path in new[] { parent, nested, prefixed })
        {
            policy.DownloadCompleted(path);
            Assert.IsTrue(policy.ShouldPrompt(path, true, true));
        }
        foreach (var path in new[] { parent, nested, prefixed })
        {
            Assert.IsTrue(policy.ConsumeDownloadRequest(path));
            Assert.IsFalse(policy.ConsumeDownloadRequest(path));
            Assert.IsFalse(policy.ShouldPrompt(path, true, true));
        }
    }
}
