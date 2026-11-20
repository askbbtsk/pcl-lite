using PCL.Core.IO.Storage.Cache.Model;
using PCL.Core.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.IO.Storage.Cache;

internal class CacheEvictionService(SqliteCacheStorage db, FileCacheStorage files, CacheOptions options)
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly object _startLock = new();

    public void Start()
    {
        lock (_startLock)
        {
            if (_loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => _EvictionLoopAsync(token));
        }
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? source;
        lock (_startLock)
        {
            source = _cts;
            loop = _loop;
            source?.Cancel();
            _cts = null;
            _loop = null;
        }
        try { if (loop is not null) await loop.ConfigureAwait(false); }
        finally { source?.Dispose(); }
    }

    public void CheckThreshold()
    {
        // NOTE: this method will not be implemented
        // because current eviction strategy is enough to keep the cache size under control without checking threshold after each write operation.
    }

    private async Task _EvictionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.EvictionInterval, ct).ConfigureAwait(false);
                using var lease = await files.EnterMutationAsync(ct).ConfigureAwait(false);
                await db.DeleteExpiredAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
                await files.PruneAsync(await db.GetAllFileHashesAsync(ct).ConfigureAwait(false)).ConfigureAwait(false);

                var stats = await db.GetStatsAsync(ct).ConfigureAwait(false);
                var excess = stats.TotalSizeBytes - (options.MaxCacheSize - options.ReserveBytes);
                if (excess > 0)
                {
                    await _EvictAsync(excess, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "CacheEviction", $"An error occurred while evicting cache entries:");
            }
        }
    }

    private async Task _EvictAsync(long targetBytes, CancellationToken ct)
    {
        var freed = 0L;
        const int batchSize = 50;

        while (freed < targetBytes && !ct.IsCancellationRequested)
        {
            var candidates = await db.GetEvictionCandidatesAsync(batchSize, ct).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return;
            }

            foreach (var can in candidates)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                await db.DeleteAsync(can.CacheKey!, ct).ConfigureAwait(false);

                if (can.EntryType is EntryType.FileRef && can.FileHash is not null)
                {
                    await files.ReleaseAsync(can.FileHash).ConfigureAwait(false);
                }

                freed += can.DataSize;
            }
        }
    }
}

public record CacheEntryEvictedEventArgs(string Key, long DataSize, int Priority, long HitCount);
