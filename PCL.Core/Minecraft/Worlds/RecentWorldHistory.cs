using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PCL.Core.App;

namespace PCL.Core.Minecraft.Worlds;

public enum WorldSessionOutcome
{
    Running,
    Completed,
    Crashed,
    Interrupted,
}

public sealed record RecentWorldEntry
{
    public required string SessionId { get; init; }
    public required string InstancePath { get; init; }
    public required string WorldPath { get; init; }
    public required string WorldName { get; init; }
    public string? MinecraftVersion { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime? EndedAtUtc { get; init; }
    public WorldSessionOutcome Outcome { get; init; }
}

public sealed class RecentWorldHistory
{
    private const int MaximumEntries = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object ProcessGate = new();
    private readonly string _filePath;
    private readonly object _gate = ProcessGate;

    public static string DefaultFilePath => Path.Combine(Paths.SharedData, "recent-worlds.json");
    public static event Action? Changed;

    public RecentWorldHistory() : this(DefaultFilePath) { }

    public RecentWorldHistory(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public RecentWorldEntry StartSession(string instancePath, string gameRoot, string worldPath, string worldName,
        string? minecraftVersion = null, DateTime? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        var normalizedWorld = ValidateWorldPath(gameRoot, worldPath);
        var entry = new RecentWorldEntry
        {
            SessionId = Guid.NewGuid().ToString("N"),
            InstancePath = Path.GetFullPath(instancePath),
            WorldPath = normalizedWorld,
            WorldName = worldName.Trim(),
            MinecraftVersion = minecraftVersion,
            StartedAtUtc = (nowUtc ?? DateTime.UtcNow).ToUniversalTime(),
            Outcome = WorldSessionOutcome.Running,
        };
        lock (_gate)
        {
            var entries = ReadUnsafe();
            entries.RemoveAll(item => PathsEqual(item.WorldPath, entry.WorldPath) && item.Outcome != WorldSessionOutcome.Running);
            entries.Add(entry);
            WriteUnsafe(entries);
        }
        return entry;
    }

    public bool FinishSession(string sessionId, bool completedNormally, DateTime? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            var entries = ReadUnsafe();
            var index = entries.FindIndex(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
            if (index < 0) return false;
            var current = entries[index];
            if (current.Outcome != WorldSessionOutcome.Running) return false;
            entries[index] = current with
            {
                Outcome = completedNormally ? WorldSessionOutcome.Completed : WorldSessionOutcome.Crashed,
                EndedAtUtc = (nowUtc ?? DateTime.UtcNow).ToUniversalTime(),
            };
            WriteUnsafe(entries);
            return true;
        }
    }

    /// <summary>Marks sessions left in Running state by a previous process as interrupted.</summary>
    public int RecoverInterruptedSessions(DateTime? nowUtc = null)
    {
        lock (_gate)
        {
            var entries = ReadUnsafe();
            var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
            var changed = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].Outcome != WorldSessionOutcome.Running) continue;
                entries[index] = entries[index] with { Outcome = WorldSessionOutcome.Interrupted, EndedAtUtc = now };
                changed++;
            }
            if (changed > 0) WriteUnsafe(entries);
            return changed;
        }
    }

    public IReadOnlyList<RecentWorldEntry> List(string? instancePath = null)
    {
        lock (_gate)
        {
            var entries = ReadUnsafe();
            if (!string.IsNullOrWhiteSpace(instancePath))
            {
                var normalizedInstance = Path.GetFullPath(instancePath);
                entries = entries.Where(item => PathsEqual(item.InstancePath, normalizedInstance)).ToList();
            }
            return entries.OrderByDescending(static item => item.StartedAtUtc).ToArray();
        }
    }

    public RecentWorldEntry? MostRecent(string? instancePath = null)
        => List(instancePath).FirstOrDefault();

    private List<RecentWorldEntry> ReadUnsafe()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<RecentWorldEntry>>(File.ReadAllText(_filePath), JsonOptions) ?? [];
            if (entries.Any(static item => string.IsNullOrWhiteSpace(item.SessionId) || string.IsNullOrWhiteSpace(item.WorldPath)))
                throw new InvalidDataException("Recent-world history contains an invalid entry.");
            return entries.OrderByDescending(static item => item.StartedAtUtc).Take(MaximumEntries).ToList();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Recent-world history could not be parsed.", ex);
        }
    }

    private void WriteUnsafe(List<RecentWorldEntry> entries)
    {
        var fullPath = Path.GetFullPath(_filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bounded = entries.OrderByDescending(static item => item.StartedAtUtc).Take(MaximumEntries).ToArray();
            File.WriteAllText(tempPath, JsonSerializer.Serialize(bounded, JsonOptions));
            File.Move(tempPath, fullPath, overwrite: true);
            try { Changed?.Invoke(); }
            catch { /* observers cannot invalidate the persisted history */ }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static string ValidateWorldPath(string gameRoot, string worldPath)
    {
        var root = Path.GetFullPath(gameRoot);
        var savesRoot = Path.GetFullPath(Path.Combine(root, "saves"));
        var world = Path.GetFullPath(worldPath);
        var prefix = savesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!world.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(world))
            throw new InvalidDataException("Recent world path must identify a directory under the instance saves folder.");
        var current = world;
        while (!string.Equals(current, savesRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Recent-world history refuses paths through reparse points.");
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Invalid saves path.");
        }
        return world;
    }

    private static bool PathsEqual(string first, string second)
        => string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
