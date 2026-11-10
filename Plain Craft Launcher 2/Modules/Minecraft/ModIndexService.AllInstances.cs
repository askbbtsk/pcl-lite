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
                        bool built;
                        try
                        {
                            built = await BuildCoreAsync(instance, cancellation,
                                new NativeWorkDialog.StatusProgress<int>(Report), null).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            built = false;
                            ModBase.Log(error, $"建立实例索引失败：{instance.PathInstance}", ModBase.LogLevel.Developer);
                        }
                        foreach (var target in group)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var current = instances[target.InstancePath];
                            Register(current);
                            var saved = built && (ReferenceEquals(current, instance)
                                || ModJarInJarCache.CopySnapshot(instance.PathInstance, current.PathInstance));
                            if (saved) { Interlocked.Increment(ref successful); Policy.AllowPromptAgain(current.PathInstance); }
                            else failed.Enqueue(current.PathInstance);
                            done++;
                        }
                        Report(0);
                    }
                    return failed.Count == 0 && count > 0;
                }, token).ConfigureAwait(false);
            }, () => ModMain.frmMain?.IsLoaded == true);

        HintService.Hint(Lang.Text("Setup.GameManage.ModIndex.AllResult", successful, failed.Count),
            completed ? HintType.Success : HintType.Info);
        if (failed.Count > 0)
            ModMain.MyMsgBox(Lang.Text("Setup.GameManage.ModIndex.AllFailed") + Environment.NewLine
                + string.Join(Environment.NewLine, failed.Distinct(StringComparer.OrdinalIgnoreCase)),
                Lang.Text("Setup.GameManage.ModIndex.Title"));
        if (PageInstanceLeft.McInstance is not null && ModMain.frmInstanceMod?.IsVisible == true)
            ModMain.frmInstanceMod.ReloadCompFileList(true);
    }
}
