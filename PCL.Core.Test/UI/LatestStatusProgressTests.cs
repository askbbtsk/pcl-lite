using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class LatestStatusProgressTests
{
    [TestMethod]
    public void FloodKeepsOnlyTheLatestMessageAndConsumptionClearsIt()
    {
        var progress = new LatestStatusProgress();
        Assert.IsNull(progress.TakeLatest());
        for (int i = 0; i < 100_000; i++) progress.Report(i.ToString());
        Assert.AreEqual("99999", progress.TakeLatest());
        Assert.IsNull(progress.TakeLatest());
    }

    [TestMethod]
    public void ParallelWorkersPublishWithoutUsingTheUiSynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        var context = new CountingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var progress = new LatestStatusProgress();
            progress.Report("from UI");
            Parallel.For(0, 10_000, i => progress.Report(i.ToString()));
            progress.Report("finished");
            Assert.AreEqual("finished", progress.TakeLatest());
            Assert.AreEqual(0, context.Posts);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [TestMethod]
    public async Task ConcurrentConsumptionDoesNotLoseTheFinalPublishedMessage()
    {
        var progress = new LatestStatusProgress();
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < 100_000; i++) progress.Report(i.ToString());
        });
        while (!producer.IsCompleted)
        {
            progress.TakeLatest();
            await Task.Yield();
        }
        await producer;
        progress.Report("finished");
        Assert.AreEqual("finished", progress.TakeLatest());
        Assert.IsNull(progress.TakeLatest());
    }

    private sealed class CountingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
            => Interlocked.Increment(ref Posts);
    }
}
