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

/// <summary>Installs into a private sibling first, then switches only explicitly planned live files.</summary>
internal static class InstanceModificationRunner
{
    internal static void Start(McInstance source, string minecraftRoot, ModDownloadLib.McInstallRequest request, bool migrateMods)
    {
        var instance = source.PathInstance; var game = source.PathIndie;
        var loader = new ModLoader.LoaderTask<string, string>(Lang.Text("Instance.Modification.Task", source.Name), task =>
        {
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
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(instance))!;
            var stageName = ".pcl-modify-" + Guid.NewGuid().ToString("N"); var stage = Path.Combine(parent, stageName);
            var marker = Path.Combine(stage, ".pcl-modify-owner"); var identity = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stage); File.WriteAllText(marker, identity);
            var notices = new List<string>();
            try
            {
                File.WriteAllText(Path.Combine(stage, ".pclignore"), "in-place modification staging");
                Directory.CreateDirectory(Path.Combine(stage, "PCL"));
                ModBase.WriteIni(Path.Combine(stage, "PCL", "Setup.ini"), "VersionArgumentIndieV2", "True");
                ModBase.WriteIni(Path.Combine(stage, "PCL", "Setup.ini"), "VersionArgumentJavaSelect", "{\"kind\":\"auto\"}");
                request.targetInstanceName = stageName; request.targetInstanceFolder = stage + Path.DirectorySeparatorChar;
                var steps = ModDownloadLib.McInstallLoader(request);
                if (steps is null) throw new IOException(Lang.Text("Instance.Migration.InstallFailed"));
                InstanceMigrationLoaders.Wait(new ModLoader.LoaderCombo<string>(Lang.Text("Instance.Migration.InstallStage"), steps), request.targetInstanceFolder, task, token);
                var replacements = new List<ModificationModReplacement>();
                var selectedPayloadNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (migrateMods)
                {
                    var targetLoader = TargetLoader(request);
                    if (targetLoader is null) notices.Add(Lang.Text("Instance.Modification.VanillaMods"));
                    else
                    {
                        var preview = InstanceMigrationCatalog.Build(source, request.minecraftName, targetLoader.Value, token);
                        var chosen = ChooseMods(preview);
                        if (chosen is null) throw new OperationCanceledException(token);
                        var stagedMods = Path.Combine(stage, "mods"); Directory.CreateDirectory(stagedMods);
                        foreach (var missing in preview.Plan.Mods.Where(item => item.Action == MigrationModAction.NeedsReview))
                            notices.Add(Path.GetFileName(missing.Source.Path) + ": " + missing.Reason);
                        foreach (var item in chosen)
                        {
                            token.ThrowIfCancellationRequested(); InstanceMigrationRunner.EnsureContext(source, minecraftRoot);
                            var destination = Path.Combine(stagedMods, item.TargetFileName!);
                            var remote = preview.Files[InstanceMigrationCatalog.Key(item.Candidate!)];
                            var targetDownload = remote.ToNetFile(destination, ModComp.DownloadReason.Dependency, request.minecraftName,
                                InstanceMigrationCatalog.ToHostLoader(targetLoader.Value));
                            var alreadyStaged = File.Exists(destination);
                            if (alreadyStaged && (targetDownload.Check is null || targetDownload.Check.Check(destination) is not null))
                            {
                                // The installer may have populated a different Fabric API build. Only this private
                                // stage file may be replaced; no live instance file is touched before the transaction.
                                PackExportContent.CheckAncestors(stage, destination);
                                File.Delete(destination); alreadyStaged = false;
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
                            else if (!alreadyStaged) Download(targetDownload, task, token);
                            replacements.Add(new(item.Source.Path, item.Source.Sha256, destination, item.TargetFileName!));
                            selectedPayloadNames.Add(item.TargetFileName!);
                            if (!item.Disabled)
                            {
                                var project = ModComp.compProjectCache[remote.ProjectId];
                                var dependencies = new ModDependencyResolver().Resolve(ModCompDependency.BuildRequest(remote, project,
                                    request.minecraftName, [InstanceMigrationCatalog.ToHostLoader(targetLoader.Value)], stagedMods));
                                if (dependencies.Unresolved.Count > 0) throw new IOException(Lang.Text("Instance.Migration.DependenciesFailed",
                                    string.Join(", ", dependencies.Unresolved.Select(value => value.ProjectId))));
                                if (dependencies.ToInstall.Count > 0)
                                {
                                    var consent = ModBase.RunInUiWait(() => ModCompDependency.ConfirmDependencyInstall(dependencies));
                                    if (consent != ModComp.CompDepsInstallTypes.WithDeps) throw new OperationCanceledException(token);
                                    foreach (var dependency in ModCompDependency.BuildDependencyDownloads(dependencies, stagedMods))
                                        if (!File.Exists(dependency.File.LocalPath)) Download(dependency.File, task, token);
                                }
                            }
                        }
                    }
                }
                token.ThrowIfCancellationRequested(); InstanceMigrationRunner.EnsureContext(source, minecraftRoot);
                var installed = new McInstance(request.targetInstanceFolder).Load();
                ValidateInstalled(installed, request, minecraftRoot, token);
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
                token.ThrowIfCancellationRequested(); InstanceMigrationRunner.EnsureContext(source, minecraftRoot);
                if (!string.Equals(Path.TrimEndingDirectorySeparator(source.PathIndie), Path.TrimEndingDirectorySeparator(game), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(Lang.Text("Instance.Migration.ContextChanged"));
                InstanceModificationTransaction.Apply(instance, game, plan, token);
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
                try
                {
                    PackExportContent.CheckAncestors(parent, stage); PackExportContent.CheckAncestors(stage, marker);
                    if (File.Exists(marker) && File.ReadAllText(marker) == identity)
                    { _ = EnumerateSafe(stage).ToArray(); Directory.Delete(stage, recursive: true); }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { ModBase.Log(error, "保留无法安全清理的修改暂存目录", ModBase.LogLevel.Developer); }
            }
        });
        loader.OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly;
        var taskGroup = new ModLoader.LoaderCombo<string>(loader.name, [loader]) { show = false };
        taskGroup.Start(instance); ModLoader.LoaderTaskbarAdd(taskGroup);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh(); ModMain.frmMain.BtnExtraDownload.Ribble();
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

    private static void Download(PCL.Network.DownloadFile file, ModLoader.LoaderBase parent, CancellationToken token)
        => InstanceMigrationLoaders.Wait(new LoaderDownload(Lang.Text("Instance.Migration.DownloadStage"), [file]), null, parent, token);

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
