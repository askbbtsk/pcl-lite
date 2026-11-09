using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.Utils.OS;
using PCL.Network.Loaders;
using CompFile = PCL.ModLocalComp.LocalCompFile;

namespace PCL;

/// <summary>Starts the regular PCL launch only after the user has reviewed required mod dependencies.</summary>
internal static class ModLaunchDependencyPrecheck
{
    private sealed record Issue(CompFile Owner, ModJarInJarIndex.DepRow Dependency, JijDepStatus Status,
        IReadOnlyList<CompFile> DisabledProviders);

    public static bool Confirm(McInstance instance)
    {
        if (instance?.Info is null || !(instance.Info.HasForge || instance.Info.HasNeoForge ||
                                        instance.Info.HasFabric || instance.Info.HasQuilt || instance.Info.HasLabyMod))
            return true;

        var modsPath = instance.Info.HasLabyMod
            ? Path.Combine(instance.PathIndie, "labymod-neo", "fabric", instance.Info.VanillaName, "mods")
            : Path.Combine(instance.PathIndie, "mods");
        if (!Directory.Exists(modsPath)) return true;

        var issues = Scan(instance, modsPath);
        if (issues.Count == 0) return true;

        var enableCandidates = issues
            .Where(issue => issue.Status == JijDepStatus.Disabled && issue.DisabledProviders.Count == 1)
            .Select(issue => issue.DisabledProviders[0])
            .Distinct()
            .ToList();

        if (enableCandidates.Count > 0)
        {
            var choice = ShowPrompt(instance, issues, canEnable: true);
            if (choice == 0) return false;
            if (choice == 2) return true;
            if (choice != 1) return false;

            var failedEnables = new List<string>();
            foreach (var provider in enableCandidates)
            {
                try
                {
                    var destination = provider.RawPath;
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        failedEnables.Add(provider.FileName);
                        continue;
                    }
                    File.Move(provider.path, destination);
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "启动前启用模组前置失败：" + provider.path, ModBase.LogLevel.Hint);
                    failedEnables.Add(provider.FileName);
                }
            }

            issues = Scan(instance, modsPath);
            if (issues.Count == 0) return true;
            if (failedEnables.Count > 0)
                ModBase.Log(Lang.Text("Minecraft.Launch.Dependency.EnableFailed", string.Join(", ", failedEnables)), ModBase.LogLevel.Hint);
        }

        return ResolveAndInstall(instance, modsPath, issues);
    }

    private static bool ResolveAndInstall(McInstance instance, string modsPath, List<Issue> issues)
    {
        var dependencyIds = issues
            .Where(issue => issue.Status is JijDepStatus.Missing or JijDepStatus.VersionMismatch)
            .Select(issue => issue.Dependency.DepId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var references = new List<ModDependencyReference>();
        var discoveryFailures = new List<UnresolvedDependency>();
        foreach (var dependencyId in dependencyIds)
        {
            try
            {
                var matches = FindExactProjects(instance, dependencyId);
                if (matches.Count == 1)
                {
                    var project = matches[0];
                    references.Add(new ModDependencyReference
                    {
                        ProjectId = project.Id,
                        Source = project.FromCurseForge ? "CurseForge" : "Modrinth",
                        IsRequired = true,
                    });
                }
                else
                {
                    discoveryFailures.Add(new UnresolvedDependency
                    {
                        ProjectId = dependencyId,
                        Source = "Modrinth/CurseForge",
                        Reason = matches.Count == 0
                            ? "No exact project slug was found for this mod id."
                            : "More than one platform returned the same exact project slug.",
                    });
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "启动前搜索模组前置失败：" + dependencyId, ModBase.LogLevel.Hint);
                discoveryFailures.Add(new UnresolvedDependency
                {
                    ProjectId = dependencyId,
                    Source = "Modrinth/CurseForge",
                    Reason = "Project lookup failed; network or service response was unavailable.",
                });
            }
        }

        var resolution = new ModDependencyResolutionResult();
        if (references.Count > 0)
        {
            var resolver = new ModDependencyResolver();
            var resolved = resolver.Resolve(new ModDependencyRequest
            {
                TargetMinecraftVersion = instance.Info.VanillaName,
                TargetLoaders = GetLoaderNames(instance.Info),
                RequiredDependencies = references,
                InstalledMods = ModCompDependency.ScanInstalledMods(modsPath),
                ProjectResolver = ModCompDependency.ResolveProjectFiles,
            });
            resolution.ToInstall.AddRange(resolved.ToInstall);
            resolution.Unresolved.AddRange(resolved.Unresolved);
            resolution.Satisfied.AddRange(resolved.Satisfied);
        }
        resolution.Unresolved.AddRange(discoveryFailures);

        if (resolution.ToInstall.Count == 0 && resolution.Unresolved.Count == 0)
        {
            var remainingAfterDiscovery = Scan(instance, modsPath);
            if (remainingAfterDiscovery.Count == 0) return true;
            return ShowPrompt(instance, remainingAfterDiscovery, canEnable: false) == 2;
        }

        var choice = ModCompDependency.ConfirmDependencyInstall(resolution);
        if (choice == ModComp.CompDepsInstallTypes.Cancel) return false;
        if (choice == ModComp.CompDepsInstallTypes.WithoutDeps) return true;
        if (choice == ModComp.CompDepsInstallTypes.Unresolved && resolution.ToInstall.Count == 0) return true;

        if (resolution.ToInstall.Count > 0)
        {
            var downloads = ModCompDependency.BuildDependencyDownloads(resolution, modsPath);
            if (downloads.Count != resolution.ToInstall.Count)
            {
                var proceedAfterPartial = false;
                var detail = Lang.Text("Minecraft.Launch.Dependency.DownloadPrepareFailed");
                ModBase.RunInUiWait(() => proceedAfterPartial = ModMain.MyMsgBox(detail,
                    Lang.Text("Minecraft.Launch.Dependency.Title"),
                    Lang.Text("Minecraft.Launch.Dependency.Continue"),
                    Lang.Text("Common.Action.Cancel")) == 1);
                if (!proceedAfterPartial) return false;
            }
            else
            {
                Directory.CreateDirectory(modsPath);
                var downloadTasks = downloads.Select(item => (ModLoader.LoaderBase)new LoaderDownload(
                    item.Filename,
                    new List<PCL.Network.DownloadFile> { item.File }) { ProgressWeight = 1d, block = true }).ToList();
                var loader = new ModLoader.LoaderCombo<int>(Lang.Text("Minecraft.Launch.Dependency.DownloadTask"), downloadTasks);
                loader.Start(1);
                ModLoader.LoaderTaskbarAdd(loader);
                while (loader.State == ModBase.LoadState.Loading) Thread.Sleep(50);
                if (loader.State != ModBase.LoadState.Finished)
                    throw loader.Error ?? new IOException("Required dependency downloads did not finish.");
            }
        }

        var remaining = Scan(instance, modsPath);
        if (remaining.Count == 0) return true;
        var proceed = false;
        var details = BuildDetails(remaining);
        ModBase.RunInUiWait(() => proceed = ModMain.MyMsgBox(
            Lang.Text("Minecraft.Launch.Dependency.Message", instance.Name, details),
            Lang.Text("Minecraft.Launch.Dependency.Title"),
            Lang.Text("Minecraft.Launch.Dependency.Continue"),
            Lang.Text("Common.Action.Cancel")) == 1);
        return proceed;
    }

    private static List<ModComp.CompProject> FindExactProjects(McInstance instance, string dependencyId)
    {
        var directIdMatches = ModComp.CompRequest.GetCompProjectsByIds([dependencyId])
            .Where(project => string.Equals(project.Id, dependencyId, StringComparison.Ordinal))
            .GroupBy(project => (project.FromCurseForge, project.Id))
            .Select(static group => group.First())
            .ToList();
        if (directIdMatches.Count == 1) return directIdMatches;
        if (directIdMatches.Count > 1) return directIdMatches;

        var storage = new ModComp.CompProjectStorage();
        var loader = new ModLoader.LoaderTask<ModComp.CompProjectRequest, int>(
            Lang.Text("Minecraft.Launch.Dependency.SearchTask", dependencyId), ModComp.CompProjectsGet);
        var request = new ModComp.CompProjectRequest(ModComp.CompType.Mod, storage, 80)
        {
            searchText = dependencyId,
            gameVersion = instance.Info.VanillaName,
            modLoader = GetSingleLoader(instance.Info),
            source = ModComp.CompSourceType.Any,
            sort = ModComp.CompSortType.Relevance,
        };
        loader.WaitForExit(request);
        var candidates = storage.results
            .Where(static project => project.Type == ModComp.CompType.Mod)
            .ToList();
        var match = DependencyProjectMatcher.FindUniqueExactSlug(candidates.Select(project =>
            new DependencyProjectCandidate(project.Id, project.FromCurseForge ? "CurseForge" : "Modrinth", project.Slug)), dependencyId);
        if (match is null) return new List<ModComp.CompProject>();
        return candidates.Where(project => string.Equals(project.Id, match.Id, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(project.FromCurseForge ? "CurseForge" : "Modrinth", match.Source,
                                               StringComparison.OrdinalIgnoreCase))
            .Take(1)
            .ToList();
    }

    private static List<string> GetLoaderNames(McInstanceInfo info)
    {
        var loaders = new List<string>();
        if (info.HasNeoForge) loaders.Add("NeoForge");
        if (info.HasForge || info.HasCleanroom) loaders.Add("Forge");
        if (info.HasFabric || info.HasLegacyFabric) loaders.Add("Fabric");
        if (info.HasQuilt) loaders.Add("Quilt");
        return loaders;
    }

    private static ModComp.CompLoaderType GetSingleLoader(McInstanceInfo info)
    {
        if (info.HasNeoForge) return ModComp.CompLoaderType.NeoForge;
        if (info.HasForge || info.HasCleanroom) return ModComp.CompLoaderType.Forge;
        if (info.HasFabric || info.HasLegacyFabric) return ModComp.CompLoaderType.Fabric;
        if (info.HasQuilt) return ModComp.CompLoaderType.Quilt;
        return ModComp.CompLoaderType.Any;
    }

    private static List<Issue> Scan(McInstance instance, string modsPath)
    {
        var mods = new List<CompFile>();
        ModJarInJarCache.UseInstance(instance.PathInstance);
        ModLocalComp.JijScanContext = new ModJarInJar.ScanContext();
        try
        {
            foreach (var path in Directory.EnumerateFiles(modsPath)
                         .Where(CompFile.IsModFile))
            {
                var mod = new CompFile(path);
                mod.Load();
                if (mod.State == CompFile.LocalFileStatus.Unavailable) continue;
                if (mod.JijPending && !mod.ResolveJijNow())
                    ModBase.Log("启动前未能完整解析内嵌模组：" + path, ModBase.LogLevel.Developer);
                mods.Add(mod);
            }
            ModJarInJarCache.Flush();
        }
        finally
        {
            ModLocalComp.JijScanContext = null;
            ModJarInJarCache.UseInstance(null);
        }

        var index = new ModJarInJarIndex(mods, instance.Info.VanillaName);
        var issues = new List<Issue>();
        foreach (var mod in mods.Where(mod => mod.State == CompFile.LocalFileStatus.Fine))
        foreach (var dependency in index.GetDependencies(mod)
                     .Where(dependency => !dependency.Optional && !ModDependencyIds.IsPlatform(dependency.DepId)))
        {
            var status = index.Analyze(mod, dependency);
            if (status is JijDepStatus.Installed or JijDepStatus.Bundled) continue;
            issues.Add(new Issue(mod, dependency, status,
                status == JijDepStatus.Disabled ? index.GetDisabledProviders(mod, dependency) : Array.Empty<CompFile>()));
        }
        return issues;
    }

    private static int ShowPrompt(McInstance instance, List<Issue> issues, bool canEnable)
    {
        var result = 0;
        var details = BuildDetails(issues);
        var message = Lang.Text("Minecraft.Launch.Dependency.Message", instance.Name, details);
        ModBase.RunInUiWait(() =>
        {
            result = canEnable
                ? ModMain.MyMsgBox(message, Lang.Text("Minecraft.Launch.Dependency.Title"),
                    Lang.Text("Minecraft.Launch.Dependency.Enable"),
                    Lang.Text("Minecraft.Launch.Dependency.Continue"), Lang.Text("Common.Action.Cancel"))
                : ModMain.MyMsgBox(message, Lang.Text("Minecraft.Launch.Dependency.Title"),
                    Lang.Text("Minecraft.Launch.Dependency.Continue"), Lang.Text("Common.Action.Cancel"));
            if (!canEnable && result == 1) result = 2;
            else if (!canEnable && result == 2) result = 0;
        });
        return result;
    }

    private static string BuildDetails(List<Issue> issues)
    {
        var lines = issues.Take(12).Select(issue =>
        {
            var owner = string.IsNullOrWhiteSpace(issue.Owner.Name) ? issue.Owner.FileName : issue.Owner.Name;
            var status = Lang.Text(issue.Status switch
            {
                JijDepStatus.Disabled => "Instance.Resource.Mod.JarInJar.Dep.Disabled",
                JijDepStatus.VersionMismatch => "Instance.Resource.Mod.JarInJar.Dep.VersionMismatch",
                _ => "Instance.Resource.Mod.JarInJar.Dep.Missing"
            });
            return Lang.Text("Minecraft.Launch.Dependency.Entry", owner, issue.Dependency.DepId,
                issue.Dependency.Raw ?? "*", status);
        }).ToList();
        if (issues.Count > lines.Count)
            lines.Add(Lang.Text("Minecraft.Launch.Dependency.More", issues.Count - lines.Count));
        return string.Join(Environment.NewLine, lines);
    }
}
