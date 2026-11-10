using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Resources;

public sealed record ModIndexFileFailure(string Path, string Reason);

/// <summary>A saved partial index remains usable; unsupported files never invalidate their neighbours.</summary>
public sealed record ModIndexBuildResult(bool CacheSaved, int IndexedFiles,
    IReadOnlyList<ModIndexFileFailure> SkippedFiles, string? SaveError = null)
{
    public bool IsComplete => CacheSaved && SkippedFiles.Count == 0;
}

public static class ModIndexFileBatch
{
    /// <param name="indexFile">Returns null on success, otherwise the reason this file was skipped.</param>
    /// <param name="save">Persists completed entries, including when a batch is cancelled.</param>
    public static async Task<ModIndexBuildResult> RunAsync(IReadOnlyList<string> paths, int parallelism,
        Func<string, CancellationToken, string?> indexFile, Func<bool> save,
        IProgress<int> progress, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(indexFile);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(progress);
        if (parallelism is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(parallelism));
        token.ThrowIfCancellationRequested();
        var indexed = 0;
        var processed = 0;
        var skipped = new ConcurrentQueue<ModIndexFileFailure>();
        var saved = false;
        string? saveError = null;
        try
        {
            await Parallel.ForEachAsync(paths, new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism, CancellationToken = token
            }, (path, workerToken) =>
            {
                workerToken.ThrowIfCancellationRequested();
                string? reason;
                try { reason = indexFile(path, workerToken); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    reason = string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
                }
                workerToken.ThrowIfCancellationRequested();
                if (reason is null) Interlocked.Increment(ref indexed);
                else skipped.Enqueue(new(path, reason));
                progress.Report(Interlocked.Increment(ref processed));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            try { saved = save(); }
            catch (Exception error) when (error is not OutOfMemoryException) { saveError = error.Message; }
        }
        return new(saved, indexed, skipped.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(), saveError);
    }
}
