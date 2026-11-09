using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>Cancellation changes loader state before workers finish; stage cleanup must await both.</summary>
internal static class InstanceMigrationLoaders
{
    internal static void Wait(ModLoader.LoaderBase loader, object? input, ModLoader.LoaderBase parent, CancellationToken token)
    {
        var workers = new HashSet<Task>();
        token.ThrowIfCancellationRequested();
        loader.Start(input);
        try
        {
            while (loader.State == ModBase.LoadState.Loading)
            {
                Capture(loader, workers);
                if (token.IsCancellationRequested) { loader.Abort(); break; }
                parent.Progress = loader.Progress;
                Thread.Sleep(10);
            }
            Capture(loader, workers);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (loader.State == ModBase.LoadState.Aborted) throw new OperationCanceledException(token);
            if (loader.State != ModBase.LoadState.Finished) throw loader.Error ?? new InvalidOperationException("Migration loader did not finish.");
        }
        finally
        {
            Capture(loader, workers);
            if (loader.State != ModBase.LoadState.Finished) loader.Abort();
            // No cancelled task gets to recreate a directory after its transaction removes the stage.
            foreach (var worker in workers)
                try { worker.GetAwaiter().GetResult(); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { ModBase.Log(error, "等待迁移子任务释放资源时遇到已完成的失败任务", ModBase.LogLevel.Developer); }
        }
    }

    private static void Capture(ModLoader.LoaderBase loader, HashSet<Task> tasks)
    {
        if (loader is ModLoader.LoaderTask task && task.lastRunningTask is { } running) tasks.Add(running);
        if (loader is LoaderDownload download && download.State != ModBase.LoadState.Waiting)
            tasks.Add(download.ExecutionCompletion);
        if (loader is ModLoader.LoaderCombo combo)
            foreach (var child in combo.loaders.ToArray()) Capture(child, tasks);
    }
}
