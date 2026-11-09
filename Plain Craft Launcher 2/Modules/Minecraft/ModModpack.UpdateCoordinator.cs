using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Packs;
using PCL.Core.UI;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Network.Loaders;
using static PCL.ModLoader;
using PCL.Core.Utils;

namespace PCL;

public static partial class ModModpack
{

    public static bool HasPackUpdateBaseline(string instancePath)
    {
        try { return new PackBaselineStore(Paths.SharedLocalData).Load(instancePath) is { Files.Count: > 0 }; }
        catch (Exception ex) { ModBase.Log(ex, "检查整合包来源记录失败"); return false; }
    }


    /// <summary>Downloads the latest compatible upstream pack archive, stages it as a temporary instance, and applies a three-way update to the selected pack.</summary>
    public static void UpdatePackFromOnline(McInstance instance) => StartPackUpdate(instance, null);


    public static void UpdatePackFromLocal(McInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var archivePath = SystemDialogs.SelectFile(Lang.Text("Minecraft.Download.Modpack.FileDialog.Filter"),
            Lang.Text("Minecraft.Download.Modpack.FileDialog.Title"));
        if (string.IsNullOrWhiteSpace(archivePath)) return;
        var confirm = ModMain.MyMsgBox(Lang.Text("Instance.PackUpdate.Local.UnverifiedMessage"),
            Lang.Text("Instance.PackUpdate.Local.Title"), Lang.Text("Common.Action.Confirm"),
            Lang.Text("Common.Action.Cancel"), isWarn: true);
        if (confirm != 1) return;
        StartPackUpdate(instance, archivePath);
    }


    private static void StartPackUpdate(McInstance instance, string? localArchivePath)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var task = new LoaderTask<int, int>(Lang.Text("Instance.PackUpdate.Task"), loader =>
            _UpdatePackFromOnline(instance, loader, localArchivePath)) { block = false };
        var root = new LoaderCombo<int>(Lang.Text("Instance.PackUpdate.Task"), [task]) { show = false };
        root.Start(0);
        ModLoader.LoaderTaskbarAdd(root);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
    }


    private static void _UpdatePackFromOnline(McInstance instance, LoaderTask<int, int> task, string? localArchivePath)
    {
        var store = new PackBaselineStore(Paths.SharedLocalData);
        var stageName = "PCLLiteUpdate" + Guid.NewGuid().ToString("N");
        var stageVersionPath = Path.Combine(ModFolder.mcFolderSelected, "versions", stageName);
        var stageRoot = Path.Combine(Path.GetDirectoryName(instance.PathInstance.TrimEnd(Path.DirectorySeparatorChar))!, $".pcllite-pack-update-{Guid.NewGuid():N}");
        try
        {
            var gameRoot = Path.GetFullPath(instance.PathIndie).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var instanceVersionPath = Path.GetFullPath(instance.PathInstance).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(gameRoot, instanceVersionPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.IsolationRequired"));
            if (ModWatcher.mcWatcherList.Any(static watcher => watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running))
                throw new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.GameRunning"));
            using (PCL.Core.Minecraft.Instances.InstanceOperationCoordinator.Acquire(
                       Path.Combine(Paths.SharedLocalData, "InstanceOperations"), ModFolder.mcFolderSelected, instance.PathIndie))
            {
                if (ModWatcher.mcWatcherList.Any(watcher => watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running))
                    throw new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.GameRunning"));
                PackUpdateTransaction.RecoverInterrupted(gameRoot);
                PackUpdateDurableTransaction.Recover(gameRoot, Paths.SharedLocalData);
            }

            var previous = store.Load(instance.PathInstance);
            if (previous is null || localArchivePath is null && string.IsNullOrWhiteSpace(previous.ProjectId))
                throw new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.NoBaseline"));
            Directory.CreateDirectory(stageRoot);
            string archivePath;
            if (localArchivePath is null)
            {
                var projects = ModComp.CompRequest.GetCompProjectsByIds([previous.ProjectId]);
                var project = projects.FirstOrDefault()
                    ?? throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.ProjectUnavailable"));
                var releases = ModComp.CompFilesGet(project.Id, project.FromCurseForge)
                    .Where(static file => file.Available && file.Type == ModComp.CompType.ModPack)
                    .OrderByDescending(static file => file.Status == ModComp.CompFileStatus.Release)
                    .ThenByDescending(static file => file.ReleaseDate)
                    .Take(50)
                    .ToList();
                if (releases.Count == 0) throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.NoReleases"));

                var selection = releases.Select(file => (IMyRadio)new MyRadioBox
                {
                    Text = $"{(string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName)}  ·  {file.ReleaseDate:yyyy-MM-dd}  ·  {file.Status}",
                }).ToList();
                var selected = ModBase.RunInUiWait(() => ModMain.MyMsgBoxSelect(selection,
                    Lang.Text("Instance.PackUpdate.SelectVersion.Title"), Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")));
                if (selected is null || selected.Value < 0 || selected.Value >= releases.Count) throw new ModBase.CancelledException();
                var release = releases[selected.Value];
                task.Progress = 0.05d;

                archivePath = Path.Combine(stageRoot, "modpack" + (Path.GetExtension(release.FileName) is { Length: > 1 } extension && extension.Length <= 10 ? extension : ".zip"));
                var packageDownload = release.ToNetFile(archivePath, ModComp.DownloadReason.ModPack,
                    instance.Info.VanillaName, _GetInstanceLoader(instance));
                var packageLoader = new LoaderDownload(Lang.Text("Instance.PackUpdate.Task.Download"), [packageDownload]);
                packageLoader.Start();
                while (packageLoader.State == ModBase.LoadState.Loading)
                {
                    if (task.IsAborted)
                    {
                        packageLoader.Abort();
                        throw new ModBase.CancelledException();
                    }
                    task.Progress = 0.05d + 0.1d * packageLoader.Progress;
                    Thread.Sleep(50);
                }
                if (packageLoader.State != ModBase.LoadState.Finished)
                    throw packageLoader.Error ?? new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.DownloadFailed"));
                task.Progress = 0.15d;
            }
            else
            {
                if (!File.Exists(localArchivePath)) throw new FileNotFoundException(Lang.Text("Instance.PackUpdate.Error.DownloadFailed"), localArchivePath);
                var extension = Path.GetExtension(localArchivePath);
                if (!new[] { ".zip", ".mrpack", ".rar" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException(Lang.Text("Minecraft.Download.Modpack.UnsupportedArchive"));
                archivePath = Path.Combine(stageRoot, "selected-local-package" + extension);
                File.Copy(localArchivePath, archivePath, overwrite: false);
                task.Progress = 0.05d;
            }

            var stagingInstall = ModpackInstall(archivePath, stageName, resourceId: previous.ProjectId, isOnlineInstall: true, forceIsolated: true);
            while (stagingInstall.State == ModBase.LoadState.Loading)
            {
                if (task.IsAborted)
                {
                    stagingInstall.Abort();
                    throw new ModBase.CancelledException();
                }
                task.Progress = 0.15d + stagingInstall.Progress * 0.35d;
                Thread.Sleep(50);
            }
            if (stagingInstall.State != ModBase.LoadState.Finished)
                throw stagingInstall.Error ?? new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.StageFailed"));

            var target = store.Load(stageVersionPath)
                ?? throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.BaselineMissing"));
            if (localArchivePath is not null)
            {
                // The user explicitly supplied this package but its author/source
                // cannot be independently authenticated. Keep the existing project
                // association and derive provenance from the original installed baseline.
                target = target with { Source = previous.Source, ProjectId = previous.ProjectId };
                store.Save(stageVersionPath, target);
                var stagedInstance = new McInstance(stageVersionPath).Load();
                if (!string.Equals(stagedInstance.Info.VanillaName, instance.Info.VanillaName, StringComparison.OrdinalIgnoreCase)
                    || stagedInstance.Info.HasForge != instance.Info.HasForge
                    || stagedInstance.Info.HasNeoForge != instance.Info.HasNeoForge
                    || stagedInstance.Info.HasFabric != instance.Info.HasFabric
                    || stagedInstance.Info.HasLegacyFabric != instance.Info.HasLegacyFabric
                    || stagedInstance.Info.HasQuilt != instance.Info.HasQuilt
                    || stagedInstance.Info.HasCleanroom != instance.Info.HasCleanroom)
                    throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.SourceMismatch"));
            }
            else if (!string.Equals(target.Source, previous.Source, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(target.ProjectId, previous.ProjectId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(Lang.Text("Instance.PackUpdate.Error.SourceMismatch"));
            }

            var payloadPaths = target.Files.Values.ToDictionary(
                static item => item.Path,
                item => PackUpdatePlanner.ResolveWithin(stageVersionPath, item.Path),
                StringComparer.OrdinalIgnoreCase);
            var plan = PackUpdatePlanner.Build(gameRoot, previous, target, payloadPaths);
            var authorCount = plan.Actions.Count(static item => item.Kind is PackUpdateActionKind.InstallAuthorFile or PackUpdateActionKind.RemoveAuthorFile);
            var personalCount = plan.Actions.Count(static item => item.Kind == PackUpdateActionKind.PreservePersonalFile && item.Ownership == PackFileOwnership.PersonalModified);
            if (authorCount == 0 && plan.Warnings.Count == 0)
            {
                ModBase.RunInUi(() => HintService.Hint(Lang.Text("Instance.PackUpdate.Hint.AlreadyCurrent")));
                return;
            }
            var previewActions = plan.Actions
                .Where(static item => item.Kind != PackUpdateActionKind.PreservePersonalFile
                    || item.Ownership is PackFileOwnership.PersonalModified or PackFileOwnership.PersonalAdded)
                .ToList();
            var previewLines = previewActions
                .Take(24)
                .Select(item => $"• {(item.Kind == PackUpdateActionKind.InstallAuthorFile ? "+" : item.Kind == PackUpdateActionKind.RemoveAuthorFile ? "−" : "=")} {item.DestinationPath}"
                    + (item.Kind == PackUpdateActionKind.PreservePersonalFile ? $" ({Lang.Text("Instance.PackUpdate.Preview.Preserve")})" : ""));
            var hiddenLineCount = Math.Max(0, previewActions.Count - 24);
            var preview = Lang.Text("Instance.PackUpdate.Preview", plan.Actions.Count(static item => item.Kind == PackUpdateActionKind.InstallAuthorFile),
                plan.Actions.Count(static item => item.Kind == PackUpdateActionKind.RemoveAuthorFile), personalCount)
                + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, previewLines)
                + (hiddenLineCount > 0 ? Environment.NewLine + $"… {hiddenLineCount}" : "")
                + (plan.Warnings.Count > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, plan.Warnings) : "");
            var decision = ModBase.RunInUiWait(() => ModMain.MyMsgBox(preview, Lang.Text("Instance.PackUpdate.Preview.Title"),
                Lang.Text("Instance.PackUpdate.Preview.Preserve"), Lang.Text("Instance.PackUpdate.Preview.UseAuthor"),
                Lang.Text("Common.Action.Cancel"), isWarn: true));
            if (decision == 3 || decision == 0) throw new ModBase.CancelledException();
            if (decision == 2)
            {
                var authorChoices = previous.Files.Keys.Concat(target.Files.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(static path => path, static _ => PackFileChoice.UseAuthor, StringComparer.OrdinalIgnoreCase);
                plan = PackUpdatePlanner.Build(gameRoot, previous, target, payloadPaths, authorChoices);
            }

            task.Progress = 0.55d;
            using var instanceOperation = PCL.Core.Minecraft.Instances.InstanceOperationCoordinator.Acquire(
                Path.Combine(Paths.SharedLocalData, "InstanceOperations"), ModFolder.mcFolderSelected, instance.PathIndie);
            if (ModWatcher.mcWatcherList.Any(watcher => watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running))
                throw new InvalidOperationException(Lang.Text("Instance.PackUpdate.Error.GameRunning"));
            using var transactionCancellation = new CancellationTokenSource();
            if (task.IsAborted) transactionCancellation.Cancel();
            PackUpdateDurableTransaction.Apply(plan, store, instance.PathInstance, transactionCancellation.Token,
                (completed, total) =>
                {
                    if (task.IsAborted) transactionCancellation.Cancel();
                    task.Progress = total == 0 ? 0.95d : 0.55d + 0.4d * completed / total;
                });
            if (!string.IsNullOrWhiteSpace(target.VersionId)) States.Instance.ModpackVersion[instance.PathInstance] = target.VersionId;
            task.Progress = 1d;
            ModBase.RunInUi(() =>
            {
                ModMain.frmInstanceOverall?.RefreshPackDetails();
                HintService.Hint(Lang.Text("Instance.PackUpdate.Hint.Success", target.VersionId ?? ""), HintType.Success);
            });
        }
        finally
        {
            try
            {
                store.Delete(stageVersionPath);
                if (Directory.Exists(stageVersionPath)) Directory.Delete(stageVersionPath, recursive: true);
            }
            catch (Exception ex) { ModBase.Log(ex, "清理整合包更新暂存实例失败"); }
            try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); }
            catch (Exception ex) { ModBase.Log(ex, "清理整合包更新下载目录失败"); }
        }
    }


    private static ModComp.CompLoaderType _GetInstanceLoader(McInstance instance)
    {
        if (instance.Info.HasNeoForge) return ModComp.CompLoaderType.NeoForge;
        if (instance.Info.HasForge || instance.Info.HasCleanroom) return ModComp.CompLoaderType.Forge;
        if (instance.Info.HasFabric || instance.Info.HasLegacyFabric) return ModComp.CompLoaderType.Fabric;
        if (instance.Info.HasQuilt) return ModComp.CompLoaderType.Quilt;
        return ModComp.CompLoaderType.Any;
    }


    private static void ApplyPackIsolationLayout(string versionFolder, IEnumerable<string> authoredPaths, bool forceIsolated)
    {
        var ini = Path.Combine(versionFolder, "PCL", "Setup.ini");
        var isolated = forceIsolated || PackExportContent.ReadIsolation(ModBase.ReadIni(ini, "VersionArgumentIndieV2", ""),
            ModBase.ReadIni(ini, "VersionArgumentIndie", ""));
        if (forceIsolated)
        {
            ModBase.WriteIni(ini, "VersionArgumentIndie", "1");
            ModBase.WriteIni(ini, "VersionArgumentIndieV2", "True");
        }
        PackInstallationLayout.Apply(versionFolder, ModFolder.mcFolderSelected, authoredPaths, isolated);
        ((PCL.Core.App.Configuration.Storage.DynamicCacheConfigStorage)PCL.Core.App.Configuration.ConfigService
            .GetProvider(PCL.Core.App.Configuration.ConfigSource.GameInstance)).InvalidateCache(versionFolder);
    }


    private static void SaveInstalledPackBaseline(string versionFolder, IEnumerable<string> authoredPaths,
        string source, string? projectId, string? versionId, IReadOnlyDictionary<string, string> identities)
    {
        try
        {
            var store = new PackBaselineStore(Paths.SharedLocalData);
            var baseline = store.Capture(versionFolder, authoredPaths, source, projectId, versionId,
                relative => identities.TryGetValue(relative, out var identity) ? identity : null);
            store.Save(versionFolder, baseline);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "记录整合包来源文件清单失败；当前安装仍可使用，但该实例之后无法进行来源差异更新");
        }
    }

}
