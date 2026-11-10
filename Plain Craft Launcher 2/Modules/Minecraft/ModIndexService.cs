using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Resources;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;

namespace PCL;

/// <summary>Explicit index creation and download-triggered refresh; no idle scans.</summary>
internal static partial class ModIndexService
{
    internal static readonly ModIndexingPolicy Policy = new();
    internal static readonly int WorkerCount = ModIndexingPolicy.GetWorkerCount(
        Environment.ProcessorCount, AvailableWorkerMemory());
    private static readonly SemaphoreSlim WorkerSlots = new(WorkerCount);
    private static readonly ModIndexWorkCoordinator Builds = new();
    private static readonly ConcurrentDictionary<string, McInstance> KnownDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> DownloadWorkers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> DownloadFiles = new(StringComparer.OrdinalIgnoreCase);

    internal static string ModsDirectory(McInstance instance)
    {
        // PathIndie uses the selected root when isolation is off. Resolve that case against
        // this instance's actual root without switching the user's selected Minecraft folder.
        var gameDirectory = instance.PathIndie;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.PathInstance)), StringComparison.OrdinalIgnoreCase))
            gameDirectory = Directory.GetParent(Path.TrimEndingDirectorySeparator(instance.PathInstance))?.Parent?.FullName
                ?? gameDirectory;
        return Path.GetFullPath(Path.Combine(gameDirectory, instance.Info.HasLabyMod
            ? Path.Combine("labymod-neo", "fabric", instance.Info.VanillaName, "mods") : "mods"));
    }

    internal static void Register(McInstance instance) => KnownDirectories[ModsDirectory(instance)] = instance;

    private static long AvailableWorkerMemory()
    {
        try { return Math.Max(1, checked((long)KernelInterop.GetAvailablePhysicalMemoryBytes()) / 2); }
        catch { return 0; } // Unknown memory uses the conservative two-worker policy.
    }

    internal static void RunWorker(Action work, CancellationToken token = default)
    {
        WorkerSlots.Wait(token);
        try { token.ThrowIfCancellationRequested(); work(); }
        finally { WorkerSlots.Release(); }
    }

    internal static void DownloadCompleted(string path)
    {
        if (!ModLocalComp.LocalCompFile.IsModFile(path)) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null) return;
        // Legacy Forge permits mods/<MC version>/. Resolve only one such level under a mods root.
        if (!Path.GetFileName(directory).Equals("mods", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || !Path.GetFileName(parent).Equals("mods", StringComparison.OrdinalIgnoreCase)) return;
            if (KnownDirectories.TryGetValue(parent, out var legacy)
                && !(legacy.Info.HasForge && legacy.Info.Drop < 130
                    && Path.GetFileName(directory) == legacy.Info.VanillaName)) return;
            directory = parent;
        }
        // The instance might not have been opened yet. Its next list load will consume the request.
        if (!KnownDirectories.TryGetValue(directory, out var instance))
        {
            Policy.DownloadCompleted(directory);
            return;
        }
        // Publish files before the wake-up signal, so a racing worker cannot consume an empty batch.
        DownloadFiles.GetOrAdd(directory, _ => new(StringComparer.OrdinalIgnoreCase))[path] = 0;
        Policy.DownloadCompleted(directory);
        StartDownloadWorker(directory, instance);
    }

    private static void StartDownloadWorker(string directory, McInstance instance)
    {
        if (!DownloadWorkers.TryAdd(directory, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (Policy.ConsumeDownloadRequest(directory))
                {
                    // Coalesce a burst of completed downloads, then decode only changed files.
                    await Task.Delay(200).ConfigureAwait(false);
                    var queued = DownloadFiles[directory];
                    var files = queued.Keys.Where(path => queued.TryRemove(path, out _)).ToArray();
                    var generation = ModLocalComp.CurrentJijGeneration;
                    if (await BuildAsync(instance, CancellationToken.None, new Progress<int>(), files).ConfigureAwait(false))
                        ModBase.RunInUi(() =>
                        {
                            if (generation == ModLocalComp.CurrentJijGeneration
                                && ReferenceEquals(instance, PageInstanceLeft.McInstance)
                                && ModMain.frmInstanceMod?.IsVisible == true
                                && ModLocalComp.compResourceListLoader.State == ModBase.LoadState.Finished)
                                ModMain.frmInstanceMod.ReloadCompFileList(true);
                        });
                }
            }
            catch (Exception error) { ModBase.Log(error, "下载后建立模组索引失败", ModBase.LogLevel.Developer); }
            finally
            {
                DownloadWorkers.TryRemove(directory, out _);
                // A download can finish between the loop's last check and removing the worker.
                if (Policy.ConsumeDownloadRequest(directory))
                {
                    Policy.DownloadCompleted(directory);
                    StartDownloadWorker(directory, instance);
                }
            }
        });
    }

    internal static async Task<bool> BuildAsync(McInstance instance, CancellationToken token, IProgress<int> progress,
        IReadOnlyList<string>? selectedPaths = null)
    {
        if (selectedPaths is not null)
            return await Builds.RunBackgroundAsync(cancellation => BuildCoreAsync(instance, cancellation, progress, selectedPaths), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        ModLocalComp.CancelBackgroundIndex();
        return await Builds.RunForegroundAsync(cancellation => BuildCoreAsync(instance, cancellation, progress, null), token).ConfigureAwait(false);
    }

    private static async Task<bool> BuildCoreAsync(McInstance instance, CancellationToken token, IProgress<int> progress,
        IReadOnlyList<string>? selectedPaths)
    {
        Register(instance);
        var clock = Stopwatch.StartNew();
        var complete = false;
        try
        {
            var directory = ModsDirectory(instance);
            var files = selectedPaths?.ToArray() ?? (Directory.Exists(directory)
                ? ModBase.EnumerateFiles(directory)
                    .Where(file => ModLocalComp.LocalCompFile.IsModFile(file.FullName)
                        && (string.Equals(file.DirectoryName, directory, StringComparison.OrdinalIgnoreCase)
                            || instance.Info.HasForge && instance.Info.Drop < 130
                                && file.Directory?.Name == instance.Info.VanillaName))
                    .OrderByDescending(file => file.Length).Select(file => file.FullName).ToArray()
                : []);
            var completed = 0;
            var failed = 0;
            var scan = new ModJarInJar.ScanContext(() => token.IsCancellationRequested);
            var preferredLoader = ModLocalComp.GetPreferredLoader(instance);
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = WorkerCount, CancellationToken = token
            }, (path, _) =>
            {
                ModJarInJarCache.UseInstance(instance.PathInstance);
                ModLocalComp.JijScanContext = scan;
                ModLocalComp.DeferJijOnMiss = false;
                try
                {
                    RunWorker(() =>
                    {
                        var mod = new ModLocalComp.LocalCompFile(path) { PreferredLoader = preferredLoader };
                        mod.Load();
                        if (mod.JijPending || mod.State == ModLocalComp.LocalCompFile.LocalFileStatus.Unavailable)
                            Interlocked.Increment(ref failed);
                    }, token);
                    progress.Report(Interlocked.Increment(ref completed));
                }
                finally
                {
                    ModLocalComp.JijScanContext = null;
                    ModLocalComp.DeferJijOnMiss = false;
                    ModJarInJarCache.UseInstance(null);
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ModBase.Log($"[ModIndex] {files.Length} files, {WorkerCount} workers, {clock.ElapsedMilliseconds} ms, {failed} incomplete");
            complete = failed == 0;
        }
        finally
        {
            // Cancellation also saves completed entries, without pruning files from a partial snapshot.
            ModJarInJarCache.UseInstance(instance.PathInstance);
            try { complete &= ModJarInJarCache.SaveCurrent(); }
            finally { ModJarInJarCache.UseInstance(null); }
        }
        return complete;
    }

    internal static void CreateNow(McInstance? instance)
    {
        if (instance is null)
        {
            HintService.Hint(Lang.Text("Setup.GameManage.ModIndex.NoInstance"), HintType.Info);
            return;
        }
        if (!instance.IsLoaded) instance.Load();
        if (NativeWorkDialog.Run(Lang.Text("Setup.GameManage.ModIndex.Create"),
                Lang.Text("Setup.GameManage.ModIndex.Working", instance.Name),
                (token, progress) => BuildAsync(instance, token, progress), () => ModMain.frmMain?.IsLoaded == true))
        {
            Policy.AllowPromptAgain(instance.PathInstance);
            HintService.Hint(Lang.Text("Setup.GameManage.ModIndex.Complete"), HintType.Success);
        }
        else HintService.Hint(Lang.Text("Setup.GameManage.ModIndex.Incomplete"), HintType.Info);
        if (ReferenceEquals(instance, PageInstanceLeft.McInstance))
            ModMain.frmInstanceMod?.ReloadCompFileList(true);
    }
}
