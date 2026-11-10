using System;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Resources;

/// <summary>Manual indexing preempts cooperative background scans; completed cache entries are reusable on retry.</summary>
public sealed class ModIndexWorkCoordinator
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1);
    private int _foregroundRequests;
    private CancellationTokenSource? _background;
    private TaskCompletionSource _foregroundIdle = CompletedSignal();

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    public async Task<T> RunForegroundAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_foregroundRequests++ == 0)
                _foregroundIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _background?.Cancel();
        }
        var entered = false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            return await work(token).ConfigureAwait(false);
        }
        finally
        {
            if (entered) _gate.Release();
            lock (_sync)
                if (--_foregroundRequests == 0) _foregroundIdle.TrySetResult();
        }
    }

    public async Task<T> RunBackgroundAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task idle;
            lock (_sync) idle = _foregroundIdle.Task;
            await idle.WaitAsync(token).ConfigureAwait(false);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            CancellationTokenSource? batch = null;
            try
            {
                lock (_sync)
                {
                    if (_foregroundRequests > 0) continue;
                    _background = batch = CancellationTokenSource.CreateLinkedTokenSource(token);
                }
                try
                {
                    batch.Token.ThrowIfCancellationRequested();
                    return await work(batch.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (batch.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    // A manual request took priority. Its completion wakes us to retry cached work.
                }
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_background, batch)) _background = null;
                    batch?.Dispose();
                }
                _gate.Release();
            }
        }
    }
}
