using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils;

namespace PCL.Core.Test.Utils;

[TestClass]
public sealed class StagedOperationProgressTests
{
    [TestMethod]
    public void InvalidStageCallbacksAreRejectedEvenBeforeBegin()
    {
        var progress = new StagedOperationProgress(1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Report(-1, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Complete(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Report(2, 0.5));
        Assert.AreEqual(0d, progress.Overall);
    }

    // Install, candidate query, dependency preflight, transfer, verification and live commit.
    private static StagedOperationProgress Migration() => new(30, 15, 15, 25, 10, 5);

    [TestMethod]
    public void CompletingInstallationDoesNotPretendTheWholeMigrationFinished()
    {
        var progress = Migration();
        Assert.AreEqual(0d, progress.Overall);
        progress.Begin(0);
        progress.Report(0, 0.5);
        Assert.AreEqual(0.15, progress.Overall, 0.000001);
        progress.Complete(0);
        Assert.AreEqual(0.30, progress.Overall, 0.000001);
        progress.Begin(1);
        progress.Report(1, 0.5);
        Assert.AreEqual(0.375, progress.Overall, 0.000001);
        Assert.IsTrue(progress.Overall < 1);
    }

    [TestMethod]
    public void EveryPhaseIncludingFinalLiveCommitMustFinishBeforeOneHundredPercent()
    {
        var progress = Migration();
        for (var stage = 0; stage < 5; stage++)
        {
            progress.Begin(stage);
            progress.Complete(stage);
            Assert.IsTrue(progress.Overall < 1, "Earlier phases cannot complete the live commit.");
        }

        Assert.AreEqual(0.95, progress.Overall, 0.000001);
        progress.Begin(5);
        progress.Report(5, 0.5);
        Assert.AreEqual(0.975, progress.Overall, 0.000001);
        progress.Complete(5);
        Assert.AreEqual(1d, progress.Overall, 0.000001);
    }

    [TestMethod]
    public void ReusedChildDownloadResetCannotMoveTheMigrationBarBackwards()
    {
        var progress = new StagedOperationProgress(25, 75);
        progress.Begin(0);
        progress.Complete(0);
        progress.Begin(1);
        // The transfer owner reports its aggregate; a late lower child report cannot reset it.
        progress.Report(1, 0.8);
        var beforeRestart = progress.Overall;
        progress.Report(1, 0.1);
        progress.Report(1, 0);
        Assert.AreEqual(beforeRestart, progress.Overall, 0.000001);
        progress.Report(1, 0.9);
        Assert.AreEqual(0.925, progress.Overall, 0.000001);
    }

    [TestMethod]
    public void DelayedInstallerCallbacksCannotWriteIntoTheDependencyOrDownloadPhase()
    {
        var progress = Migration();
        progress.Begin(0);
        progress.Complete(0);
        progress.Begin(1);
        progress.Report(1, 0.4);
        var expected = progress.Overall;
        progress.Report(0, 0);
        progress.Report(0, 0.75);
        progress.Complete(0);
        progress.Report(4, 1); // Future verification has not actually happened.
        Assert.AreEqual(expected, progress.Overall, 0.000001);
    }

    [TestMethod]
    public void BeginningAnUnfinishedNextPhaseIsRejectedWithoutLosingCurrentProgress()
    {
        var progress = Migration();
        progress.Begin(0);
        progress.Report(0, 0.4);
        var expected = progress.Overall;
        Assert.ThrowsExactly<InvalidOperationException>(() => progress.Begin(1));
        Assert.AreEqual(expected, progress.Overall, 0.000001);
        progress.Complete(0);
        progress.Begin(1);
    }

    [TestMethod]
    public void PhasesCannotBeSkippedReversedOrStartedOutsideTheDeclaredWorkflow()
    {
        var progress = Migration();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => progress.Begin(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => progress.Begin(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => progress.Begin(6));
        progress.Begin(0);
        progress.Complete(0);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => progress.Begin(2));
        progress.Begin(1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => progress.Begin(0));
    }

    [TestMethod]
    public void RepeatedBeginNotificationDoesNotResetAnActivePhase()
    {
        var progress = Migration();
        progress.Begin(0);
        progress.Report(0, 0.75);
        var expected = progress.Overall;
        progress.Begin(0);
        progress.Report(0, 0.1);
        Assert.AreEqual(expected, progress.Overall, 0.000001);
    }

    [TestMethod]
    public void CancelledTransferRemainsPartialInsteadOfClaimingSuccessfulCommit()
    {
        var progress = Migration();
        for (var stage = 0; stage < 3; stage++)
        {
            progress.Begin(stage);
            progress.Complete(stage);
        }
        progress.Begin(3);
        progress.Report(3, 0.6);
        var atCancellation = progress.Overall;
        // Cancellation is owned by the operation: it must not call Complete on failure.
        progress.Report(2, 1);
        Assert.AreEqual(0.75, atCancellation, 0.000001);
        Assert.AreEqual(atCancellation, progress.Overall, 0.000001);
        Assert.IsTrue(progress.Overall < 1);
    }

    [TestMethod]
    public void EmptyOptionalPhaseCanBeExplicitlyCompletedWithoutInventingTransferProgress()
    {
        var progress = Migration();
        progress.Begin(0);
        progress.Complete(0);
        progress.Begin(1);
        progress.Complete(1);
        progress.Begin(2);
        progress.Complete(2); // No mod dependencies were selected.
        Assert.AreEqual(0.60, progress.Overall, 0.000001);
        progress.Begin(3);
        Assert.AreEqual(0.60, progress.Overall, 0.000001);
        progress.Complete(3); // No payloads are needed either.
        Assert.AreEqual(0.85, progress.Overall, 0.000001);
    }

    [DataTestMethod]
    [DataRow(0d)]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void InvalidStageWeightIsRejected(double weight)
        => Assert.ThrowsExactly<ArgumentException>(() => new StagedOperationProgress(10, weight, 20));

    [TestMethod]
    public void EmptyNullAndOverflowingWeightSetsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new StagedOperationProgress());
        Assert.ThrowsExactly<ArgumentNullException>(() => new StagedOperationProgress(null!));
        Assert.ThrowsExactly<ArgumentException>(() => new StagedOperationProgress(double.MaxValue, double.MaxValue));
    }

    [DataTestMethod]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity)]
    public void NonFiniteWorkerProgressCannotContaminateTheTaskCard(double value)
    {
        var progress = Migration();
        progress.Begin(0);
        progress.Report(0, 0.5);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => progress.Report(0, value));
        Assert.AreEqual(0.15, progress.Overall, 0.000001);
    }

    [TestMethod]
    public void FiniteOutOfRangeWorkerValuesAreClamped()
    {
        var progress = new StagedOperationProgress(1, 3);
        progress.Begin(0);
        progress.Report(0, -100);
        Assert.AreEqual(0d, progress.Overall);
        progress.Report(0, 100);
        Assert.AreEqual(0.25, progress.Overall, 0.000001);
        progress.Begin(1);
        progress.Report(1, -1);
        Assert.AreEqual(0.25, progress.Overall, 0.000001);
    }

    [TestMethod]
    public async Task ConcurrentWorkersKeepTheHighestAggregateAndProduceAFiniteResult()
    {
        var progress = new StagedOperationProgress(20, 80);
        progress.Begin(0);
        progress.Complete(0);
        progress.Begin(1);
        await Task.WhenAll(Enumerable.Range(1, 500).Select(index =>
            Task.Run(() => progress.Report(1, index / 1000d))));
        Assert.AreEqual(0.6, progress.Overall, 0.000001);
        Assert.IsTrue(double.IsFinite(progress.Overall));
        progress.Report(1, 0.01);
        Assert.AreEqual(0.6, progress.Overall, 0.000001);
    }

    [TestMethod]
    public void CallerCannotChangeWorkflowWeightsAfterConstruction()
    {
        var weights = new[] { 20d, 80d };
        var progress = new StagedOperationProgress(weights);
        weights[0] = double.PositiveInfinity;
        weights[1] = 0;
        progress.Begin(0);
        progress.Complete(0);
        Assert.AreEqual(0.2, progress.Overall, 0.000001);
    }
}
