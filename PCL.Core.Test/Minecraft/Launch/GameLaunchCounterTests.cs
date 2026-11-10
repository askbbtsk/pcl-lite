using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launch;

namespace PCL.Core.Test.Minecraft.Launch;

[TestClass]
public sealed class GameLaunchCounterTests
{
    [TestMethod]
    public void JavaBedrockAndLunarExtendTheExistingValueRatherThanResettingIt()
    {
        var stored = 437;
        var counter = new GameLaunchCounter(() => stored, value => stored = value);
        Assert.IsTrue(counter.RecordSuccess("java-process:100:1"));
        Assert.IsTrue(counter.RecordSuccess("bedrock-process:200:2"));
        Assert.IsTrue(counter.RecordSuccess("lunar-process:300:3"));
        Assert.AreEqual(440, stored);
    }

    [TestMethod]
    public void DuplicateSuccessCallbacksAndReactivatingTheSameProcessAreCountedOnce()
    {
        var stored = 5;
        var counter = new GameLaunchCounter(() => stored, value => stored = value);
        Assert.IsTrue(counter.RecordSuccess("200:638998000000000000"));
        Assert.IsFalse(counter.RecordSuccess("200:638998000000000000"));
        Assert.IsFalse(counter.RecordSuccess("200:638998000000000000"));
        Assert.AreEqual(6, stored);
    }

    [TestMethod]
    public void ReusedProcessIdWithANewStartTimeRepresentsANewLaunch()
    {
        var stored = 10;
        var counter = new GameLaunchCounter(() => stored, value => stored = value);
        Assert.IsTrue(counter.RecordSuccess("200:638998000000000000"));
        Assert.IsTrue(counter.RecordSuccess("200:638998100000000000"));
        Assert.AreEqual(12, stored);
    }

    [TestMethod]
    public async Task ConcurrentEditionSuccessesCannotLoseIncrementsOrDoubleCount()
    {
        var stored = 29;
        var counter = new GameLaunchCounter(() => stored, value => stored = value);
        var counted = 0;
        await Task.WhenAll(Enumerable.Range(0, 400).Select(index => Task.Run(() =>
        {
            if (counter.RecordSuccess("process:" + index % 100)) Interlocked.Increment(ref counted);
        })));
        Assert.AreEqual(100, counted);
        Assert.AreEqual(129, stored);
    }

    [TestMethod]
    public void FailedPersistenceDoesNotConsumeTheSuccessfulProcessIdentity()
    {
        var stored = 8;
        var fail = true;
        var counter = new GameLaunchCounter(() => stored, value =>
        {
            if (fail) throw new IOException("temporary config write failure");
            stored = value;
        });
        Assert.ThrowsExactly<IOException>(() => counter.RecordSuccess("game:1"));
        Assert.AreEqual(8, stored);
        fail = false;
        Assert.IsTrue(counter.RecordSuccess("game:1"));
        Assert.IsFalse(counter.RecordSuccess("game:1"));
        Assert.AreEqual(9, stored);
    }

    [TestMethod]
    public void CounterReadsTheLatestPersistedValueBeforeEachDistinctSuccess()
    {
        var stored = 20;
        var counter = new GameLaunchCounter(() => stored, value => stored = value);
        counter.RecordSuccess("game:1");
        stored = 90; // Settings migration/user restoration happened after the previous launch.
        counter.RecordSuccess("game:2");
        Assert.AreEqual(91, stored);
    }

    [TestMethod]
    public void FailedReadCanBeRetriedWithoutLosingOrInventingLaunches()
    {
        var stored = 12;
        var fail = true;
        var counter = new GameLaunchCounter(() => fail ? throw new IOException("read failure") : stored, value => stored = value);
        Assert.ThrowsExactly<IOException>(() => counter.RecordSuccess("game:1"));
        Assert.AreEqual(12, stored);
        fail = false;
        Assert.IsTrue(counter.RecordSuccess("game:1"));
        Assert.AreEqual(13, stored);
    }

    [TestMethod]
    public void MaximumPersistedCountNeverWrapsNegative()
    {
        var stored = int.MaxValue;
        var writes = 0;
        var counter = new GameLaunchCounter(() => stored, value => { writes++; stored = value; });
        Assert.IsTrue(counter.RecordSuccess("game:1"));
        Assert.IsFalse(counter.RecordSuccess("game:1"));
        Assert.AreEqual(int.MaxValue, stored);
        Assert.AreEqual(0, writes);
    }

    [TestMethod]
    public void InvalidSuccessIdentityNeverReadsOrChangesTheStoredValue()
    {
        var counter = new GameLaunchCounter(() => throw new AssertFailedException("Invalid identities must be rejected before reading."),
            _ => Assert.Fail("Invalid identities cannot write."));
        Assert.ThrowsExactly<ArgumentNullException>(() => counter.RecordSuccess(null!));
        Assert.ThrowsExactly<ArgumentException>(() => counter.RecordSuccess(""));
        Assert.ThrowsExactly<ArgumentException>(() => counter.RecordSuccess(" "));
    }
}
