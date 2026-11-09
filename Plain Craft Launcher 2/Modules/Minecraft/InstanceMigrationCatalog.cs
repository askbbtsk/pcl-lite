using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.InstanceMigration;
using PCL.Core.Minecraft.ResourceCollections;

namespace PCL;

/// <summary>Recognizes actual bytes with existing platforms; never maps equivalent names across projects.</summary>
internal static class InstanceMigrationCatalog
{
    internal sealed record Preview(InstanceMigrationPlan Plan, Dictionary<string, ModComp.CompFile> Files);
    private sealed record Local(string Path, string Sha256, string Sha1, string Md5);

    internal static string Key(MigrationCandidate candidate) => candidate.Platform + ":" + candidate.ProjectId + ":" + candidate.FileId;

    internal static Preview Build(McInstance source, string minecraftVersion, MigrationLoader loader, CancellationToken token)
    {
        var modsDirectory = Path.Combine(source.PathIndie, "mods");
        var paths = Directory.Exists(modsDirectory) ? Directory.EnumerateFiles(modsDirectory).ToArray() : [];
        var index = ResourceCollectionIndexer.Capture("migration", paths, token);
        var sources = new List<MigrationSourceMod>();
        var locals = new List<Local>();
        foreach (var entry in index.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Availability != ResourceAvailability.Available)
            {
                sources.Add(new() { Path = entry.CanonicalPath, Sha256 = "", UnresolvedReason =
                    Lang.Text("Instance.Migration.Unreadable", entry.Availability.ToString()) });
                continue;
            }
            try
            {
                var hashes = HashFile(entry.CanonicalPath, token);
                locals.Add(new(entry.CanonicalPath, hashes.Sha256, hashes.Sha1, hashes.Md5));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { sources.Add(new() { Path = entry.CanonicalPath, Sha256 = "", UnresolvedReason = ex.Message }); }
        }

        var modrinth = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in locals.Chunk(500))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var response = ModDownload.DlModRequest<JsonObject>("https://api.modrinth.com/v2/version_files", "POST",
                    JsonSerializer.Serialize(new { hashes = group.Select(file => file.Sha1.ToLowerInvariant()).ToArray(), algorithm = "sha1" }), "application/json");
                foreach (var file in group)
                    if (response[file.Sha1.ToLowerInvariant()] is JsonObject found && MatchesModrinth(found, file.Sha1))
                        modrinth[file.Path] = found;
            }
            catch (Exception ex)
            { ModBase.Log(ex, "迁移时查询 Modrinth 本地文件失败", ModBase.LogLevel.Developer); }
        }

        var curseForge = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in locals.Where(file => !modrinth.ContainsKey(file.Path)).Chunk(500))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var fingerprints = group.Select(file => (File: file, Hash: new ModLocalComp.LocalCompFile(file.Path).CurseForgeHash)).ToArray();
                var response = ModDownload.DlModRequest<JsonObject>("https://api.curseforge.com/v1/fingerprints/432", "POST",
                    JsonSerializer.Serialize(new { fingerprints = fingerprints.Select(pair => pair.Hash).ToArray() }), "application/json");
                if (response["data"]?["exactMatches"] is not JsonArray matches) continue;
                foreach (var item in matches.OfType<JsonObject>())
                {
                    if (item["file"] is not JsonObject remote) continue;
                    foreach (var local in group)
                        if (MatchesCurseForge(remote, local)) curseForge[local.Path] = remote;
                }
            }
            catch (Exception ex)
            { ModBase.Log(ex, "迁移时查询 CurseForge 本地文件失败", ModBase.LogLevel.Developer); }
        }

        var remoteFiles = new Dictionary<string, ModComp.CompFile>(StringComparer.Ordinal);
        foreach (var local in locals)
        {
            token.ThrowIfCancellationRequested();
            var platform = modrinth.ContainsKey(local.Path) ? "Modrinth" : curseForge.ContainsKey(local.Path) ? "CurseForge" : null;
            var metadata = platform == "Modrinth" ? modrinth[local.Path] : platform == "CurseForge" ? curseForge[local.Path] : null;
            var projectId = metadata?[platform == "Modrinth" ? "project_id" : "modId"]?.ToString();
            var candidates = new List<MigrationCandidate>();
            string? failure = null;
            if (projectId is not null && platform is not null)
            {
                try
                {
                    foreach (var file in ModComp.CompFilesGet(projectId, platform == "CurseForge"))
                    {
                        if (!file.Available || file.ProjectId != projectId || !InstanceMigrationPlanner.ValidHash(file.Hash)) continue;
                        var candidate = new MigrationCandidate
                        {
                            Platform = platform, ProjectId = projectId, FileId = file.Id, FileName = file.FileName,
                            Hash = file.Hash, MinecraftVersions = file.RawGameVersions.ToArray(),
                            Loaders = file.ModLoaders.Select(ToMigrationLoader).Where(value => value.HasValue).Select(value => value!.Value).ToArray(),
                            PublishedAt = new DateTimeOffset(file.ReleaseDate.ToUniversalTime()), Stable = file.Status == ModComp.CompFileStatus.Release,
                        };
                        candidates.Add(candidate);
                        remoteFiles[Key(candidate)] = file;
                    }
                }
                catch (Exception ex)
                { ModBase.Log(ex, "迁移时查询同项目构建失败", ModBase.LogLevel.Developer); failure = Lang.Text("Instance.Migration.LookupFailed"); }
            }
            sources.Add(new()
            {
                Path = local.Path, Sha256 = local.Sha256, Sha1 = local.Sha1, Platform = platform,
                ProjectId = projectId, Candidates = candidates, UnresolvedReason = failure ?? (projectId is null ? Lang.Text("Instance.Migration.UnknownProject") : null),
            });
        }
        return new(InstanceMigrationPlanner.Create(minecraftVersion, loader, sources), remoteFiles);
    }

    internal static ModComp.CompLoaderType ToHostLoader(MigrationLoader loader) => loader switch
    {
        MigrationLoader.Fabric => ModComp.CompLoaderType.Fabric,
        MigrationLoader.Forge => ModComp.CompLoaderType.Forge,
        MigrationLoader.NeoForge => ModComp.CompLoaderType.NeoForge,
        _ => throw new ArgumentOutOfRangeException(nameof(loader)),
    };

    private static MigrationLoader? ToMigrationLoader(ModComp.CompLoaderType loader) => loader switch
    {
        ModComp.CompLoaderType.Fabric => MigrationLoader.Fabric,
        ModComp.CompLoaderType.Forge => MigrationLoader.Forge,
        ModComp.CompLoaderType.NeoForge => MigrationLoader.NeoForge,
        _ => null,
    };

    private static bool MatchesModrinth(JsonObject metadata, string sha1) => metadata["files"] is JsonArray files
        && files.OfType<JsonObject>().Any(file => string.Equals(file["hashes"]?["sha1"]?.ToString(), sha1, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesCurseForge(JsonObject metadata, Local local) => metadata["hashes"] is JsonArray hashes
        && hashes.OfType<JsonObject>().Any(hash => hash["algo"]?.ToString() switch
        {
            "1" => string.Equals(hash["value"]?.ToString(), local.Sha1, StringComparison.OrdinalIgnoreCase),
            "2" => string.Equals(hash["value"]?.ToString(), local.Md5, StringComparison.OrdinalIgnoreCase),
            _ => false,
        });

    internal static (string Sha256, string Sha1, string Md5) HashFile(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var bytes = new byte[64 * 1024]; int count;
        while ((count = stream.Read(bytes, 0, bytes.Length)) > 0)
        { token.ThrowIfCancellationRequested(); sha256.AppendData(bytes, 0, count); sha1.AppendData(bytes, 0, count); md5.AppendData(bytes, 0, count); }
        return (Convert.ToHexString(sha256.GetHashAndReset()), Convert.ToHexString(sha1.GetHashAndReset()), Convert.ToHexString(md5.GetHashAndReset()));
    }
}
