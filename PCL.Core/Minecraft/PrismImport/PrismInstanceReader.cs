using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;

namespace PCL.Core.Minecraft.PrismImport;

public sealed record PrismComponent(string Uid, string Version, bool Important, bool DependencyOnly,
    bool Disabled, JsonObject Original);
public sealed record PrismPatch(string FileName, string Uid, int Order, int SourceOrdinal, JsonObject Original);
public sealed record PrismReadResult
{
    public required string InstanceDirectory { get; init; }
    public required string GameDirectory { get; init; }
    public required string SuggestedName { get; init; }
    public required string MinecraftVersion { get; init; }
    public string? LoaderUid { get; init; }
    public string? LoaderVersion { get; init; }
    public required JsonObject OriginalManifest { get; init; }
    public IReadOnlyList<PrismComponent> Components { get; init; } = [];
    public IReadOnlyList<PrismPatch> Patches { get; init; } = [];
    public IReadOnlyDictionary<string, string> InstanceSettings { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SourceHashes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> BlockingIssues { get; init; } = [];
    public bool CanImport => BlockingIssues.Count == 0;
}

/// <summary>Reads only the explicitly selected Prism instance, never global accounts, caches or running Prism state.</summary>
public static class PrismInstanceReader
{
    private static readonly HashSet<string> KnownUids = new(StringComparer.Ordinal)
    { "net.minecraft", "net.fabricmc.fabric-loader", "net.fabricmc.intermediary", "net.minecraftforge", "net.neoforged", "org.lwjgl", "org.lwjgl3" };
    private static readonly HashSet<string> PatchKeys = new(StringComparer.Ordinal)
    {
        "uid", "version", "name", "formatVersion", "order", "releaseTime", "time", "minecraftVersion", "requires", "conflicts",
        "mainClass", "assetIndex", "libraries", "+libraries", "+jvmArgs", "+minecraftArguments", "+tweakers", "compatibleJavaMajors",
    };

    public static PrismReadResult Read(string instanceDirectory, CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory));
        RejectLinks(root);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifest = ReadObject(root, "mmc-pack.json", hashes, cancellationToken);
        if (manifest["formatVersion"]?.ToString() != "1") throw new InvalidDataException("Unsupported Prism manifest formatVersion.");
        var warnings = new List<string>(); var blockers = new List<string>();
        var components = new List<PrismComponent>();
        if (manifest["components"] is not JsonArray declared) throw new InvalidDataException("Prism components are missing.");
        foreach (var raw in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (raw is not JsonObject component) throw new InvalidDataException("Invalid Prism component.");
            var uid = component["uid"]?.ToString(); var version = component["version"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(uid)) throw new InvalidDataException("Component UID is required.");
            var record = new PrismComponent(uid, version, ReadBoolean(component, "important"), ReadBoolean(component, "dependencyOnly"),
                ReadBoolean(component, "disabled"), (JsonObject)component.DeepClone());
            components.Add(record);
            if (!record.Disabled && !InstanceMigration.InstanceMigrationPlanner.ValidFileName(version))
                blockers.Add("Active component version is not a safe version identifier: " + uid);
            if (record.Disabled) warnings.Add("Disabled component preserved without installation: " + uid);
            else if (uid == "org.quiltmc.quilt-loader") blockers.Add("Quilt installation is not supported by this importer.");
            else if (!KnownUids.Contains(uid)) blockers.Add("Unmapped active component: " + uid);
            else if (uid is "org.lwjgl" or "org.lwjgl3") warnings.Add("Prism LWJGL selection requires compatible patch metadata; the standard installer alone is not evidence of the requested custom LWJGL version.");
        }
        var active = components.Where(component => !component.Disabled).ToArray();
        foreach (var repeated in active.GroupBy(component => component.Uid, StringComparer.Ordinal).Where(group => group.Count() > 1))
            blockers.Add("Ambiguous duplicate active component UID: " + repeated.Key);
        var minecraft = active.Where(component => component.Uid == "net.minecraft").ToArray();
        if (minecraft.Length != 1 || string.IsNullOrWhiteSpace(minecraft[0].Version)) throw new InvalidDataException("Exactly one active Minecraft component is required.");
        var loaders = active.Where(component => component.Uid is "net.fabricmc.fabric-loader" or "net.minecraftforge" or "net.neoforged").ToArray();
        if (loaders.Length > 1) blockers.Add("Multiple active loaders cannot be mapped safely.");
        if (loaders.Any(component => string.IsNullOrWhiteSpace(component.Version))) blockers.Add("The active loader has no version.");
        var settings = ReadSettings(root, hashes, cancellationToken);
        foreach (var setting in settings.Keys.Where(key => key is not ("name" or "InstanceType" or "iconKey")))
            warnings.Add("Unmapped Prism setting, retained in the import report only: " + setting);
        if (settings.ContainsKey("iconKey")) warnings.Add("Prism icon reference was not imported from the global launcher cache.");
        var game = Path.Combine(root, ".minecraft");
        RejectLinks(game);
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException("The selected Prism instance has no .minecraft directory.");
        var patches = new List<PrismPatch>();
        var patchDirectory = Path.Combine(root, "patches"); RejectLinks(patchDirectory);
        if (Directory.Exists(patchDirectory))
        {
            var files = Directory.EnumerateFileSystemEntries(patchDirectory).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (files.Length > 1000) throw new InvalidDataException("Too many Prism patch files.");
            for (var ordinal = 0; ordinal < files.Length; ordinal++)
            {
                var file = files[ordinal]; RejectLinks(file);
                if (Directory.Exists(file)) { blockers.Add("Nested patch directories are not supported: " + Path.GetFileName(file)); continue; }
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { warnings.Add("Unmapped patch file: " + Path.GetFileName(file)); continue; }
                var patch = ReadObject(root, Path.GetRelativePath(root, file), hashes, cancellationToken);
                var uid = patch["uid"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(uid)) { blockers.Add("Patch UID is missing: " + Path.GetFileName(file)); continue; }
                var order = patch["order"] is null ? 0 : patch["order"]!.GetValue<int>();
                patches.Add(new(Path.GetFileName(file), uid, order, ordinal, patch));
                if (!active.Any(component => component.Uid == uid))
                { warnings.Add("Unapplied patch preserved for inactive or unknown component: " + uid); continue; }
                foreach (var key in patch.Select(pair => pair.Key).Where(key => !PatchKeys.Contains(key)))
                    blockers.Add("Unmapped active patch field: " + uid + "/" + key);
                if (patch["libraries"] is not null)
                    blockers.Add("Component-level library replacement needs an explicit adapter and was not silently merged: " + uid);
            }
        }
        foreach (var duplicate in patches.Where(patch => active.Any(component => component.Uid == patch.Uid))
                     .GroupBy(patch => patch.Uid, StringComparer.Ordinal).Where(group => group.Count() > 1))
            blockers.Add("More than one local patch maps to the active component: " + duplicate.Key);
        foreach (var duplicate in patches.GroupBy(patch => patch.FileName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            blockers.Add("Patch filenames are ambiguous under Windows path comparison: " + duplicate.Key);
        // Modern Prism applies mmc-pack.components in manifest order. Deprecated patch.order is retained as data only.
        var ordered = patches.OrderBy(patch =>
        {
            var index = Array.FindIndex(active, component => component.Uid == patch.Uid);
            return index < 0 ? int.MaxValue : index;
        }).ThenBy(patch => patch.SourceOrdinal).ToArray();
        return new()
        {
            InstanceDirectory = root, GameDirectory = game,
            SuggestedName = settings.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name) ? name : Path.GetFileName(root),
            MinecraftVersion = minecraft[0].Version, LoaderUid = loaders.FirstOrDefault()?.Uid, LoaderVersion = loaders.FirstOrDefault()?.Version,
            OriginalManifest = manifest, Components = components, Patches = ordered, InstanceSettings = settings,
            SourceHashes = hashes, Warnings = warnings, BlockingIssues = blockers,
        };
    }

    public static bool InputsUnchanged(PrismReadResult source, CancellationToken cancellationToken = default)
    {
        foreach (var pair in source.SourceHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(source.InstanceDirectory, pair.Key); RejectLinks(path);
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), pair.Value, StringComparison.Ordinal)) return false;
        }
        var cfgExists = File.Exists(Path.Combine(source.InstanceDirectory, "instance.cfg"));
        if (cfgExists != source.SourceHashes.ContainsKey("instance.cfg")) return false;
        var patches = Path.Combine(source.InstanceDirectory, "patches"); RejectLinks(patches);
        var now = Directory.Exists(patches) ? Directory.EnumerateFiles(patches, "*.json").Select(path => Path.GetRelativePath(source.InstanceDirectory, path)).ToArray() : [];
        return now.Length == source.Patches.Count && now.All(source.SourceHashes.ContainsKey);
    }

    private static JsonObject ReadObject(string root, string relative, Dictionary<string, string> hashes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.Combine(root, relative); RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Prism metadata file is too large.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        hashes[relative] = Convert.ToHexString(SHA256.HashData(bytes));
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("Invalid Prism metadata object.");
    }

    private static Dictionary<string, string> ReadSettings(string root, Dictionary<string, string> hashes, CancellationToken token)
    {
        var path = Path.Combine(root, "instance.cfg"); RejectLinks(path);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return values;
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Prism settings file is too large.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        hashes["instance.cfg"] = Convert.ToHexString(SHA256.HashData(bytes));
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(bytes)); string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var split = line.IndexOf('=');
            if (split > 0 && !line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith(';'))
                values[line[..split].Trim()] = line[(split + 1)..];
        }
        return values;
    }

    private static bool ReadBoolean(JsonObject value, string key)
        => value[key] is null ? false : value[key]!.GetValue<bool>();

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Prism import cannot follow links: " + current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
