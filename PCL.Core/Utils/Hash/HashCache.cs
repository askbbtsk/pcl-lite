using Microsoft.Data.Sqlite;
using PCL.Core.Utils.Exts;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Utils.Hash;

public class HashCache : IDisposable
{
    private readonly string _dbPath;

    /// <summary>
    ///     单持久连接 + 全操作串行。
    ///     <para>
    ///         之前是「每次操作开新连接 + WAL」：这台机器上并发开连接建 <c>-shm</c>
    ///         会随机撞上 <c>SQLITE_READONLY</c>（50 并发 13/15 轮复现，且杀软实时
    ///         扫描会放大它）。哈希缓存本来就是低吞吐场景，单连接串行完全够用，
    ///         还顺带消灭了所有建文件竞态。
    ///     </para>
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public HashCache(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection(
            $"Data Source={_dbPath};Pooling=False;Cache=Private;Default Timeout=5");
        _connection.Open();
        _Initialize();
    }

    private void _Initialize()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS HashCache (
                FilePath TEXT NOT NULL PRIMARY KEY,
                FileSize INTEGER NOT NULL,
                LastWriteTime TEXT NOT NULL,
                MD5 TEXT NULL,
                SHA1 TEXT NULL,
                SHA256 TEXT NULL,
                SHA512 TEXT NULL,
                MurmurHash2 TEXT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        using var setCmd = _connection.CreateCommand();
        setCmd.CommandText = "PRAGMA journal_mode=WAL";
        setCmd.ExecuteNonQuery();
    }

    public Task<string> GetMD5Async(string filePath) =>
        _GetHashWithPending(filePath, MD5Provider.Instance, "MD5");

    public Task<string> GetSHA1Async(string filePath) =>
        _GetHashWithPending(filePath, SHA1Provider.Instance, "SHA1");

    public Task<string> GetSHA256Async(string filePath) =>
        _GetHashWithPending(filePath, SHA256Provider.Instance, "SHA256");

    public Task<string> GetSHA512Async(string filePath) =>
        _GetHashWithPending(filePath, SHA512Provider.Instance, "SHA512");

    public Task<string> GetMurmurHash2Async(string filePath) =>
        _GetHashWithPending(filePath, MurmurHash2Provider.Instance, "MurmurHash2");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _FileHashComputePending = new();

    public Task<string> GetHashAsync(string filePath, IHashProvider provider)
    {
        var algoName = provider switch
        {
            MD5Provider => "MD5",
            SHA1Provider => "SHA1",
            SHA256Provider => "SHA256",
            SHA512Provider => "SHA512",
            MurmurHash2Provider => "MurmurHash2",
            _ => throw new ArgumentException($"不支持的哈希算法: {provider.GetType().Name}")
        };
        return _GetHashWithPending(filePath, provider, algoName);
    }

    private Task<string> _GetHashWithPending(string filePath, IHashProvider provider, string algoName)
    {
        var computeKey = $"{_dbPath}|{filePath}:{algoName}";
        return _FileHashComputePending.GetOrAdd(computeKey, key =>
        {
            var computeTask = _GetHashAsync(filePath, provider, algoName);
            _ = computeTask.ContinueWith(t =>
            {
                _FileHashComputePending.TryRemove(key, out _);
            }, TaskContinuationOptions.ExecuteSynchronously);
            return computeTask;
        });
    }

    private async Task<string> _GetHashAsync(string filePath, IHashProvider provider, string algoName)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        var fullPath = Path.GetFullPath(filePath);

        try
        {
            var fileInfo = new FileInfo(fullPath);
            var fileSize = fileInfo.Length;
            var lastWrite = fileInfo.LastWriteTimeUtc.ToString("O");

            var cached = await _FindCacheEntryAsync(fullPath).ConfigureAwait(false);

            if (cached != null)
            {
                if (cached.FileSize == fileSize && cached.LastWriteTime == lastWrite)
                {
                    var hash = _GetHashFromEntry(cached, algoName);
                    if (hash != null)
                    {
                        ValidateFileFingerprint(fullPath, fileSize, lastWrite);
                        return hash;
                    }

                    var computedHash = await _ComputeHashAsync(fullPath, provider).ConfigureAwait(false);
                    ValidateFileFingerprint(fullPath, fileSize, lastWrite);
                    await _InsertOrUpdateHashAsync(fullPath, fileSize, lastWrite, algoName, computedHash).ConfigureAwait(false);
                    return computedHash;
                }
                else
                {
                    await _DeleteCacheEntryAsync(fullPath).ConfigureAwait(false);
                }
            }

            var computed = await _ComputeHashAsync(fullPath, provider).ConfigureAwait(false);
            ValidateFileFingerprint(fullPath, fileSize, lastWrite);
            await _InsertOrUpdateHashAsync(fullPath, fileSize, lastWrite, algoName, computed).ConfigureAwait(false);
            return computed;
        }
        catch (FileNotFoundException)
        {
            await _DeleteCacheEntryAsync(fullPath).ConfigureAwait(false);
            throw;
        }
        catch (IOException)
        {
            await _DeleteCacheEntryAsync(fullPath).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> _ComputeHashAsync(string fullPath, IHashProvider provider)
    {
        var before = new FileInfo(fullPath);
        var size = before.Length;
        var lastWrite = before.LastWriteTimeUtc.ToString("O");
        using FileStream fs = new(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Write | FileShare.Delete);
        var hash = (await provider.ComputeHashAsync(fs).ConfigureAwait(false)).ToHexString();
        ValidateFileFingerprint(fullPath, size, lastWrite);
        return hash;
    }

    private static void ValidateFileFingerprint(string path, long size, string lastWrite)
    {
        var current = new FileInfo(path);
        current.Refresh();
        if (!current.Exists) throw new FileNotFoundException("File was removed during hashing.", path);
        if (current.Length != size || current.LastWriteTimeUtc.ToString("O") != lastWrite)
            throw new IOException("File changed during hashing; its result was not cached: " + path);
    }

    private async Task<CacheEntry?> _FindCacheEntryAsync(string fullPath)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT FilePath, FileSize, LastWriteTime, MD5, SHA1, SHA256, SHA512, MurmurHash2 FROM HashCache WHERE FilePath = @FilePath";
            cmd.Parameters.AddWithValue("@FilePath", fullPath);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
                return null;

            return new CacheEntry
            {
                FilePath = reader.GetString(0),
                FileSize = reader.GetInt64(1),
                LastWriteTime = reader.GetString(2),
                MD5 = reader.IsDBNull(3) ? null : reader.GetString(3),
                SHA1 = reader.IsDBNull(4) ? null : reader.GetString(4),
                SHA256 = reader.IsDBNull(5) ? null : reader.GetString(5),
                SHA512 = reader.IsDBNull(6) ? null : reader.GetString(6),
                MurmurHash2 = reader.IsDBNull(7) ? null : reader.GetString(7)
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? _GetHashFromEntry(CacheEntry entry, string algoName) => algoName switch
    {
        "MD5" => entry.MD5,
        "SHA1" => entry.SHA1,
        "SHA256" => entry.SHA256,
        "SHA512" => entry.SHA512,
        "MurmurHash2" => entry.MurmurHash2,
        _ => null
    };

    private async Task _InsertOrUpdateHashAsync(string fullPath, long fileSize, string lastWrite, string algoName, string hash)
    {
        if (hash.IsNullOrWhiteSpace()) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO HashCache (FilePath, FileSize, LastWriteTime, MD5, SHA1, SHA256, SHA512, MurmurHash2)
                VALUES (@FilePath, @FileSize, @LastWriteTime, @MD5, @SHA1, @SHA256, @SHA512, @MurmurHash2)
                ON CONFLICT(FilePath) DO UPDATE SET
                    FileSize = excluded.FileSize,
                    LastWriteTime = excluded.LastWriteTime,
                    MD5 = COALESCE(excluded.MD5, HashCache.MD5),
                    SHA1 = COALESCE(excluded.SHA1, HashCache.SHA1),
                    SHA256 = COALESCE(excluded.SHA256, HashCache.SHA256),
                    SHA512 = COALESCE(excluded.SHA512, HashCache.SHA512),
                    MurmurHash2 = COALESCE(excluded.MurmurHash2, HashCache.MurmurHash2)
                """;
            cmd.Parameters.AddWithValue("@FilePath", fullPath);
            cmd.Parameters.AddWithValue("@FileSize", fileSize);
            cmd.Parameters.AddWithValue("@LastWriteTime", lastWrite);
            cmd.Parameters.AddWithValue("@MD5", algoName == "MD5" ? hash : DBNull.Value);
            cmd.Parameters.AddWithValue("@SHA1", algoName == "SHA1" ? hash : DBNull.Value);
            cmd.Parameters.AddWithValue("@SHA256", algoName == "SHA256" ? hash : DBNull.Value);
            cmd.Parameters.AddWithValue("@SHA512", algoName == "SHA512" ? hash : DBNull.Value);
            cmd.Parameters.AddWithValue("@MurmurHash2", algoName == "MurmurHash2" ? hash : DBNull.Value);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task _DeleteCacheEntryAsync(string fullPath)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM HashCache WHERE FilePath = @FilePath";
            cmd.Parameters.AddWithValue("@FilePath", fullPath);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
        _gate.Dispose();
    }

    private sealed class CacheEntry
    {
        public string FilePath { get; init; } = "";
        public long FileSize { get; init; }
        public string LastWriteTime { get; init; } = "";
        public string? MD5 { get; init; }
        public string? SHA1 { get; init; }
        public string? SHA256 { get; init; }
        public string? SHA512 { get; init; }
        public string? MurmurHash2 { get; init; }
    }
}
