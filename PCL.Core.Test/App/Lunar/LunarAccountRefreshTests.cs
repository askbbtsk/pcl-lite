using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;
using PCL.Core.App.Lunar.Social;

namespace PCL.Core.Test.App.Lunar;

[TestClass]
public sealed class LunarAccountRefreshTests
{
    [TestMethod]
    public void RefreshPersistsTheNewTokenAndPreservesUnrelatedAccountsAndFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunar-token-tests-" + Guid.NewGuid().ToString("N"));
        var path = LunarAccountStore.FilePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string original = """
            {"selectedAccount":"target","accounts":{
              "target":{"refreshToken":"old-refresh","accessToken":"old-access","custom":42},
              "other":{"refreshToken":"other-refresh","accessToken":"other-access"}}}
            """;
        try
        {
            File.WriteAllText(path, original);
            Assert.IsTrue(LunarAccountStore.UpdateToken(root, "target", "new-access",
                DateTimeOffset.UtcNow.AddHours(1), "new-refresh"));
            var updated = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.AreEqual("new-refresh", updated["accounts"]!["target"]!["refreshToken"]!.GetValue<string>());
            Assert.AreEqual("new-access", updated["accounts"]!["target"]!["accessToken"]!.GetValue<string>());
            Assert.AreEqual(42, updated["accounts"]!["target"]!["custom"]!.GetValue<int>());
            Assert.AreEqual("other-refresh", updated["accounts"]!["other"]!["refreshToken"]!.GetValue<string>());
            Assert.AreEqual(original, File.ReadAllText(path + ".pclbak"));
            Assert.IsTrue(LunarAccountStore.UpdateToken(root, "target", "newest-access", null, ""));
            updated = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.AreEqual("new-refresh", updated["accounts"]!["target"]!["refreshToken"]!.GetValue<string>());
            Assert.IsFalse(LunarAccountStore.UpdateToken(root, "missing-account", "wrong", null, "wrong"));
            Assert.IsFalse(File.Exists(path + ".pcltmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DisposeWaitsForTheReceiveLoopBeforeDisposingItsLifetimeAndCanBeRepeated()
    {
        var client = new LunarSocialClient("dispose-test", "3.3.0");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var lifetime = (CancellationTokenSource)typeof(LunarSocialClient).GetField("_lifetime", flags)!.GetValue(client)!;
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.Token.Register(() => canceled.TrySetResult());
        var releaseReceiver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = Task.Run(async () =>
        {
            await releaseReceiver.Task;
            Assert.IsTrue(lifetime.Token.IsCancellationRequested,
                "Receive-loop cleanup must still be able to observe its token.");
        });
        typeof(LunarSocialClient).GetField("_receiveLoop", flags)!.SetValue(client, receiver);
        var first = client.DisposeAsync().AsTask();
        var second = client.DisposeAsync().AsTask();
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(first.IsCompleted, "Disposal must wait for the receive loop.");
            Assert.AreSame(first, second, "Concurrent disposal must join the same cleanup.");
        }
        finally { releaseReceiver.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
    }
}
