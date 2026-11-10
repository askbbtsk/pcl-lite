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
                    var result = await BuildAsync(instance, CancellationToken.None, new Progress<int>(), files).ConfigureAwait(false);
                    if (result.CacheSaved)
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

    internal static async Task<ModIndexBuildResult> BuildAsync(McInstance instance, CancellationToken token, IProgress<int> progress,
        IReadOnlyList<string>? selectedPaths = null)
    {
        if (selectedPaths is not null)
            return await Builds.RunBackgroundAsync(cancellation => BuildCoreAsync(instance, cancellation, progress, selectedPaths), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        ModLocalComp.CancelBackgroundIndex();
        return await Builds.RunForegroundAsync(cancellation => BuildCoreAsync(instance, cancellation, progress, null), token).ConfigureAwait(false);
    }

    private static async Task<ModIndexBuildResult> BuildCoreAsync(McInstance instance, CancellationToken token, IProgress<int> progress,
        IReadOnlyList<string>? selectedPaths)
    {
        Register(instance);
        var clock = Stopwatch.StartNew();
        var directory = ModsDirectory(instance);
        var files = selectedPaths?.ToArray() ?? (Directory.Exists(directory)
                ? ModBase.EnumerateFiles(directory)
                    .Where(file => ModLocalComp.LocalCompFile.IsModFile(file.FullName)
                        && (string.Equals(file.DirectoryName, directory, StringComparison.OrdinalIgnoreCase)
                            || instance.Info.HasForge && instance.Info.Drop < 130
                                && file.Directory?.Name == instance.Info.VanillaName))
                    .OrderByDescending(file =>
                    {
                        try { return file.Length; }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0L; }
                    }).Select(file => file.FullName).ToArray()
                : []);
        var preferredLoader = ModLocalComp.GetPreferredLoader(instance);
        var result = await ModIndexFileBatch.RunAsync(files, WorkerCount, (path, workerToken) =>
            {
                ModJarInJarCache.UseInstance(instance.PathInstance);
                // A malformed/large archive must not exhaust the scan budget for unrelated files.
                // Per-file depth, node, entry and decompression limits remain enforced.
                ModLocalComp.JijScanContext = new ModJarInJar.ScanContext(() => workerToken.IsCancellationRequested);
                ModLocalComp.DeferJijOnMiss = false;
                try
                {
                    string? reason = null;
                    RunWorker(() =>
                    {
                        var mod = new ModLocalComp.LocalCompFile(path) { PreferredLoader = preferredLoader };
                        mod.Load();
                        if (mod.State == ModLocalComp.LocalCompFile.LocalFileStatus.Unavailable)
                            reason = mod.FileUnavailableReason?.Message ?? Lang.Text("Setup.GameManage.ModIndex.FileUnavailable");
                        else if (mod.JijPending)
                            reason = Lang.Text("Setup.GameManage.ModIndex.FileIncomplete");
                    }, workerToken);
                    return reason;
                }
                finally
                {
                    ModLocalComp.JijScanContext = null;
                    ModLocalComp.DeferJijOnMiss = false;
                    ModJarInJarCache.UseInstance(null);
                }
            }, () =>
            {
                // Save good entries even after a bad file or cancellation; do not prune partial scans.
                ModJarInJarCache.UseInstance(instance.PathInstance);
                try { return ModJarInJarCache.SaveCurrent(); }
                finally { ModJarInJarCache.UseInstance(null); }
            }, progress, token).ConfigureAwait(false);
            ModBase.Log($"[ModIndex] {instance.PathInstance}: {files.Length} files, {WorkerCount} workers, "
                + $"{clock.ElapsedMilliseconds} ms, {result.IndexedFiles} indexed, {result.SkippedFiles.Count} skipped, saved={result.CacheSaved}");
            foreach (var failure in result.SkippedFiles)
                ModBase.Log($"[ModIndex] 跳过文件，保留其他索引：{failure.Path} — {failure.Reason}", ModBase.LogLevel.Developer);
            return result;
    }

    internal static void CreateNow(McInstance? instance)
    {
        if (instance is null)
        {
            HintService.Hint(Lang.Text("Setup.GameManage.ModIndex.NoInstance"), HintType.Info);
            return;
        }
        if (!instance.IsLoaded) instance.Load();
        ModIndexBuildResult? result = null;
        if (NativeWorkDialog.Run(Lang.Text("Setup.GameManage.ModIndex.Create"),
                Lang.Text("Setup.GameManage.ModIndex.Working", instance.Name),
                async (token, progress) =>
                {
                    result = await BuildAsync(instance, token, progress).ConfigureAwait(false);
                    return result.CacheSaved;
                }, () => ModMain.frmMain?.IsLoaded == true))
        {
            if (result!.IsComplete)
            {
                Policy.AllowPromptAgain(instance.PathInstance);
                HintService.Hint(Lang.Text("Setup.GameManage.ModIndex.Complete"), HintType.Success);
            }
            else
            {
                HintService.Hint(Lang.Text("Setup.GameManage.ModIndex.Partial", result.IndexedFiles, result.SkippedFiles.Count), HintType.Info);
                ShowSkippedFiles(result.SkippedFiles);
            }
        }
        else HintService.Hint(Lang.Text("Setup.GameManage.ModIndex.Incomplete"), HintType.Info);
        if (ReferenceEquals(instance, PageInstanceLeft.McInstance))
            ModMain.frmInstanceMod?.ReloadCompFileList(true);
    }

    private static void ShowSkippedFiles(IEnumerable<ModIndexFileFailure> files, IEnumerable<string>? failedDirectories = null)
    {
        var details = string.Join(Environment.NewLine, files.Select(file => file.Path + Environment.NewLine + "    " + file.Reason));
        var failures = string.Join(Environment.NewLine, failedDirectories ?? []);
        var sections = new List<string>();
        if (details.Length > 0) sections.Add(Lang.Text("Setup.GameManage.ModIndex.Skipped") + Environment.NewLine + details);
        if (failures.Length > 0) sections.Add(Lang.Text("Setup.GameManage.ModIndex.AllFailed") + Environment.NewLine + failures);
        if (sections.Count > 0)
            ModMain.MyMsgBox(string.Join(Environment.NewLine + Environment.NewLine, sections), Lang.Text("Setup.GameManage.ModIndex.Title"));
    }
}
