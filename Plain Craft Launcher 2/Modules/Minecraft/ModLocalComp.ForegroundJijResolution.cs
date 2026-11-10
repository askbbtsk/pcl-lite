using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCL;

public static partial class ModLocalComp
{
    internal static int CurrentJijGeneration => Volatile.Read(ref _jijGeneration);

    internal static void CancelBackgroundIndex()
    {
        Interlocked.Increment(ref _jijGeneration);
        JijWorkers.CancelObsolete();
    }

    internal static async Task<bool> ResolveJijForCurrentOutputAsync(List<LocalCompFile> output, string instancePath,
        int expectedGeneration, CancellationToken token, IProgress<int> progress)
    {
        // Promotion owns a new publication generation too, so the cancelled background batch's
        // finally cannot overwrite this foreground result with an old incomplete status.
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(output, compResourceListLoader.output)) return false;
        var generation = expectedGeneration + 1;
        if (Interlocked.CompareExchange(ref _jijGeneration, generation, expectedGeneration) != expectedGeneration)
            return false;
        CompJijResolved = false;
        bool IsCurrent() => !token.IsCancellationRequested && generation == Volatile.Read(ref _jijGeneration)
            && ReferenceEquals(output, compResourceListLoader.output);
        if (!IsCurrent()) return false;
        var pending = output.Where(mod => !mod.IsFolder && mod.JijPending).ToArray();
        var sharedScanGate = new object();
        ModJarInJar.ScanContext? sharedScan = null;
        var completed = 0;
        await JijWorkers.RunAsync(pending, (mod, workerToken) =>
        {
            if (!IsCurrent()) return;
            ModJarInJarCache.UseInstance(instancePath);
            lock (sharedScanGate)
                JijScanContext = sharedScan ??= new ModJarInJar.ScanContext(() => workerToken.IsCancellationRequested || !IsCurrent());
            try
            {
                ModIndexService.RunWorker(() => mod.ResolveJijNow(), workerToken);
                progress.Report(Interlocked.Increment(ref completed));
            }
            finally { JijScanContext = null; ModJarInJarCache.UseInstance(null); }
        }, token, IsCurrent).ConfigureAwait(false);
        if (!IsCurrent()) return false;
        var allResolved = pending.All(mod => !mod.JijPending);
        ModJarInJarCache.UseInstance(instancePath);
        try
        {
            if (allResolved) ModJarInJarCache.Prune(output.Where(mod => !mod.IsFolder).Select(mod => mod.path));
            ModJarInJarCache.FlushCurrent();
        }
        finally { ModJarInJarCache.UseInstance(null); }
        if (IsCurrent()) CompJijResolved = allResolved;
        return allResolved && IsCurrent();
    }
}
