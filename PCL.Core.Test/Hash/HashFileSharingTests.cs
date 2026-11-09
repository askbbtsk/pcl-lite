using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.Hash;

namespace PCL.Core.Test.Hash;

[TestClass]
public sealed class HashFileSharingTests
{
    private sealed class PausedProvider : IHashProvider
    {
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Length => 64;
        public async ValueTask<byte[]> ComputeHashAsync(Stream input, CancellationToken token = default)
        { Ready.SetResult(); await Continue.Task; return await SHA256.HashDataAsync(input, token); }
        public byte[] ComputeHash(Stream input) => SHA256.HashData(input);
        public byte[] ComputeHash(ReadOnlySpan<byte> input) => SHA256.HashData(input);
        public byte[] ComputeHash(string input, Encoding? en = null) => SHA256.HashData((en ?? Encoding.UTF8).GetBytes(input));
    }

    [TestMethod]
    public async Task IndexHashHandleAllowsDeleteAndDoesNotReturnTheDeletedFilesDigest()
    {
        var root = Path.Combine(Path.GetTempPath(), "PCLHashSharing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mod.jar"); File.WriteAllText(path, "old bytes");
        var provider = new PausedProvider();
        var compute = typeof(HashCache).GetMethod("_ComputeHashAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (Task<string>)compute.Invoke(null, [path, provider])!;
        try
        {
            await provider.Ready.Task;
            File.Delete(path); // Must succeed while the index hash still owns its file handle.
            provider.Continue.SetResult();
            try { await result; Assert.Fail("Removed-file hash must not be returned or cached."); }
            catch (FileNotFoundException) { }
        }
        finally
        {
            provider.Continue.TrySetResult();
            try { await result; } catch (IOException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task IndexHashHandleAllowsEditButRejectsChangedFingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "PCLHashSharing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mod.jar"); File.WriteAllText(path, "old bytes");
        var provider = new PausedProvider();
        var compute = typeof(HashCache).GetMethod("_ComputeHashAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (Task<string>)compute.Invoke(null, [path, provider])!;
        try
        {
            await provider.Ready.Task;
            File.WriteAllText(path, "different user bytes");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            provider.Continue.SetResult();
            try { await result; Assert.Fail("Changed-file hash must not be returned or cached."); }
            catch (IOException) { }
            Assert.AreEqual("different user bytes", File.ReadAllText(path));
        }
        finally
        {
            provider.Continue.TrySetResult();
            try { await result; } catch (IOException) { }
            Directory.Delete(root, recursive: true);
        }
    }
}
