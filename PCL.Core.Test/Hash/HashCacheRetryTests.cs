using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.Hash;

namespace PCL.Core.Test.Hash;

[TestClass]
public sealed class HashCacheRetryTests
{
    [TestMethod]
    public async Task FileRestoredAfterImmediateMissingFailureCanBeIndexedWithoutRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PCLHashRetry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var cache = new HashCache(Path.Combine(directory, "hash.db"));
            var path = Path.Combine(directory, "restored.jar");
            try { await cache.GetSHA1Async(path); Assert.Fail("Missing file should fail."); }
            catch (FileNotFoundException) { }
            File.WriteAllText(path, "restored bytes");
            var expected = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path)));
            Assert.AreEqual(expected, (await cache.GetSHA1Async(path)).ToUpperInvariant());
            Assert.AreEqual(expected, (await cache.GetSHA1Async(path)).ToUpperInvariant());
        }
        finally { Directory.Delete(directory, true); }
    }
}
