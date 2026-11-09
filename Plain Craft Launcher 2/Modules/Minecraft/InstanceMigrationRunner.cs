using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Enhancements;
using PCL.Core.Minecraft.InstanceMigration;
using PCL.Core.Minecraft.Instances;
using PCL.Core.Minecraft.Java.UserPreference;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.ResourceCollections;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

internal static class InstanceMigrationRunner
{
    internal static void Start(McInstance source, string minecraftRoot, string targetName,
        InstanceMigrationCatalog.Preview preview, ModDownloadLib.McInstallRequest template, IReadOnlyList<string> optionalContent)
    {
        var targetDirectory = Path.Combine(minecraftRoot, "versions", targetName);
        var sourceGame = source.PathIndie;
        var loader = new ModLoader.LoaderTask<string, string>(Lang.Text("Instance.Migration.Task", source.Name, targetName), task =>
        {
            var token = task.AbortedToken;
            using var operation = InstanceOperationCoordinator.Acquire(Path.Combine(Paths.SharedLocalData, "InstanceOperations"), minecraftRoot, sourceGame);
            EnsureContext(source, minecraftRoot);
            var outcome = InstanceMigrationTransaction.ExecuteAsync(source.PathInstance, targetDirectory, preview.Plan,
                (stage, cancellation) =>
                {
                    EnsureContext(source, minecraftRoot);
                    Directory.CreateDirectory(Path.Combine(stage.StageDirectory, "PCL"));
                    var ini = Path.Combine(stage.StageDirectory, "PCL", "Setup.ini");
                    ModBase.WriteIni(ini, "VersionArgumentIndieV2", "True");
                    ModBase.WriteIni(ini, "VersionArgumentIndie", "1");
                    ModBase.WriteIni(ini, "VersionArgumentJavaSelect", JsonSerializer.Serialize<JavaPreference>(new AutoSelect(), JsonCompat.SerializerOptions));
                    template.targetInstanceName = stage.StageName;
                    template.targetInstanceFolder = stage.StageDirectory + Path.DirectorySeparatorChar;
                    var steps = ModDownloadLib.McInstallLoader(template);
                    if (steps is null) throw new IOException(Lang.Text("Instance.Migration.InstallFailed"));
                    var install = new ModLoader.LoaderCombo<string>(Lang.Text("Instance.Migration.InstallStage"), steps);
                    InstanceMigrationLoaders.Wait(install, template.targetInstanceFolder, task, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    task.Progress = 0.65;
                    return Task.CompletedTask;
                },
                (stage, item, cancellation) =>
                {
                    EnsureContext(source, minecraftRoot);
                    var mods = Path.Combine(stage.StageDirectory, "mods"); Directory.CreateDirectory(mods);
                    var destination = Path.Combine(mods, item.TargetFileName!);
                    if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException(Lang.Text("Instance.Migration.TargetConflict", item.TargetFileName));
                    if (item.Action == MigrationModAction.CopyVerifiedLocal)
                    {
                        var copied = ResourceCollectionCopyService.Copy([item.Source.Path], mods, cancellation);
                        if (copied.Cancelled) throw new OperationCanceledException(cancellation);
                        var file = copied.Items.Single();
                        if (file.Status is not (ResourceCopyStatus.Copied or ResourceCopyStatus.AlreadyPresent)
                            || !string.Equals(file.Sha256, item.Source.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException(Lang.Text("Instance.Migration.SourceChanged", Path.GetFileName(item.Source.Path)));
                        if (!string.Equals(file.DestinationPath, destination, StringComparison.OrdinalIgnoreCase))
                            File.Move(file.DestinationPath!, destination, overwrite: false);
                    }
                    else
                    {
                        var remote = preview.Files[InstanceMigrationCatalog.Key(item.Candidate!)];
                        Download(remote.ToNetFile(destination, ModComp.DownloadReason.Dependency, preview.Plan.MinecraftVersion,
                            InstanceMigrationCatalog.ToHostLoader(preview.Plan.TargetLoader)), task, cancellation);
                    }
                    // Disabled roots do not cause their dependencies to be activated in the new instance.
                    if (!item.Disabled)
                    {
                        var remote = preview.Files[InstanceMigrationCatalog.Key(item.Candidate!)];
                        if (!ModComp.compProjectCache.TryGetValue(remote.ProjectId, out var project))
                            throw new IOException(Lang.Text("Instance.Migration.LookupFailed"));
                        var request = ModCompDependency.BuildRequest(remote, project, preview.Plan.MinecraftVersion,
                            [InstanceMigrationCatalog.ToHostLoader(preview.Plan.TargetLoader)], mods);
                        var dependencies = new ModDependencyResolver().Resolve(request);
                        if (dependencies.Unresolved.Count > 0)
                            throw new IOException(Lang.Text("Instance.Migration.DependenciesFailed", string.Join(", ", dependencies.Unresolved.Select(value => value.ProjectId))));
                        if (dependencies.ToInstall.Count > 0)
                        {
                            var consent = ModBase.RunInUiWait(() => ModCompDependency.ConfirmDependencyInstall(dependencies));
                            if (consent != ModComp.CompDepsInstallTypes.WithDeps) throw new OperationCanceledException(cancellation);
                            var downloads = ModCompDependency.BuildDependencyDownloads(dependencies, mods);
                            if (downloads.Count != dependencies.ToInstall.Count) throw new IOException(Lang.Text("Instance.Migration.LookupFailed"));
                            foreach (var dependency in downloads)
                            {
                                if (File.Exists(dependency.File.LocalPath) || Directory.Exists(dependency.File.LocalPath))
                                    throw new IOException(Lang.Text("Instance.Migration.TargetConflict", dependency.Filename));
                                Download(dependency.File, task, cancellation);
                            }
                        }
                    }
                    return Task.CompletedTask;
                },
                (stage, cancellation) => Task.FromResult(Validate(source, minecraftRoot, stage, preview.Plan, cancellation)),
                (stage, cancellation) =>
                {
                    EnsureContext(source, minecraftRoot);
                    InstanceMigrationContent.Copy(sourceGame, stage.StageDirectory, optionalContent, cancellation);
                    return Task.CompletedTask;
                }, token).GetAwaiter().GetResult();
            ModBase.Log("[Migration] " + JsonSerializer.Serialize(outcome), ModBase.LogLevel.Normal);
            if (outcome.Cancelled) throw new OperationCanceledException(token);
            task.output = outcome.Success ? outcome.TargetDirectory! : "";
            var details = new StringBuilder(Lang.Text(outcome.Success ? "Instance.Migration.Success" : "Instance.Migration.Failed", targetName));
            foreach (var problem in outcome.Problems) details.AppendLine().Append(problem);
            if (outcome.RetainedStageDirectory is not null && Directory.Exists(outcome.RetainedStageDirectory))
                details.AppendLine().Append(outcome.RetainedStageDirectory);
            ModBase.RunInUi(() =>
            {
                ModMain.MyMsgBox(details.ToString(), Lang.Text("Instance.Migration.Title"), isWarn: !outcome.Success);
                if (outcome.Success) ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, minecraftRoot,
                    ModLoader.LoaderFolderRunType.ForceRun, 1, "versions\\");
            });
            if (!outcome.Success) throw new IOException(Lang.Text("Instance.Migration.Failed", targetName));
        });
        loader.OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly;
        var taskGroup = new ModLoader.LoaderCombo<string>(loader.name, [loader]) { show = false };
        taskGroup.Start(targetDirectory);
        ModLoader.LoaderTaskbarAdd(taskGroup);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
    }

    private static void Download(DownloadFile file, ModLoader.LoaderBase parent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var download = new LoaderDownload(Lang.Text("Instance.Migration.DownloadStage"), [file]);
        InstanceMigrationLoaders.Wait(download, null, parent, token);
        token.ThrowIfCancellationRequested();
    }

    private static IReadOnlyList<string> Validate(McInstance source, string minecraftRoot, MigrationStage stage,
        InstanceMigrationPlan plan, CancellationToken token)
    {
        EnsureContext(source, minecraftRoot);
        var target = new McInstance(stage.StageDirectory + Path.DirectorySeparatorChar).Load();
        var expectedLoader = plan.TargetLoader switch
        {
            MigrationLoader.Fabric => target.Info.HasFabric,
            MigrationLoader.Forge => target.Info.HasForge,
            MigrationLoader.NeoForge => target.Info.HasNeoForge,
            _ => false,
        };
        var errors = new List<string>();
        if (!expectedLoader || target.Info.VanillaName != plan.MinecraftVersion) errors.Add(Lang.Text("Instance.Migration.InstallFailed"));
        var libraries = ModLibrary.McLibListGet(target, true);
        var health = InstanceEnhancementAnalyzer.Analyze(new()
        {
            InstanceDirectory = stage.StageDirectory, EffectiveVersionJson = target.JsonObject.ToJsonString(),
            MinecraftVersion = plan.MinecraftVersion, AllowedLibraryRoots = [minecraftRoot],
            Libraries = libraries.Select(file => new EnhancementLibrary(file.LocalPath, file.OriginalName, file.Sha1,
                file.size > 0 ? file.size : null, IsLocal: file.IsLocal)).ToArray(),
        }, token);
        foreach (var library in health.Libraries.Where(file => file.Status is not (LibraryHealthStatus.Valid or LibraryHealthStatus.Unverified)))
            errors.Add(Lang.Text("Instance.Migration.LibraryFailed", library.Path, library.Status.ToString()));
        var modsPath = Path.Combine(stage.StageDirectory, "mods");
        var mods = Directory.Exists(modsPath) ? Directory.EnumerateFiles(modsPath).Where(ModLocalComp.LocalCompFile.IsModFile)
            .Select(path => new ModLocalComp.LocalCompFile(path)).ToList() : [];
        foreach (var mod in mods) { token.ThrowIfCancellationRequested(); mod.Load(); if (mod.JijPending) mod.ResolveJijNow(); }
        var index = new ModJarInJarIndex(mods, plan.MinecraftVersion);
        foreach (var mod in mods.Where(file => file.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine))
        foreach (var dependency in index.GetDependencies(mod).Where(dependency => !dependency.Optional && !ModDependencyIds.IsPlatform(dependency.DepId)))
            if (index.Analyze(mod, dependency) is not (JijDepStatus.Installed or JijDepStatus.Bundled))
                errors.Add(Lang.Text("Instance.Migration.DependenciesFailed", mod.FileName + " → " + dependency.DepId));
        foreach (var conflict in index.FindActiveConflicts().Where(conflict => conflict.Hard))
            errors.Add(Lang.Text("Instance.Migration.DependenciesFailed", conflict.A.FileName + " / " + conflict.B.FileName));
        if (!InstanceMigrationJava.SelectForTarget(target)) errors.Add(Lang.Text("Instance.Migration.JavaFailed"));
        return errors;
    }

    internal static void EnsureContext(McInstance source, string minecraftRoot)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(ModFolder.mcFolderSelected), Path.TrimEndingDirectorySeparator(minecraftRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException(Lang.Text("Instance.Migration.ContextChanged"));
        if (ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading)
            throw new IOException(Lang.Text("Instance.Migration.ContextChanged"));
        if (ModWatcher.mcWatcherList.ToArray().Any(watcher => watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running
            && watcher.version is not null && (string.Equals(watcher.version.PathInstance, source.PathInstance, StringComparison.OrdinalIgnoreCase)
                || Path.GetFullPath(watcher.version.PathInstance).StartsWith(Path.GetFullPath(Path.Combine(minecraftRoot, "versions")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            throw new IOException(Lang.Text("Instance.PackUpdate.Error.GameRunning"));
    }
}
