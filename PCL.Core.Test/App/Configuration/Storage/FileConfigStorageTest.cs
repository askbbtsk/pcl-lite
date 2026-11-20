using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Configuration.Storage;

namespace PCL.Core.Test.App.Configuration.Storage;

[TestClass]
public class FileConfigStorageTest
{
    [TestMethod]
    public void Stop_ShouldPersistQueuedWrite()
    {
        var provider = new TestFileProvider();
        var storage = new FileConfigStorage(provider);

        storage.SetValue("JvmArgs", "-Xmx2G");
        storage.Stop();

        Assert.AreEqual("-Xmx2G", provider.Get<string>("JvmArgs"));
    }

    [TestMethod]
    public async Task ASecondWriteIsFlushedWithoutWaitingForAThirdWriteOrShutdown()
    {
        var provider = new TestFileProvider();
        var storage = new FileConfigStorage(provider);
        try
        {
            storage.SetValue("first", 1);
            await provider.FirstSync.Task.WaitAsync(TimeSpan.FromSeconds(5));
            storage.SetValue("second", 2);
            await provider.SecondSync.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsTrue(storage.GetValue<int>("second", out var value));
            Assert.AreEqual(2, value);
        }
        finally { storage.Stop(); }
    }

    [TestMethod]
    public void EquivalentInstancePathsShareOneStorageAndInvalidateTogether()
    {
        var created = 0;
        var path = Path.Combine(Path.GetTempPath(), "Pcl-Config-" + Guid.NewGuid().ToString("N"));
        var storage = new DynamicCacheConfigStorage
        {
            StorageFactory = _ =>
            {
                created++;
                return new FileConfigStorage(new TestFileProvider());
            }
        };
        try
        {
            storage.SetValue("value", 1, path);
            storage.SetValue("value", 2, path + Path.DirectorySeparatorChar);
            Assert.AreEqual(1, created);
            Assert.IsTrue(storage.InvalidateCache(path + Path.DirectorySeparatorChar));
            storage.SetValue("value", 3, path);
            Assert.AreEqual(2, created);
        }
        finally { storage.Stop(); }
    }

    private sealed class TestFileProvider : IKeyValueFileProvider
    {
        private readonly Dictionary<string, object?> _values = [];

        public string FilePath => "test://config";

        public T Get<T>(string key) => (T)_values[key]!;

        public void Set<T>(string key, T value) => _values[key] = value;

        public bool Exists(string key) => _values.ContainsKey(key);

        public void Remove(string key) => _values.Remove(key);

        private int _syncCount;
        public TaskCompletionSource FirstSync { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSync { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Sync()
        {
            var count = Interlocked.Increment(ref _syncCount);
            if (count == 1) FirstSync.TrySetResult();
            if (count == 2) SecondSync.TrySetResult();
        }
    }
}
