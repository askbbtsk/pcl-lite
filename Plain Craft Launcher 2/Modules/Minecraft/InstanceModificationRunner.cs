using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.Minecraft.Enhancements;
using PCL.Core.Minecraft.InstanceMigration;
using PCL.Core.Minecraft.Instances;
using PCL.Core.Minecraft.Java.UserPreference;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.ResourceCollections;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Network.Loaders;
using PCL.Core.Utils;

namespace PCL;

/// <summary>Installs in an owned OS-temp workspace, then switches only explicitly planned live files.</summary>
internal static class InstanceModificationRunner
{
    internal static void Start(McInstance source, string minecraftRoot, ModDownloadLib.McInstallRequest request, bool migrateMods,
        bool automaticDependencies = false)
    {
        var instance = source.PathInstance; var game = source.PathIndie;
        var presentation = new OperationTaskPresentation(
            (Lang.Text("Instance.Modification.Stage.Index"), 12),
            (Lang.Text("Instance.Modification.Stage.Dependencies"), 18),
            (Lang.Text("Instance.Migration.InstallStage"), 35),
            (Lang.Text("Instance.Modification.Stage.Download"), 25),
            (Lang.Text("Instance.Modification.Stage.Validate"), 7),
            (Lang.Text("Instance.Modification.Stage.Commit"), 3));
        var loader = new ModLoader.LoaderTask<string, string>(Lang.Text("Instance.Modification.Task", source.Name), task =>
        {
            presentation.Begin(0);
            var token = task.AbortedToken;
            using var operation = InstanceOperationCoordinator.Acquire(Path.Combine(Paths.SharedLocalData, "InstanceOperations"), minecraftRoot, game);
            InstanceMigrationRunner.EnsureContext(source, minecraftRoot);
            InstanceModificationTransaction.Recover(instance, game);
            var originalJson = Path.Combine(instance, source.Name + ".json"); var originalJar = Path.Combine(instance, source.Name + ".jar");
            var jsonBefore = InstanceModificationTransaction.HashOrMissing(originalJson);
            var jarBefore = InstanceModificationTransaction.HashOrMissing(originalJar);
            var originalIni = Path.Combine(instance, "PCL", "Setup.ini");
            var iniBefore = InstanceModificationTransaction.HashOrMissing(originalIni);
            var iniBytes = iniBefore is null ? [] : File.ReadAllBytes(originalIni);
            if (!string.Equals(InstanceModificationTransaction.HashOrMissing(originalIni), iniBefore, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Lang.Text("Instance.Migration.SourceChanged", "Setup.ini"));
            if (jsonBefore is null) throw new IOException(Lang.Text("Instance.Migration.SourceChanged", source.Name));
            using var workspace = ModificationWorkspace.Create();
            var stageName = workspace.StageName; var stage = workspace.StageDirectory;
            var notices = new List<string>();
            try
            {
                File.WriteAllText(Path.Combine(stage, ".pclignore"), "in-place modification staging");
                Directory.CreateDirectory(Path.Combine(stage, "PCL"));
                ModBase.WriteIni(Path.Combine(stage, "PCL", "Setup.ini"), "VersionArgumentIndieV2", "True");
                ModBase.WriteIni(Path.Combine(stage, "PCL", "Setup.ini"), "VersionArgumentJavaSelect", "{\"kind\":\"auto\"}");
                request.targetInstanceName = stageName; request.targetInstanceFolder = stage + Path.DirectorySeparatorChar;
                var targetLoader = TargetLoader(request);
                var stagedMods = Path.Combine(stage, "mods");
                InstanceMigrationCatalog.Preview? preview = null;
                IReadOnlyList<MigrationModPlan> chosen = [];
                if (migrateMods && targetLoader is not null)
                {
                    preview = InstanceMigrationCatalog.Build(source, request.minecraftName, targetLoader.Value, token,
                        value => presentation.Report(0, value), detail => presentation.Describe(0, detail));
                    chosen = ChooseMods(preview) ?? throw new OperationCanceledException(token);
                }
                else if (migrateMods) notices.Add(Lang.Text("Instance.Modification.VanillaMods"));
                presentation.Begin(1);
                var prepared = preview is null ? new ModificationDependencyCoordinator.Prepared([], [], [])
                    : ModificationDependencyCoordinator.Prepare(preview, chosen, request.minecraftName,
                        targetLoader!.Value, stagedMods, token, value => presentation.Report(1, value),
                        detail => presentation.Describe(1, detail), automaticDependencies);
                notices.AddRange(prepared.Notices);
                // Do not install or download target mods until all selected roots have been reviewed.
                token.ThrowIfCancellationRequested();
                InstanceMigrationRunner.EnsureContext(source, minecraftRoot);
                presentation.Begin(2);
                var steps = ModDownloadLib.McInstallLoader(request);
                if (steps is null) throw new IOException(Lang.Text("Instance.Migration.InstallFailed"));
                InstanceMigrationLoaders.Wait(new ModLoader.LoaderCombo<string>(Lang.Text("Instance.Migration.InstallStage"), steps),
                    request.targetInstanceFolder, task, token, value => presentation.Report(2, value));
                var replacements = new List<ModificationModReplacement>();
                var selectedPayloadNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                presentation.Begin(3);
                if (preview is not null && targetLoader is not null)
                {
                    Directory.CreateDirectory(stagedMods);
                    var downloads = new List<PCL.Network.DownloadFile>(prepared.Dependencies);
                    foreach (var item in prepared.Mods)
                    {
                        token.ThrowIfCancellationRequested();
                        InstanceMigrationRunner.EnsureContext(source, minecraftRoot);
                        var destination = Path.Combine(stagedMods, item.TargetFileName!);
                        var remote = preview.Files[InstanceMigrationCatalog.Key(item.Candidate!)];
                        var targetDownload = remote.ToNetFile(destination, ModComp.DownloadReason.Dependency, request.minecraftName,
                            InstanceMigrationCatalog.ToHostLoader(targetLoader.Value));
                        var alreadyStaged = File.Exists(destination);
                        if (alreadyStaged && (targetDownload.Check is null || targetDownload.Check.Check(destination) is not null))
                        {
                            // Only an owned stage file may be replaced before the transaction.
                            PackExportContent.CheckAncestors(stage, destination);
                            File.Delete(destination);
                            alreadyStaged = false;
                        }
                        if (!alreadyStaged && item.Action == MigrationModAction.CopyVerifiedLocal)
                        {
                            var copy = ResourceCollectionCopyService.Copy([item.Source.Path], stagedMods, token);
                            if (copy.Cancelled) throw new OperationCanceledException(token);
                            var copied = copy.Items.Single();
                            if (copied.Status is not (ResourceCopyStatus.Copied or ResourceCopyStatus.AlreadyPresent))
                                throw new IOException(copied.Error);
                            if (!string.Equals(copied.DestinationPath, destination, StringComparison.OrdinalIgnoreCase))
                                File.Move(copied.DestinationPath!, destination, overwrite: false);
                        }
                        else if (!alreadyStaged) downloads.Add(targetDownload);
                        replacements.Add(new(item.Source.Path, item.Source.Sha256, destination, item.TargetFileName!));
                        selectedPayloadNames.Add(item.TargetFileName!);
                    }
                    if (downloads.Count > 0)
                        InstanceMigrationLoaders.Wait(new LoaderDownload(Lang.Text("Instance.Modification.Stage.Download"), downloads),
                            null, task, token, value =>
                            {
                                presentation.Report(3, value);
                                presentation.Describe(3, Lang.Text("Instance.Modification.DownloadCount",
                                    downloads.Count(file => file.State == PCL.Network.NetState.Finished), downloads.Count));
                            });
                }
                presentation.Begin(4);
                token.ThrowIfCancellationRequested(); InstanceMigrationRunner.EnsureContext(source, minecraftRoot);
                var installed = new McInstance(request.targetInstanceFolder).Load();
                ValidateInstalled(installed, request, minecraftRoot, token);
                ValidateMigratedMods(installed, token);
                presentation.Report(4, 0.8);
                var stagedJson = Path.Combine(stage, stageName + ".json");
                var json = JsonNode.Parse(File.ReadAllText(stagedJson))!.AsObject();
                json["id"] = source.Name; if (json["jar"]?.ToString() == stageName) json["jar"] = source.Name;
                File.WriteAllText(stagedJson, json.ToJsonString());
                var payloads = new List<ModificationPayload> { new(source.Name + ".json", stagedJson, jsonBefore, true) };
                var adjustedIni = Path.Combine(stage, ".pcl-modify-user-settings.tmp");
                File.WriteAllBytes(adjustedIni, InstanceJavaPreferenceEditor.WithJavaPreference(iniBytes,
                    JsonSerializer.Serialize<JavaPreference>(new AutoSelect(), JsonCompat.SerializerOptions)));
                payloads.Add(new(Path.Combine("PCL", "Setup.ini"), adjustedIni, iniBefore, true));
                var stagedJar = Path.Combine(stage, stageName + ".jar");
                if (File.Exists(stagedJar)) payloads.Add(new(source.Name + ".jar", stagedJar, jarBefore));
                var localLibs = Path.Combine(stage, "libraries");
                if (Directory.Exists(localLibs))
                    foreach (var library in EnumerateSafe(localLibs))
                    {
                        var relative = Path.Combine("libraries", Path.GetRelativePath(localLibs, library));
                        var old = InstanceModificationTransaction.HashOrMissing(Path.Combine(instance, relative));
                        var fresh = InstanceModificationTransaction.HashOrMissing(library);
                        if (old is not null && old != fresh) throw new IOException(Lang.Text("Instance.Migration.TargetConflict", relative));
                        if (old != fresh) payloads.Add(new(relative, library, old));
                    }
                var additional = new List<ModificationPayload>(); var mods = Path.Combine(stage, "mods");
                if (Directory.Exists(mods))
                    foreach (var file in Directory.EnumerateFiles(mods).Where(ModLocalComp.LocalCompFile.IsModFile))
                        if (!selectedPayloadNames.Contains(Path.GetFileName(file))) additional.Add(new(Path.Combine("mods", Path.GetFileName(file)), file, null));
                var plan = InstanceModificationTransaction.Prepare(instance, game, payloads, replacements, additional);
                if (plan.RetiredFiles.Count > 0)
                {
                    var confirmed = ModBase.RunInUiWait(() => ModMain.MyMsgBox(
                        Lang.Text("Instance.Modification.ReplacePreview", string.Join(Environment.NewLine, plan.RetiredFiles)),
                        Lang.Text("Instance.Migration.Title"), Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true));
                    if (confirmed != 1) throw new OperationCanceledException(token);
                }
                presentation.Begin(5);
                token.ThrowIfCancellationRequested(); InstanceMigrationRunner.EnsureContext(source, minecraftRoot);
                if (!string.Equals(Path.TrimEndingDirectorySeparator(source.PathIndie), Path.TrimEndingDirectorySeparator(game), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(Lang.Text("Instance.Migration.ContextChanged"));
                InstanceModificationTransaction.Apply(instance, game, plan, token,
                    (completed, total) => presentation.Report(5, total == 0 ? 0 : 0.98 * completed / total));
                presentation.Complete(5);
                try
                {
                    ModBase.IniClearCache(originalIni);
                    ((DynamicCacheConfigStorage)ConfigService.GetProvider(ConfigSource.GameInstance)).InvalidateCache(instance);
                }
                catch (Exception error)
                { ModBase.Log(error, "修改已提交，但刷新实例设置缓存失败", ModBase.LogLevel.Developer); }
                task.output = instance;
                var result = new StringBuilder(Lang.Text("Instance.Modification.Success", source.Name));
                foreach (var notice in notices) result.AppendLine().Append(notice);
                foreach (var retired in plan.RetiredFiles) result.AppendLine().Append(retired);
                ModBase.RunInUi(() =>
                {
                    source.JsonText = null;
                    source.JsonObject = null;
                    source.Info = null;
                    source.Load();
                    ModMain.MyMsgBox(result.ToString(), Lang.Text("Instance.Migration.Title"));
                    ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, minecraftRoot, ModLoader.LoaderFolderRunType.ForceRun, 1, "versions\\");
                });
            }
            finally
            {
                workspace.Dispose();
                if (workspace.CleanupError is { } error)
                    ModBase.Log(error, $"暂存目录未能立即清理，将在下次启动重试：{workspace.Root}", ModBase.LogLevel.Developer);
            }
        });
        loader.OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly;
        var taskGroup = presentation.Wrap(loader.name, loader);
        taskGroup.Start(instance); ModLoader.LoaderTaskbarAdd(taskGroup);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh(); ModMain.frmMain.BtnExtraDownload.Ribble();
        ModMain.frmMain.PageChange(FormMain.PageType.TaskManager);
    }

    private static MigrationLoader? TargetLoader(ModDownloadLib.McInstallRequest request)
        => request.neoForgeEntry is not null || request.neoForgeVersion is not null ? MigrationLoader.NeoForge
            : request.forgeEntry is not null || request.forgeVersion is not null ? MigrationLoader.Forge
            : request.fabricVersion is not null ? MigrationLoader.Fabric : null;

    private static IReadOnlyList<MigrationModPlan>? ChooseMods(InstanceMigrationCatalog.Preview preview)
    {
        var applicable = preview.Plan.Mods.Where(item => item.Action != MigrationModAction.NeedsReview).ToArray();
        if (applicable.Length == 0) return [];
        return ModBase.RunInUiWait(() =>
        {
            var rows = applicable.Select(item =>
            {
                var row = new MyListItem { Title = Path.GetFileName(item.Source.Path), Info = item.TargetFileName,
                    Height = 44, Type = MyListItem.CheckType.CheckBox };
                row.SetChecked(true, false, false); return row;
            }).ToList();
            var selected = ModMain.MyMsgBoxMultiSelect(rows, Lang.Text("Instance.Migration.SelectMods"),
                Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"));
            return selected?.Where(index => index >= 0 && index < applicable.Length).Select(index => applicable[index]).ToArray();
        });
    }

    private static void ValidateInstalled(McInstance installed, ModDownloadLib.McInstallRequest request, string root, CancellationToken token)
    {
        if (installed.Info.VanillaName != request.minecraftName) throw new IOException(Lang.Text("Instance.Migration.InstallFailed"));
        var targetLoader = TargetLoader(request);
        if ((targetLoader == MigrationLoader.Fabric && !installed.Info.HasFabric) || (targetLoader == MigrationLoader.Forge && !installed.Info.HasForge)
            || (targetLoader == MigrationLoader.NeoForge && !installed.Info.HasNeoForge)) throw new IOException(Lang.Text("Instance.Migration.InstallFailed"));
        var libraries = ModLibrary.McLibListGet(installed, true);
        var report = InstanceEnhancementAnalyzer.Analyze(new()
        {
            InstanceDirectory = installed.PathInstance, EffectiveVersionJson = installed.JsonObject.ToJsonString(), AllowedLibraryRoots = [root],
            Libraries = libraries.Select(file => new EnhancementLibrary(file.LocalPath, file.OriginalName, file.Sha1,
                file.size > 0 ? file.size : null, IsLocal: file.IsLocal)).ToArray(),
        }, token);
        if (report.Libraries.Any(file => file.Status is not (LibraryHealthStatus.Valid or LibraryHealthStatus.Unverified)))
            throw new IOException(Lang.Text("Instance.Migration.InstallFailed"));
        if (!InstanceMigrationJava.SelectForTarget(installed)) throw new IOException(Lang.Text("Instance.Migration.JavaFailed"));
    }

    private static void ValidateMigratedMods(McInstance installed, CancellationToken token)
    {
        var directory = Path.Combine(installed.PathIndie, "mods");
        if (!Directory.Exists(directory)) return;
        var mods = new List<ModLocalComp.LocalCompFile>();
        foreach (var path in Directory.EnumerateFiles(directory).Where(ModLocalComp.LocalCompFile.IsModFile))
        {
            token.ThrowIfCancellationRequested();
            var mod = new ModLocalComp.LocalCompFile(path) { PreferredLoader = ModLocalComp.GetPreferredLoader(installed) };
            mod.Load();
            if (mod.State == ModLocalComp.LocalCompFile.LocalFileStatus.Unavailable)
                throw new InvalidDataException(Lang.Text("Instance.Migration.DependenciesFailed", Path.GetFileName(path)));
            if (mod.JijPending && !mod.ResolveJijNow())
                throw new InvalidDataException(Lang.Text("Instance.Migration.DependenciesFailed", Path.GetFileName(path)));
            mods.Add(mod);
        }
        var index = new ModJarInJarIndex(mods, installed.Info.VanillaName);
        foreach (var mod in mods.Where(item => item.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine))
        foreach (var dependency in index.GetDependencies(mod).Where(dependency => !dependency.Optional
                     && !ModDependencyIds.IsPlatform(dependency.DepId)))
        {
            token.ThrowIfCancellationRequested();
            if (index.Analyze(mod, dependency) is not (JijDepStatus.Installed or JijDepStatus.Bundled))
                throw new InvalidDataException(Lang.Text("Instance.Migration.DependenciesFailed",
                    mod.FileName + " → " + dependency.DepId));
        }
        foreach (var conflict in index.FindActiveConflicts().Where(conflict => conflict.Hard))
            throw new InvalidDataException(Lang.Text("Instance.Migration.DependenciesFailed",
                conflict.A.FileName + " / " + conflict.B.FileName));
    }

    private static IEnumerable<string> EnumerateSafe(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
            foreach (var child in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                PackExportContent.CheckAncestors(root, child);
                if (Directory.Exists(child)) pending.Push(child); else yield return child;
            }
    }
}
