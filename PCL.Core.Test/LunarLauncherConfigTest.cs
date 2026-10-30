using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     官方 Lunar 启动器设置文件（<c>settings/launcher.json</c>）的解析回归测试。
///     <para>
///         只喂字符串，不碰磁盘 —— 这个文件的内容完全由官方启动器决定，
///         版本之间改过字段类型，解析必须容错。
///     </para>
/// </summary>
[TestClass]
public class LunarLauncherConfigTest
{
    [TestMethod]
    public void Parse_ReadsMemoryAndJvmArgs()
    {
        const string json = """
                            {
                              "settings": {
                                "allocatedMemory": 8192,
                                "jvmArgs": "-XX:+UseG1GC -Xms2G",
                                "language": "zh_CN"
                              }
                            }
                            """;

        var result = LunarLauncherConfig.Parse(json);

        Assert.AreEqual(8192, result.AllocatedMemoryMb);
        Assert.AreEqual("-XX:+UseG1GC -Xms2G", result.JvmArgs);
        Assert.IsTrue(result.HasMemory);
        Assert.IsTrue(result.HasJvmArgs);
    }

    [TestMethod]
    public void Parse_ToleratesMissingFields()
    {
        var result = LunarLauncherConfig.Parse("""{"settings":{"language":"en_US"}}""");

        Assert.AreEqual(0, result.AllocatedMemoryMb);
        Assert.AreEqual(string.Empty, result.JvmArgs);
        Assert.IsFalse(result.HasMemory);
        Assert.IsFalse(result.HasJvmArgs);
    }

    [TestMethod]
    public void Parse_AcceptsStringMemory()
    {
        // 手改过的文件里见过字符串形式，不能因此整份丢掉
        var result = LunarLauncherConfig.Parse("""{"settings":{"allocatedMemory":"6144"}}""");

        Assert.AreEqual(6144, result.AllocatedMemoryMb);
    }

    [TestMethod]
    public void Parse_AcceptsArrayJvmArgs()
    {
        // 现在存的是一行字符串，但别假设它永远不改结构
        var result = LunarLauncherConfig.Parse("""{"settings":{"jvmArgs":["-XX:+UseG1GC","-Xmx4G"]}}""");

        Assert.AreEqual("-XX:+UseG1GC -Xmx4G", result.JvmArgs);
    }

    [TestMethod]
    public void Parse_ReturnsEmptyForGarbage()
    {
        foreach (var json in new[] { null, "", "   ", "not json", "[]", """{"settings":42}""" })
        {
            var result = LunarLauncherConfig.Parse(json);
            Assert.AreEqual(0, result.AllocatedMemoryMb, $"输入 {json ?? "<null>"} 不该解析出内存");
            Assert.AreEqual(string.Empty, result.JvmArgs);
        }
    }

    [TestMethod]
    public void Parse_NegativeMemoryTreatedAsUnset()
    {
        var result = LunarLauncherConfig.Parse("""{"settings":{"allocatedMemory":-1}}""");
        Assert.AreEqual(0, result.AllocatedMemoryMb);
    }
}
