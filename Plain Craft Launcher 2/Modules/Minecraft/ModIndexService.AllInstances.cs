using System.IO;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Resources;
using PCL.Core.Utils;

namespace PCL;

internal static partial class ModIndexService
{
    internal static void CreateAllNow()
    {
        var successful = 0;
        var partial = 0;
        var skipped = new System.Collections.Concurrent.ConcurrentQueue<ModIndexFileFailure>();
        var failed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var completed = NativeWorkDialog.RunWithStatus(Lang.Text("Setup.GameManage.ModIndex.Create"),
            Lang.Text("Setup.GameManage.ModIndex.Discovering"), async (token, status) =>
            {
                if (ModFolder.mcFolderListLoader.State != ModBase.LoadState.Finished)
                {
                    ModFolder.mcFolderListLoader.Start();
                    while (ModFolder.mcFolderListLoader.State == ModBase.LoadState.Loading)
                        await Task.Delay(100, token).ConfigureAwait(false);
                    if (ModFolder.mcFolderListLoader.State != ModBase.LoadState.Finished)
                        failed.Enqueue(Lang.Text("Setup.GameManage.ModIndex.Discovering"));
                }
                token.ThrowIfCancellationRequested();
                var roots = ModBase.RunInUiWait(() => ModFolder.mcFolderList.Select(folder => folder.Location)
                    .Append(ModFolder.mcFolderSelected).Where(root => !string.IsNullOrWhiteSpace(root))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                var targets = new List<ModIndexTarget>();
                var instances = new Dictionary<string, McInstance>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var versions = Path.Combine(root, "versions");
                        if (!Directory.Exists(versions)) continue;
                        foreach (var folder in new DirectoryInfo(versions).EnumerateDirectories())
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                // Match native exclusions without switching selection or deleting ignore markers.
                                if (!folder.EnumerateFiles().Any() || File.Exists(Path.Combine(folder.FullName, ".pclignore"))) continue;
                                if ((folder.Name is "cache" or "BLClient" or "PCL")
                                    && !File.Exists(Path.Combine(folder.FullName, folder.Name + ".json"))) continue;
                                var instance = new McInstance(folder.FullName).Load();
                                if (instance.state == McInstanceState.Error)
                                {
                                    failed.Enqueue(instance.PathInstance);
                                    continue;
                                }
                                var directory = ModsDirectory(instance);
                                var context = string.Join("|", ModLocalComp.GetPreferredLoader(instance) ?? "",
                                    instance.Info.VanillaName, instance.Info.HasForge && instance.Info.Drop < 130);
                                instances[instance.PathInstance] = instance;
                                targets.Add(new(instance.PathInstance, directory, context));
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error)
                            {
                                failed.Enqueue(folder.FullName);
                                ModBase.Log(error, $"索引发现实例失败：{folder.FullName}", ModBase.LogLevel.Developer);
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        failed.Enqueue(root);
                        ModBase.Log(error, $"索引读取 Minecraft 目录失败：{root}", ModBase.LogLevel.Developer);
                    }
                }
                var groups = ModIndexBatchPlan.Group(targets);
                var count = groups.Sum(group => group.Count);
                var done = 0;
                token.ThrowIfCancellationRequested();
                ModLocalComp.CancelBackgroundIndex();
                return await Builds.RunForegroundAsync(async cancellation =>
                {
                    foreach (var group in groups)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var instance = instances[group[0].InstancePath];
                        void Report(int files) => status.Report(Lang.Text("Setup.GameManage.ModIndex.AllProgress",
                            done, count, instance.Name, files));
                        Report(0);
                        ModIndexBuildResult built;
                        try
                        {
                            built = await BuildCoreAsync(instance, cancellation,
                                new NativeWorkDialog.StatusProgress<int>(Report), null).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            built = new(false, 0, [], error.Message);
                            ModBase.Log(error, $"建立实例索引失败：{instance.PathInstance}", ModBase.LogLevel.Developer);
                        }
                        foreach (var file in built.SkippedFiles) skipped.Enqueue(file);
                        foreach (var target in group)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var current = instances[target.InstancePath];
                            Register(current);
                            // Incomplete files do not invalidate valid entries or prevent shared-cache reuse.
                            var saved = built.CacheSaved && (ReferenceEquals(current, instance)
                                || ModJarInJarCache.CopySnapshot(instance.PathInstance, current.PathInstance));
                            if (saved)
                            {
                                Interlocked.Increment(ref successful);
                                if (built.SkippedFiles.Count > 0) Interlocked.Increment(ref partial);
                                else Policy.AllowPromptAgain(current.PathInstance);
                            }
                            else failed.Enqueue(current.PathInstance
                                + (built.SaveError is { Length: > 0 } ? " — " + built.SaveError : ""));
                            done++;
                        }
                        Report(0);
                    }
                    return failed.Count == 0 && count > 0;
                }, token).ConfigureAwait(false);
            }, () => ModMain.frmMain?.IsLoaded == true);

        HintService.Hint(Lang.Text("Setup.GameManage.ModIndex.AllResult", successful, partial,
                skipped.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), failed.Count),
            completed && skipped.IsEmpty ? HintType.Success : HintType.Info);
        ShowSkippedFiles(skipped.DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase),
            failed.Distinct(StringComparer.OrdinalIgnoreCase));
        if (PageInstanceLeft.McInstance is not null && ModMain.frmInstanceMod?.IsVisible == true)
            ModMain.frmInstanceMod.ReloadCompFileList(true);
    }
}
