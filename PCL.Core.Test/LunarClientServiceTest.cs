using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     Lunar 物料下载通道的路由测试。
///     <para>
///         只验证「注册了下载通道就走通道、没注册就走内置兜底」的路由逻辑，
///         不发真实请求 —— 通道的实现（FileDownloader）在应用层，这里给个假通道。
///     </para>
/// </summary>
[TestClass]
public class LunarClientServiceTest
{
    [TestMethod]
    public async Task DownloadAsync_UsesDispatcherWhenRegistered()
    {
        IReadOnlyList<string>? receivedUrls = null;
        var receivedPath = "";
        LunarClientService.DownloadDispatcher = (urls, path, _) =>
        {
            receivedUrls = urls;
            receivedPath = path;
            File.WriteAllText(path, "ok");
            return Task.CompletedTask;
        };

        try
        {
            var target = Path.Combine(Path.GetTempPath(), "pcl-lunar-dltest-" + Guid.NewGuid().ToString("N"));
            await LunarClientService.DownloadAsync(["https://example.invalid/a"], target, CancellationToken.None);

            Assert.IsNotNull(receivedUrls);
            Assert.AreEqual(1, receivedUrls.Count);
            Assert.AreEqual("https://example.invalid/a", receivedUrls[0]);
            Assert.AreEqual(target, receivedPath);
            Assert.AreEqual("ok", File.ReadAllText(target));
        }
        finally
        {
            LunarClientService.DownloadDispatcher = null;
        }
    }

    [TestMethod]
    public async Task DownloadFirstAsync_PassesAllUrlsToDispatcher()
    {
        IReadOnlyList<string>? receivedUrls = null;
        LunarClientService.DownloadDispatcher = (urls, path, _) =>
        {
            receivedUrls = urls;
            File.WriteAllText(path, "ok");
            return Task.CompletedTask;
        };

        try
        {
            var target = Path.Combine(Path.GetTempPath(), "pcl-lunar-dltest-" + Guid.NewGuid().ToString("N"));
            // 模拟 jre.download 的 url + fallbackUrl：两条都必须原样进通道
            await LunarClientService.DownloadFirstAsync(
                ["https://example.invalid/primary", "https://example.invalid/fallback"],
                target, CancellationToken.None);

            Assert.IsNotNull(receivedUrls);
            CollectionAssert.AreEqual(
                new[] { "https://example.invalid/primary", "https://example.invalid/fallback" },
                receivedUrls.ToArray());
        }
        finally
        {
            LunarClientService.DownloadDispatcher = null;
        }
    }
}
