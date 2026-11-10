using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.InstanceMigration;
using PCL.Network;

namespace PCL;

/// <summary>Resolves all selected mods before materializing any of them, using exact raw target-version metadata.</summary>
internal static class ModificationDependencyCoordinator
{
    internal sealed record Prepared(IReadOnlyList<MigrationModPlan> Mods, IReadOnlyList<DownloadFile> Dependencies,
        IReadOnlyList<string> Notices);

    internal static Prepared Prepare(InstanceMigrationCatalog.Preview preview, IReadOnlyList<MigrationModPlan> chosen,
        string targetMinecraftVersion, MigrationLoader targetLoader, string stageModsDirectory, CancellationToken token,
        Action<double>? progress = null, Action<string>? activity = null)
    {
        var loader = targetLoader.ToString();
        var projects = new Dictionary<string, IReadOnlyList<ModificationDependencyFile>>(StringComparer.Ordinal);
        var rawFiles = new Dictionary<string, ModComp.CompFile>(StringComparer.Ordinal);
        var notices = new List<string>();
        IReadOnlyList<ModificationDependencyFile> GetFiles(string platform, string projectId)
        {
            token.ThrowIfCancellationRequested();
            var key = platform + ":" + projectId;
            if (projects.TryGetValue(key, out var cached)) return cached;
            activity?.Invoke(platform + " / " + projectId);
            var result = new List<ModificationDependencyFile>();
            if (platform == "Modrinth")
            {
                // Platform labels on a universal artifact are a union, not conditional dependency expressions.
                // Keep required references intact; if one has no target build, skip that root rather than guessing a substitute.
                var endpoint = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(projectId) + "/version?include_changelog=false"
                    + "&loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() }))
                    + "&game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { targetMinecraftVersion }));
                var versions = ModDownload.DlModRequest<JsonArray>(endpoint);
                foreach (var version in versions.OfType<JsonObject>())
                {
                    token.ThrowIfCancellationRequested();
                    var host = new ModComp.CompFile(version, ModComp.CompType.Mod);
                    if (!host.Available || host.ProjectId != projectId) continue;
                    var references = new List<ModificationDependencyReference>();
                    if (version["dependencies"] is JsonArray dependencies)
                        foreach (var dependency in dependencies.OfType<JsonObject>().Where(item => item["dependency_type"]?.ToString() == "required"))
                        {
                            var requiredProject = dependency["project_id"]?.ToString();
                            var fileId = dependency["version_id"]?.ToString();
                            if (string.IsNullOrWhiteSpace(requiredProject) && fileId is not null)
                                requiredProject = ModDownload.DlModRequest<JsonObject>("https://api.modrinth.com/v2/version/" + Uri.EscapeDataString(fileId))["project_id"]?.ToString();
                            if (string.IsNullOrWhiteSpace(requiredProject))
                                throw new InvalidDataException("Required dependency has no verified project identity: " + host.ProjectId + "/" + host.Id);
                            references.Add(new("Modrinth", requiredProject, fileId));
                        }
                    var file = Map(host, platform, references);
                    result.Add(file); rawFiles[ModificationDependencyPlanner.FileKey(file)] = host;
                }
            }
            else if (platform == "CurseForge")
                foreach (var host in ModComp.CompFilesGet(projectId, true))
                {
                    token.ThrowIfCancellationRequested();
                    if (!host.Available || host.ProjectId != projectId) continue;
                    var file = Map(host, platform, host.RawDependencies.Select(id => new ModificationDependencyReference(platform, id)).ToArray());
                    result.Add(file); rawFiles[ModificationDependencyPlanner.FileKey(file)] = host;
                }
            else throw new InvalidDataException("Unsupported dependency platform: " + platform);
            projects[key] = result; return result;
        }
        var roots = new List<ModificationDependencyRoot>();
        var inspected = 0;
        foreach (var item in chosen)
        {
            token.ThrowIfCancellationRequested();
            activity?.Invoke($"{inspected + 1}/{chosen.Count} · {Path.GetFileName(item.Source.Path)}");
            var candidate = item.Candidate!;
            try
            {
                var file = GetFiles(candidate.Platform, candidate.ProjectId).FirstOrDefault(value => value.FileId == candidate.FileId);
                if (file is null)
                { notices.Add(Path.GetFileName(item.Source.Path) + ": " + Lang.Text("Instance.Migration.LookupFailed")); continue; }
                roots.Add(new(item.Source.Path, file, item.Disabled));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            { notices.Add(Path.GetFileName(item.Source.Path) + ": " + error.Message); }
            progress?.Invoke(0.5 * ++inspected / Math.Max(1, chosen.Count));
        }
        var plan = ModificationDependencyPlanner.Plan(roots, targetMinecraftVersion, loader, GetFiles, token);
        progress?.Invoke(0.9);
        notices.AddRange(plan.Rejected.Select(item => Path.GetFileName(item.SourcePath) + ": " + item.Reason));
        var accepted = chosen.Where(item => plan.AcceptedSources.Contains(item.Source.Path, StringComparer.OrdinalIgnoreCase)).ToArray();
        var downloads = new List<DownloadFile>();
        var names = new HashSet<string>(accepted.Select(item => item.TargetFileName!), StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in plan.Downloads)
        {
            token.ThrowIfCancellationRequested();
            var host = rawFiles[ModificationDependencyPlanner.FileKey(dependency)];
            if (!names.Add(host.FileName)) throw new IOException("Dependency artifact filename collides with a different selected file: " + host.FileName);
            var target = Path.Combine(stageModsDirectory, host.FileName);
            downloads.Add(host.ToNetFile(target, ModComp.DownloadReason.Dependency, targetMinecraftVersion,
                InstanceMigrationCatalog.ToHostLoader(targetLoader)));
        }
        if (downloads.Count > 0)
        {
            var summary = string.Join(Environment.NewLine, plan.Downloads.Select(file => file.ProjectId + " / " + file.FileName));
            var consent = ModBase.RunInUiWait(() => ModMain.MyMsgBox(
                Lang.Text("Instance.Modification.DependencyBatch", summary), Lang.Text("Instance.Migration.Title"),
                Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true));
            if (consent != 1) throw new OperationCanceledException(token);
        }
        if (notices.Count > 0)
        {
            var message = Lang.Text("Instance.Modification.DependencySkipped", string.Join(Environment.NewLine, notices));
            if (ModBase.RunInUiWait(() => ModMain.MyMsgBox(message, Lang.Text("Instance.Migration.Title"),
                    Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true)) != 1)
                throw new OperationCanceledException(token);
        }
        progress?.Invoke(1);
        return new(accepted, downloads, notices);
    }

    private static ModificationDependencyFile Map(ModComp.CompFile file, string platform,
        IReadOnlyList<ModificationDependencyReference> references) => new()
    {
        Platform = platform, ProjectId = file.ProjectId, FileId = file.Id, FileName = file.FileName, Hash = file.Hash,
        MinecraftVersions = file.RawGameVersions.ToArray(), Loaders = file.ModLoaders.Where(value => value != ModComp.CompLoaderType.Any).Select(value => value.ToString()).ToArray(),
        Dependencies = references, Stable = file.Status == ModComp.CompFileStatus.Release,
        PublishedAt = new DateTimeOffset(file.ReleaseDate.ToUniversalTime()),
    };
}
