using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCL.Core.Minecraft.PersonalOptions;

public enum PersonalOptionsApplyChoice { Skip, PreferInstance, PreferShared }

public sealed record PersonalOptionsDifference(string Key, string? Current, string? Instance, string? Shared);

public sealed record PersonalOptionsChangePreview(IReadOnlyList<PersonalOptionsDifference> Differences,
    bool HasConflict, bool HasInstanceValues, bool HasSharedValues);

public sealed record PersonalOptionsMemoryRequest(string InstanceDirectory, string GameDirectory,
    string SharedProfilePath, bool RememberInstance, PersonalOptionCategories SharedCategories,
    bool TargetUsesModernKeyNames, bool FovIsDegrees = true);

/// <summary>Versioned optional sidecar. The owner key prevents a copied directory from adopting another instance's memory.</summary>
public sealed class InstancePersonalOptionsDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string OwnerKey { get; init; } = "";
    public PersonalOptionsProfile Profile { get; init; } = new();
}

/// <summary>Coordinates one preview and one write for per-instance memory and the existing shared profile.</summary>
public static class InstancePersonalOptionsMemory
{
    private const PersonalOptionCategories Remembered = PersonalOptionCategories.KeyBindings | PersonalOptionCategories.Mouse;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string GetMemoryPath(string instanceDirectory)
    {
        var owner = OwnerKey(instanceDirectory);
        return Path.Combine(Path.GetFullPath(instanceDirectory), "PCL", "Enhancements", "PersonalOptions", owner + ".json");
    }

    private static string OwnerKey(string instanceDirectory)
    {
        var path = Path.GetFullPath(instanceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
    }

    public static PersonalOptionsRunSession? Prepare(PersonalOptionsMemoryRequest request,
        Func<PersonalOptionsChangePreview, PersonalOptionsApplyChoice> choose,
        Func<bool> isGameRunning, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(choose);
        ArgumentNullException.ThrowIfNull(isGameRunning);
        if (!request.RememberInstance && request.SharedCategories == PersonalOptionCategories.None) return null;
        var optionsPath = Path.Combine(Path.GetFullPath(request.GameDirectory), "options.txt");
        if (isGameRunning()) { report?.Invoke("Options are in use by a running game; writes skipped."); return null; }
        var lease = PersonalOptionsFileCoordinator.TryAcquire(optionsPath);
        if (lease is null) { report?.Invoke("Another game session owns these options; writes skipped."); return null; }
        try
        {
            if (isGameRunning()) { lease.Dispose(); return null; }
            var options = PersonalOptionsFileCoordinator.Read(optionsPath);
            var currentText = options.Text;
            var memoryPath = GetMemoryPath(request.InstanceDirectory);
            var memorySnapshot = request.RememberInstance ? PersonalOptionsFileCoordinator.Read(memoryPath)
                : new PersonalOptionsFileCoordinator.Snapshot(false, [], "missing");
            var sharedSnapshot = request.SharedCategories == PersonalOptionCategories.None
                ? new PersonalOptionsFileCoordinator.Snapshot(false, [], "missing")
                : PersonalOptionsFileCoordinator.Read(request.SharedProfilePath);
            var memory = new PersonalOptionsProfile();
            var canRemember = request.RememberInstance;
            if (canRemember && memorySnapshot.Exists)
            {
                try
                {
                    var document = JsonSerializer.Deserialize<InstancePersonalOptionsDocument>(memorySnapshot.Text.TrimStart('\uFEFF'), JsonOptions)
                        ?? throw new InvalidDataException("Personal settings memory is empty.");
                    if (document.SchemaVersion != 1 || document.OwnerKey != OwnerKey(request.InstanceDirectory)
                        || document.Profile is null || document.Profile.SchemaVersion != 1 || document.Profile.Values is null)
                        throw new InvalidDataException("Unsupported personal settings memory or owner.");
                    memory = FilterProfile(document.Profile, Remembered);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    canRemember = false;
                    report?.Invoke("Instance memory was preserved without rewriting: " + ex.Message);
                }
            }
            var shared = new PersonalOptionsProfile();
            if (request.SharedCategories != PersonalOptionCategories.None)
            {
                try { shared = LoadSharedProfile(sharedSnapshot); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    request = request with { SharedCategories = PersonalOptionCategories.None };
                    report?.Invoke("Shared profile was preserved without rewriting: " + ex.Message);
                }
            }
            var preview = BuildPreview(currentText, memory, shared, request);
            if (preview.Differences.Count > 0)
            {
                var choice = choose(preview);
                if ((choice is PersonalOptionsApplyChoice.PreferInstance or PersonalOptionsApplyChoice.PreferShared)
                    && !isGameRunning())
                {
                    var memoryCurrent = canRemember ? PersonalOptionsFileCoordinator.Read(memoryPath) : memorySnapshot;
                    var sharedCurrent = request.SharedCategories == PersonalOptionCategories.None ? sharedSnapshot
                        : PersonalOptionsFileCoordinator.Read(request.SharedProfilePath);
                    if (memoryCurrent.Hash != memorySnapshot.Hash || memoryCurrent.Exists != memorySnapshot.Exists
                        || sharedCurrent.Hash != sharedSnapshot.Hash || sharedCurrent.Exists != sharedSnapshot.Exists)
                    {
                        report?.Invoke("Settings sources changed after the preview; no options were applied.");
                        return new PersonalOptionsRunSession(lease, request, optionsPath, canRemember ? memoryPath : null,
                            memorySnapshot, sharedSnapshot, report);
                    }
                    var candidate = Combine(memory, shared, request.SharedCategories,
                        choice == PersonalOptionsApplyChoice.PreferShared);
                    var merged = MergePreservingLines(currentText, candidate, request.TargetUsesModernKeyNames, request.FovIsDegrees);
                    if (!string.Equals(currentText, merged, StringComparison.Ordinal)
                        && !PersonalOptionsFileCoordinator.TryWrite(optionsPath, PersonalOptionsFileCoordinator.Encode(merged), options))
                        report?.Invoke("Options changed after the preview; the new contents were preserved.");
                }
            }
            return new PersonalOptionsRunSession(lease, request, optionsPath, canRemember ? memoryPath : null,
                memorySnapshot, sharedSnapshot, report);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public static PersonalOptionsChangePreview BuildPreview(string currentText, PersonalOptionsProfile memory,
        PersonalOptionsProfile shared, PersonalOptionsMemoryRequest request)
    {
        var instanceValues = request.RememberInstance ? FilterProfile(memory, Remembered).Values
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var sharedValues = FilterProfile(shared, request.SharedCategories).Values;
        var currentValues = PersonalOptionsSyncService.Capture(currentText, null, PersonalOptionCategories.All).Values;
        var differences = instanceValues.Keys.Concat(sharedValues.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).Select(key =>
            {
                currentValues.TryGetValue(key, out var current);
                instanceValues.TryGetValue(key, out var instance);
                sharedValues.TryGetValue(key, out var global);
                return new PersonalOptionsDifference(key, current, instance, global);
            })
            .Where(item => (item.Instance is not null && item.Instance != item.Current)
                || (item.Shared is not null && item.Shared != item.Current)).ToArray();
        return new PersonalOptionsChangePreview(differences,
            differences.Any(item => item.Instance is not null && item.Shared is not null && item.Instance != item.Shared),
            instanceValues.Count > 0, sharedValues.Count > 0);
    }

    private static PersonalOptionsProfile Combine(PersonalOptionsProfile memory, PersonalOptionsProfile shared,
        PersonalOptionCategories categories, bool preferShared)
    {
        var memoryValues = FilterProfile(memory, Remembered).Values;
        var sharedValues = FilterProfile(shared, categories).Values;
        var result = new Dictionary<string, string>(preferShared ? memoryValues : sharedValues, StringComparer.Ordinal);
        foreach (var pair in preferShared ? sharedValues : memoryValues) result[pair.Key] = pair.Value;
        return new PersonalOptionsProfile { Values = result };
    }

    private static PersonalOptionsProfile FilterProfile(PersonalOptionsProfile profile, PersonalOptionCategories categories)
    {
        if (profile.SchemaVersion != 1 || profile.Values is null) throw new InvalidDataException("Unsupported options profile.");
        var safe = profile.Values.Where(pair => pair.Key.IndexOfAny(['\r', '\n', '\0', ':']) < 0
            && pair.Value is not null && pair.Value.IndexOfAny(['\r', '\n', '\0']) < 0);
        return PersonalOptionsSyncService.Capture(string.Join("\n", safe.Select(pair => pair.Key + ":" + pair.Value)), null, categories);
    }

    private static PersonalOptionsProfile LoadSharedProfile(PersonalOptionsFileCoordinator.Snapshot snapshot)
    {
        if (!snapshot.Exists) return new PersonalOptionsProfile();
        var profile = JsonSerializer.Deserialize<PersonalOptionsProfile>(snapshot.Text.TrimStart('\uFEFF'), JsonOptions)
            ?? throw new InvalidDataException("Shared options profile is empty.");
        return FilterProfile(profile, PersonalOptionCategories.All);
    }

    /// <summary>Uses existing key conversion but preserves every unrelated original line and delimiter.</summary>
    public static string MergePreservingLines(string currentText, PersonalOptionsProfile candidate, bool modern, bool fovIsDegrees)
    {
        var rendered = PersonalOptionsSyncService.Apply(currentText, candidate, PersonalOptionCategories.All, modern, fovIsDegrees);
        var desired = PersonalOptionsSyncService.Parse(rendered.Text);
        var allowed = new HashSet<string>(rendered.AppliedKeys, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new StringBuilder();
        var bom = currentText.StartsWith('\uFEFF');
        if (bom) result.Append('\uFEFF');
        var text = bom ? currentText[1..] : currentText;
        var cursor = 0;
        var preferredNewline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r') ? "\r" : "\n";
        while (cursor < text.Length)
        {
            var end = cursor;
            while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
            var line = text[cursor..end];
            var colon = line.IndexOf(':');
            var rawKey = colon > 0 ? line[..colon] : "";
            var key = rawKey == "key_key.swapHands" ? "key_key.swapOffhand" : rawKey;
            if (allowed.Contains(key) && desired.TryGetValue(key, out var replacement))
            {
                result.Append(rawKey).Append(':').Append(replacement);
                seen.Add(key);
            }
            else result.Append(line);
            if (end < text.Length)
            {
                if (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n')
                { result.Append("\r\n"); end += 2; }
                else { result.Append(text[end]); end++; }
            }
            cursor = end;
        }
        foreach (var key in allowed.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (seen.Contains(key) || !desired.TryGetValue(key, out var value)) continue;
            if (result.Length > (bom ? 1 : 0) && result[^1] is not '\r' and not '\n') result.Append(preferredNewline);
            var outputKey = !modern && key == "key_key.swapOffhand" ? "key_key.swapHands" : key;
            result.Append(outputKey).Append(':').Append(value).Append(preferredNewline);
        }
        return result.ToString();
    }

    public sealed class PersonalOptionsRunSession : IDisposable
    {
        private PersonalOptionsFileCoordinator? _lease;
        private readonly PersonalOptionsMemoryRequest _request;
        private readonly string _optionsPath;
        private readonly string? _memoryPath;
        private readonly PersonalOptionsFileCoordinator.Snapshot _memorySnapshot;
        private readonly PersonalOptionsFileCoordinator.Snapshot _sharedSnapshot;
        private readonly Action<string>? _report;
        private int _completed;

        internal PersonalOptionsRunSession(PersonalOptionsFileCoordinator lease, PersonalOptionsMemoryRequest request,
            string optionsPath, string? memoryPath, PersonalOptionsFileCoordinator.Snapshot memorySnapshot,
            PersonalOptionsFileCoordinator.Snapshot sharedSnapshot, Action<string>? report)
        {
            _lease = lease; _request = request; _optionsPath = optionsPath; _memoryPath = memoryPath;
            _memorySnapshot = memorySnapshot; _sharedSnapshot = sharedSnapshot; _report = report;
        }

        /// <summary>Retains a PID/start-time identity so another launcher can avoid writes even after this launcher exits.</summary>
        public void MarkGameStarted(int processId, long startTimeUtcTicks) => _lease?.MarkGameStarted(processId, startTimeUtcTicks);

        public void Complete(bool reliableNormalExit, Func<bool> isAnotherGameRunning,
            bool rememberStillEnabled, bool sharedStillEnabled,
            PersonalOptionCategories currentSharedCategories = PersonalOptionCategories.All)
        {
            if (_lease is null || System.Threading.Interlocked.Exchange(ref _completed, 1) != 0) return;
            try
            {
                if (!reliableNormalExit || isAnotherGameRunning()) return;
                var options = PersonalOptionsFileCoordinator.Read(_optionsPath);
                if (!options.Exists || string.IsNullOrWhiteSpace(options.Text)) return;
                var values = PersonalOptionsSyncService.Capture(options.Text, null, Remembered).Values;
                if (!values.ContainsKey("key_key.attack") || !values.ContainsKey("key_key.use")
                    || !values.TryGetValue("mouseSensitivity", out var sensitivity) || string.IsNullOrWhiteSpace(sensitivity))
                { _report?.Invoke("Options do not contain a complete baseline control set; existing memory was preserved."); return; }
                if (_memoryPath is not null && rememberStillEnabled)
                {
                    var profile = PersonalOptionsSyncService.Capture(options.Text, null, Remembered);
                    if (profile.Values.Count > 0)
                    {
                        var document = new InstancePersonalOptionsDocument { OwnerKey = OwnerKey(_request.InstanceDirectory), Profile = profile };
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, JsonOptions));
                        if (!PersonalOptionsFileCoordinator.TryWrite(_memoryPath, bytes, _memorySnapshot))
                            _report?.Invoke("Instance memory changed during play; it was not overwritten.");
                    }
                }
                if (_request.SharedCategories != PersonalOptionCategories.None && sharedStillEnabled)
                {
                    using var profileLease = PersonalOptionsFileCoordinator.TryAcquireProfile(_request.SharedProfilePath);
                    if (profileLease is null)
                    { _report?.Invoke("Shared profile is in use; it was not overwritten."); return; }
                    var current = PersonalOptionsFileCoordinator.Read(_request.SharedProfilePath);
                    if (current.Hash != _sharedSnapshot.Hash || current.Exists != _sharedSnapshot.Exists)
                    { _report?.Invoke("Shared profile changed during play; it was not overwritten."); return; }
                    var previous = LoadSharedProfile(current);
                    var captured = PersonalOptionsSyncService.Capture(options.Text, previous,
                        _request.SharedCategories & currentSharedCategories);
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(captured, JsonOptions));
                    if (!PersonalOptionsFileCoordinator.TryWrite(_request.SharedProfilePath, bytes, current))
                        _report?.Invoke("Shared profile changed during capture; it was not overwritten.");
                }
            }
            finally
            {
                try { _lease?.ClearStoppedOwner(); }
                finally { Dispose(); }
            }
        }

        public void Dispose()
        {
            var lease = System.Threading.Interlocked.Exchange(ref _lease, null);
            lease?.Dispose();
        }
    }
}
