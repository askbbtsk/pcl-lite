using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Worlds;

namespace PCL.Core.Test.Minecraft.Worlds;

[TestClass]
public sealed class RecentWorldHistoryTest
{
    private string _root = null!;
    private string _file = null!;
    private string _instance = null!;
    private string _game = null!;
    private string _world = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLRecentWorlds", Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_root, "history.json");
        _instance = Path.Combine(_root, "versions", "modpack");
        _game = _instance;
        _world = Path.Combine(_game, "saves", "Last World");
        Directory.CreateDirectory(_world);
        File.WriteAllText(Path.Combine(_world, "level.dat"), "fixture");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void StartedAndCompletedWorldIsPersistedAndQueryableByInstance()
    {
        var history = new RecentWorldHistory(_file);
        var session = history.StartSession(_instance, _game, _world, "Last World", "1.21.4", DateTime.UnixEpoch);
        Assert.AreEqual(WorldSessionOutcome.Running, history.MostRecent(_instance)!.Outcome);
        Assert.IsTrue(history.FinishSession(session.SessionId, completedNormally: true, DateTime.UnixEpoch.AddMinutes(10)));

        var reloaded = new RecentWorldHistory(_file);
        var mostRecent = reloaded.MostRecent(_instance)!;
        Assert.AreEqual("Last World", mostRecent.WorldName);
        Assert.AreEqual("1.21.4", mostRecent.MinecraftVersion);
        Assert.AreEqual(WorldSessionOutcome.Completed, mostRecent.Outcome);
        Assert.AreEqual(DateTime.UnixEpoch.AddMinutes(10), mostRecent.EndedAtUtc);
    }

    [TestMethod]
    public void StartupRecoveryMarksAbandonedSessionsInterrupted()
    {
        var history = new RecentWorldHistory(_file);
        history.StartSession(_instance, _game, _world, "Last World", nowUtc: DateTime.UnixEpoch);

        var recovered = new RecentWorldHistory(_file);
        Assert.AreEqual(1, recovered.RecoverInterruptedSessions(DateTime.UnixEpoch.AddMinutes(4)));
        Assert.AreEqual(WorldSessionOutcome.Interrupted, recovered.MostRecent()!.Outcome);
        Assert.AreEqual(0, recovered.RecoverInterruptedSessions(DateTime.UnixEpoch.AddMinutes(5)));
    }

    [TestMethod]
    public void WorldOutsideInstanceSavesFolderIsRejected()
    {
        var history = new RecentWorldHistory(_file);
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Assert.ThrowsExactly<InvalidDataException>(() => history.StartSession(_instance, _game, outside, "outside"));
    }

    [TestMethod]
    public void RepeatedWorldLaunchMovesRecordToMostRecentAndBoundsHistory()
    {
        var history = new RecentWorldHistory(_file);
        var first = history.StartSession(_instance, _game, _world, "Old title", nowUtc: DateTime.UnixEpoch);
        history.FinishSession(first.SessionId, completedNormally: true, DateTime.UnixEpoch.AddMinutes(30));
        history.StartSession(_instance, _game, _world, "Renamed world", nowUtc: DateTime.UnixEpoch.AddHours(1));

        var entries = history.List(_instance);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("Renamed world", entries[0].WorldName);
    }
}
