using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace PCL.Core.App.Configuration.Storage;

public class DynamicCacheConfigStorage : ConfigStorage
{
    private readonly Dictionary<object, ConfigStorage> _cache = [];
    private ConfigStorage? _nullContextCache;
    private readonly object _cacheLock = new();

    /// <summary>
    /// 存取仓库工厂。在没有匹配的上下文实例时将被调用，以创建新的上下文实例。
    /// </summary>
    public required Func<object?, ConfigStorage> StorageFactory { get; init; }

    protected override bool OnAccess<TKey, TValue>(StorageAction action, ref TKey key, [NotNullWhen(true)] ref TValue value, object? context)
    {
        lock (_cacheLock)
        {
            var cacheKey = NormalizeContext(context);
            ConfigStorage? storage;
            if (context is null) storage = _nullContextCache;
            else _cache.TryGetValue(cacheKey!, out storage);
            if (storage is null)
            {
                try
                {
                    storage = StorageFactory(context);
                    if (context is null) _nullContextCache = storage;
                    else _cache[cacheKey!] = storage;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to invoke storage factory", ex);
                }
            }
            return storage.Access(action, ref key, ref value, context);
        }
    }

    protected override void OnStop()
    {
        lock (_cacheLock)
        {
            foreach (var item in _cache.Values) item.Stop();
            _cache.Clear();
            _nullContextCache?.Stop();
            _nullContextCache = null;
        }
    }

    public bool InvalidateCache(object context)
    {
        lock (_cacheLock)
        {
            var cacheKey = NormalizeContext(context)!;
            var result = _cache.TryGetValue(cacheKey, out var center);
            if (result)
            {
                center?.Stop();
                _cache.Remove(cacheKey);
            }
            return result;
        }
    }

    private static object? NormalizeContext(object? context)
    {
        if (context is not string path) return context;
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }
}
