using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Utils;

/// <summary>Bounds actual work across batches and cancels obsolete queued work when a foreground batch replaces it.</summary>
public sealed class LatestBatchWorkScheduler
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _slots;
    private readonly int _parallelism;
    private CancellationTokenSource? _current;

    public LatestBatchWorkScheduler(int parallelism)
    {
        if (parallelism < 1 || parallelism > 64) throw new ArgumentOutOfRangeException(nameof(parallelism));
        _parallelism = parallelism;
        _slots = new SemaphoreSlim(parallelism, parallelism);
    }

    public void CancelObsolete()
    {
        lock (_gate) _current?.Cancel();
    }

    public async Task RunAsync<T>(IReadOnlyList<T> items, Action<T, CancellationToken> execute,
        CancellationToken cancellationToken = default, Func<bool>? canStart = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(execute);
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource batch;
        lock (_gate)
        {
            if (canStart?.Invoke() == false) return;
            _current?.Cancel();
            _current = batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        try
        {
            var index = -1;
            var workers = new Task[Math.Min(items.Count, _parallelism)];
            for (var worker = 0; worker < workers.Length; worker++)
                workers[worker] = Task.Run(async () =>
                {
                    while (true)
                    {
                        batch.Token.ThrowIfCancellationRequested();
                        var next = Interlocked.Increment(ref index);
                        if (next >= items.Count) return;
                        await _slots.WaitAsync(batch.Token).ConfigureAwait(false);
                        try { batch.Token.ThrowIfCancellationRequested(); execute(items[next], batch.Token); }
                        finally { _slots.Release(); }
                    }
                }, batch.Token);
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, batch)) _current = null;
                batch.Dispose();
            }
        }
    }
}
