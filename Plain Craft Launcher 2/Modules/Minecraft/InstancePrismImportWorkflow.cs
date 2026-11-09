using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Enhancements;
using PCL.Core.Minecraft.Instances;
using PCL.Core.Minecraft.Java.UserPreference;
using PCL.Core.Minecraft.PrismImport;
using PCL.Core.Utils;
using PCL.Network.Loaders;

namespace PCL;

internal static class InstancePrismImportWorkflow
{
    internal static void Start(PrismReadResult source, string targetName, string minecraftRoot)
    {
        var target = Path.Combine(minecraftRoot, "versions", targetName);
        var loader = new ModLoader.LoaderTask<string, string>(Lang.Text("Instance.Prism.Task", targetName), task =>
        {
            var token = task.AbortedToken;
            using var operation = InstanceOperationCoordinator.Acquire(Path.Combine(Paths.SharedLocalData, "InstanceOperations"), minecraftRoot);
            EnsureAvailable(minecraftRoot);
            PrismCopyResult? copied = null;
            PrismImportTransaction.ExecuteAsync(source, target,
                (stage, cancellation) =>
                {
                    EnsureAvailable(minecraftRoot);
                    copied = PrismImportContent.Copy(source, stage.Directory, cancellation);
                    Directory.CreateDirectory(Path.Combine(stage.Directory, "PCL"));
                    var ini = Path.Combine(stage.Directory, "PCL", "Setup.ini");
                    ModBase.WriteIni(ini, "VersionArgumentIndieV2", "True");
                    ModBase.WriteIni(ini, "VersionArgumentIndie", "1");
                    ModBase.WriteIni(ini, "VersionArgumentJavaSelect", JsonSerializer.Serialize<JavaPreference>(new AutoSelect(), JsonCompat.SerializerOptions));
                    return Task.CompletedTask;
                },
                (stage, cancellation) =>
                {
                    var request = new ModDownloadLib.McInstallRequest
                    {
                        targetInstanceName = stage.Name, targetInstanceFolder = stage.Directory + Path.DirectorySeparatorChar,
                        minecraftName = source.MinecraftVersion,
                        fabricVersion = source.LoaderUid == "net.fabricmc.fabric-loader" ? source.LoaderVersion : null,
                        forgeVersion = source.LoaderUid == "net.minecraftforge" ? source.LoaderVersion : null,
                        neoForgeVersion = source.LoaderUid == "net.neoforged" ? source.LoaderVersion : null,
                    };
                    var steps = ModDownloadLib.McInstallLoader(request);
                    if (steps is null) throw new IOException(Lang.Text("Instance.Prism.InstallFailed"));
                    InstanceMigrationLoaders.Wait(new ModLoader.LoaderCombo<string>(Lang.Text("Instance.Prism.InstallStage"), steps),
                        request.targetInstanceFolder, task, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    var jsonPath = Path.Combine(stage.Directory, stage.Name + ".json");
                    var installed = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject
                        ?? throw new InvalidDataException(Lang.Text("Instance.Prism.InstallFailed"));
                    var composed = PrismPatchComposer.Apply(installed, source);
                    File.WriteAllText(jsonPath, composed.ToJsonString());
                    // Patches may add new artifacts; use the existing downloader/checkers for those too.
                    var instance = new McInstance(request.targetInstanceFolder).Load();
                    var libraries = ModLibrary.McLibNetFilesFromInstance(instance);
                    if (libraries.Count > 0)
                        InstanceMigrationLoaders.Wait(new LoaderDownload(Lang.Text("Instance.Prism.LibrariesStage"), libraries), null, task, cancellation);
                    ValidateTarget(source, stage, minecraftRoot, cancellation);
                    if (copied is not null) PrismImportContent.VerifySource(source, copied, cancellation);
                    WriteDeclarations(source, stage);
                    EnsureAvailable(minecraftRoot);
                    return Task.CompletedTask;
                }, token).GetAwaiter().GetResult();
            task.output = target;
            var report = new StringBuilder(Lang.Text("Instance.Prism.Success", targetName));
            foreach (var warning in source.Warnings) report.AppendLine().Append(warning);
            if (copied is not null)
                foreach (var excluded in copied.Skipped) report.AppendLine().Append(Lang.Text("Instance.Prism.Skipped", excluded));
            ModBase.Log("[PrismImport] " + report, ModBase.LogLevel.Normal);
            ModBase.RunInUi(() =>
            {
                ModMain.MyMsgBox(report.ToString(), Lang.Text("Instance.Prism.Title"));
                ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, minecraftRoot, ModLoader.LoaderFolderRunType.ForceRun, 1, "versions\\");
            });
        });
        loader.OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly;
        var taskGroup = new ModLoader.LoaderCombo<string>(loader.name, [loader]) { show = false };
        taskGroup.Start(target); ModLoader.LoaderTaskbarAdd(taskGroup);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh(); ModMain.frmMain.BtnExtraDownload.Ribble();
    }

    private static void ValidateTarget(PrismReadResult source, PrismImportStage stage, string minecraftRoot, CancellationToken token)
    {
        var target = new McInstance(stage.Directory + Path.DirectorySeparatorChar).Load();
        if (target.Info.VanillaName != source.MinecraftVersion
            || (source.LoaderUid == "net.fabricmc.fabric-loader" && !target.Info.HasFabric)
            || (source.LoaderUid == "net.minecraftforge" && !target.Info.HasForge)
            || (source.LoaderUid == "net.neoforged" && !target.Info.HasNeoForge))
            throw new IOException(Lang.Text("Instance.Prism.InstallFailed"));
        var tokens = ModLibrary.McLibListGet(target, true);
        foreach (var component in source.Components.Where(component => !component.Disabled && (component.Uid is "org.lwjgl" or "org.lwjgl3")))
            if (!tokens.Any(library => (library.OriginalName?.StartsWith("org.lwjgl:", StringComparison.Ordinal) == true
                    || (component.Uid == "org.lwjgl" && library.OriginalName?.StartsWith("org.lwjgl.lwjgl:", StringComparison.Ordinal) == true))
                && library.OriginalName.Split(':').ElementAtOrDefault(2) == component.Version))
                throw new IOException(Lang.Text("Instance.Prism.LwjglMismatch", component.Version));
        var health = InstanceEnhancementAnalyzer.Analyze(new()
        {
            InstanceDirectory = stage.Directory, EffectiveVersionJson = target.JsonObject.ToJsonString(),
            MinecraftVersion = source.MinecraftVersion, AllowedLibraryRoots = [minecraftRoot],
            Libraries = tokens.Select(file => new EnhancementLibrary(file.LocalPath, file.OriginalName, file.Sha1,
                file.size > 0 ? file.size : null, IsLocal: file.IsLocal)).ToArray(),
        }, token);
        var failed = health.Libraries.Where(library => library.Status is not (LibraryHealthStatus.Valid or LibraryHealthStatus.Unverified)).ToArray();
        if (failed.Length > 0) throw new IOException(Lang.Text("Instance.Prism.LibrariesFailed", string.Join(", ", failed.Select(library => library.Path))));
        if (!InstanceMigrationJava.SelectForTarget(target)) throw new IOException(Lang.Text("Instance.Prism.JavaFailed"));
    }

    private static void WriteDeclarations(PrismReadResult source, PrismImportStage stage)
    {
        var patchReport = new JsonArray();
        foreach (var patch in source.Patches)
            patchReport.Add(new JsonObject { ["fileName"] = patch.FileName, ["order"] = patch.Order,
                ["sourceOrdinal"] = patch.SourceOrdinal, ["metadata"] = patch.Original.DeepClone() });
        var original = new JsonObject { ["manifest"] = source.OriginalManifest.DeepClone(), ["patches"] = patchReport,
            ["unmappedSettingKeys"] = new JsonArray(source.InstanceSettings.Keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()) };
        var document = new InstanceEnhancementDocument
        {
            InstanceId = Guid.NewGuid(), Components = source.Components.Select(component => new ComponentDescriptor
            {
                Id = component.Uid, Version = component.Version, Important = component.Important,
                DependencyOnly = component.DependencyOnly, Disabled = component.Disabled, Source = "Prism-import",
                ExtensionData = new() { ["prismOriginal"] = JsonSerializer.SerializeToElement(component.Original) },
            }).ToList(),
            ExtensionData = new() { ["prismImport"] = JsonSerializer.SerializeToElement(original) },
        };
        new InstanceEnhancementStore(stage.Directory).SaveDeclaration(document);
    }

    private static void EnsureAvailable(string minecraftRoot)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(ModFolder.mcFolderSelected), Path.TrimEndingDirectorySeparator(minecraftRoot), StringComparison.OrdinalIgnoreCase)
            || ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading || ModWatcher.hasRunningMinecraft)
            throw new IOException(Lang.Text("Instance.Prism.Busy"));
    }
}
