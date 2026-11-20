using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Mcp;

namespace PCL.Core.Test.Mcp;

[TestClass]
public sealed class McpCorsTests
{
    [TestMethod]
    [DataRow("http://localhost:5173")]
    [DataRow("https://pcl.localhost")]
    [DataRow("http://127.0.0.1:3000")]
    [DataRow("http://[::1]:3000")]
    public void BrowserOriginAllowsLoopbackHosts(string origin)
    {
        Assert.IsTrue(McpHost.IsAllowedBrowserOrigin(origin));
    }

    [TestMethod]
    public void BrowserOriginRejectsNonLoopbackHosts()
    {
        foreach (var origin in new[]
                 {
                     "https://example.com",
                     "https://localhost.example.com",
                     "http://10.0.0.2",
                     "null",
                     "file://"
                 })
            Assert.IsFalse(McpHost.IsAllowedBrowserOrigin(origin), origin);
    }
}
