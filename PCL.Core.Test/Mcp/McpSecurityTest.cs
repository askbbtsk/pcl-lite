using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Mcp;
using PCL.Core.Mcp.Analysis;
using PCL.Core.Mcp.Tools;

namespace PCL.Core.Test.Mcp;

/// <summary>
/// MCP 的安全边界与几个纯函数。
/// <para>
/// 这些用例的共同点：全部不依赖启动器宿主、不依赖 UI、不碰真实游戏目录，
/// 所以可以在 CI 里裸跑。它们守的是「模型能不能越权」这条线，
/// 改动 <c>McpPath</c> / <c>McpConfigGuard</c> / <c>McpLaunchGuards</c> 时务必重跑。
/// </para>
/// </summary>
[TestClass]
public class McpSecurityTest
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl-mcp-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        Directory.CreateDirectory(Path.Combine(_root, "saves", "world1"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响测试结论
        }
    }

    #region 路径沙箱

    [TestMethod]
    public void ResolveUnder_AcceptsChildPath()
    {
        var resolved = McpPath.ResolveUnder(_root, "mods");
        Assert.IsNotNull(resolved);
        Assert.AreEqual(Path.Combine(_root, "mods"), resolved);
    }

    [TestMethod]
    public void ResolveUnder_AcceptsRootItself()
    {
        Assert.IsNotNull(McpPath.ResolveUnder(_root, ""));
        Assert.IsNotNull(McpPath.ResolveUnder(_root, "."));
    }

    [TestMethod]
    public void ResolveUnder_RejectsParentTraversal()
    {
        Assert.IsNull(McpPath.ResolveUnder(_root, ".."));
        Assert.IsNull(McpPath.ResolveUnder(_root, "../../Windows"));
        Assert.IsNull(McpPath.ResolveUnder(_root, "mods/../../etc"));
    }

    [TestMethod]
    public void ResolveUnder_RejectsAbsolutePathOutsideRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pcl-mcp-outside");
        Assert.IsNull(McpPath.ResolveUnder(_root, outside));
        Assert.IsNull(McpPath.ResolveUnder(_root, @"C:\Windows\System32"));
    }

    [TestMethod]
    public void ResolveUnder_RejectsSiblingWithSamePrefix()
    {
        // C:\tmp\pcl-mcp-test-xxx-evil 不能因为字符串前缀相同就被放行
        var sibling = _root + "-evil";
        Assert.IsNull(McpPath.ResolveUnder(_root, sibling));
    }

    [TestMethod]
    public void FileNameOnly_StripsDirectories()
    {
        Assert.AreEqual("sodium.jar", McpPath.FileNameOnly(@"..\..\sodium.jar"));
        Assert.AreEqual("sodium.jar", McpPath.FileNameOnly("/etc/passwd/../sodium.jar"));
        Assert.AreEqual("", McpPath.FileNameOnly("   "));
    }

    #endregion

    #region 配置闸门

    [TestMethod]
    public void ConfigGuard_TokenIsSecret()
    {
        Assert.IsTrue(McpConfigGuard.IsSecret("McpToken"));
        Assert.IsTrue(McpConfigGuard.IsSecret("mcptoken"), "键名比较必须大小写不敏感");
        Assert.IsTrue(McpConfigGuard.IsSecret("MyApiToken"), "键名里带 token 就要当机密");
        Assert.IsTrue(McpConfigGuard.IsSecret("SomePassword"));
        Assert.IsFalse(McpConfigGuard.IsSecret("UiDarkMode"));
    }

    [TestMethod]
    public void ConfigGuard_PermissionSwitchesAreProtected()
    {
        foreach (var key in new[]
                 {
                     "McpEnabled", "McpPort", "McpToken", "McpAllowWrite",
                     "McpAllowGameControl", "McpAllowLauncherConfig",
                     "SystemDebugMode", "SystemDebugAllowRestrictedFeature"
                 })
            Assert.IsTrue(McpConfigGuard.IsProtected(key), $"{key} 必须不可写");
    }

    [TestMethod]
    public void ConfigGuard_NormalKeysAreWritable()
    {
        // 这些是用户真的可能让 AI 调的普通设置
        Assert.IsFalse(McpConfigGuard.IsProtected("UiDarkMode"));
        Assert.IsFalse(McpConfigGuard.IsProtected("LaunchRam"));
    }

    #endregion

    #region 启动参数白名单

    [TestMethod]
    public void LaunchGuards_AllowsOrdinaryJvmFlags()
    {
        Assert.IsTrue(McpLaunchGuards.TryValidate(["-Xmx2G", "-XX:+UseG1GC"], out var error));
        Assert.IsNull(error);
        Assert.IsTrue(McpLaunchGuards.TryValidate(null, out _));
        Assert.IsTrue(McpLaunchGuards.TryValidate([], out _));
    }

    [TestMethod]
    public void LaunchGuards_RejectsCodeInjection()
    {
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-javaagent:C:\\evil.jar"], out _));
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-agentlib:jdwp=transport=dt_socket"], out _));
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-Xbootclasspath/a:C:\\evil.jar"], out _));
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-XX:OnError=calc.exe"], out _));
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-Djava.system.class.loader=Evil"], out _));
    }

    [TestMethod]
    public void LaunchGuards_RejectsClasspathOverride()
    {
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-jar"], out _));
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-cp"], out _));
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-classpath"], out _));
        Assert.IsFalse(McpLaunchGuards.TryValidate(["--module-path"], out _));
    }

    [TestMethod]
    public void LaunchGuards_RejectsWhitespaceAndQuotes()
    {
        // 参数最终用空格拼接，带空格就会被拆成两个参数
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-Dfoo=hello world"], out _));
        Assert.IsFalse(McpLaunchGuards.TryValidate(["-Dfoo=\"bar\""], out _));
    }

    [TestMethod]
    public void LaunchGuards_RejectsOversizedInput()
    {
        var tooMany = Enumerable.Range(0, 33).Select(i => $"-Dk{i}=v").ToList();
        Assert.IsFalse(McpLaunchGuards.TryValidate(tooMany, out _));

        var tooLong = new[] { "-Dfoo=" + new string('a', 600) };
        Assert.IsFalse(McpLaunchGuards.TryValidate(tooLong, out _));
    }

    #endregion

    #region 分析器的两个历史 bug

    [TestMethod]
    public void RecommendedJavaMajor_Handles121AndLater()
    {
        // 老写法是 minor >= 20 && patch >= 5，1.21 / 1.21.4 会掉到 17
        Assert.AreEqual(21, InstanceAnalyzer.RecommendedJavaMajor("1.21"));
        Assert.AreEqual(21, InstanceAnalyzer.RecommendedJavaMajor("1.21.4"));
        Assert.AreEqual(21, InstanceAnalyzer.RecommendedJavaMajor("1.22"));
        Assert.AreEqual(21, InstanceAnalyzer.RecommendedJavaMajor("1.20.5"));
        Assert.AreEqual(17, InstanceAnalyzer.RecommendedJavaMajor("1.20.4"));
        Assert.AreEqual(17, InstanceAnalyzer.RecommendedJavaMajor("1.18.2"));
        Assert.AreEqual(16, InstanceAnalyzer.RecommendedJavaMajor("1.17.1"));
        Assert.AreEqual(8, InstanceAnalyzer.RecommendedJavaMajor("1.16.5"));
        Assert.AreEqual(8, InstanceAnalyzer.RecommendedJavaMajor("1.12.2"));
    }

    [TestMethod]
    public void ParseJavaMajorFromPath_HandlesLegacyOneDotNaming()
    {
        // jre1.8.0_51 里的 1 是主版本写法，不是 Java 1
        Assert.AreEqual(8, InstanceAnalyzer.ParseJavaMajorFromPath(@"C:\Java\jre1.8.0_51\bin\javaw.exe"));
        Assert.AreEqual(7, InstanceAnalyzer.ParseJavaMajorFromPath(@"C:\Java\jre1.7.0_80\bin\java.exe"));
        Assert.AreEqual(21, InstanceAnalyzer.ParseJavaMajorFromPath(@"C:\Java\jdk-21\bin\java.exe"));
        Assert.AreEqual(17, InstanceAnalyzer.ParseJavaMajorFromPath(@"C:\Java\jdk17.0.9\bin\java.exe"));
    }

    #endregion

    #region 审计摘要

    [TestMethod]
    public void Audit_SummarizeTruncatesLongValues()
    {
        var arguments = new JsonObject { ["path"] = new string('a', 300) };
        var summary = McpAudit.Summarize(arguments);

        Assert.IsTrue(summary.StartsWith("path="), summary);
        Assert.IsTrue(summary.EndsWith("…"), "超长值必须被截断");
        Assert.IsTrue(summary.Length <= 400);
    }

    [TestMethod]
    public void Audit_SummarizeKeepsAtMostEightArguments()
    {
        var arguments = new JsonObject();
        for (var i = 0; i < 20; i++) arguments[$"k{i}"] = i;

        var summary = McpAudit.Summarize(arguments);
        Assert.AreEqual(8, summary.Split(", ").Length);
    }

    [TestMethod]
    public void Audit_SummarizeSkipsNullValues()
    {
        var arguments = new JsonObject { ["a"] = "1", ["b"] = null };
        Assert.AreEqual("a=\"1\"", McpAudit.Summarize(arguments));
        Assert.AreEqual("{}", McpAudit.Summarize(null));
    }

    #endregion
}
