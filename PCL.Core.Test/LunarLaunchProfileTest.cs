using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     Lunar 启动设置解析的回归测试。
///     <para>
///         只测纯逻辑（参数切分、内存夹取、来源判定），不碰 <c>Config</c> ——
///         配置服务需要完整初始化，在单元测试里跑起来成本太高。
///     </para>
/// </summary>
[TestClass]
public class LunarLaunchProfileTest
{
    [TestMethod]
    public void SplitArguments_SplitsOnWhitespace()
    {
        var result = LunarLaunchProfile.SplitArguments("-XX:+UseG1GC   -Xmx8G\t-Dfoo=bar");
        CollectionAssert.AreEqual(new[] { "-XX:+UseG1GC", "-Xmx8G", "-Dfoo=bar" }, result.ToArray());
    }

    [TestMethod]
    public void SplitArguments_KeepsQuotedSpaces()
    {
        // 引号里的空格不能切，否则 -D 的值会被拆成两半
        var result = LunarLaunchProfile.SplitArguments("-Dfoo=\"a b\" -Xmx1G");
        CollectionAssert.AreEqual(new[] { "-Dfoo=a b", "-Xmx1G" }, result.ToArray());
    }

    [TestMethod]
    public void SplitArguments_HandlesEscapedQuote()
    {
        var result = LunarLaunchProfile.SplitArguments("-Dfoo=\"a \\\"b\\\" c\"");
        CollectionAssert.AreEqual(new[] { "-Dfoo=a \"b\" c" }, result.ToArray());
    }

    [TestMethod]
    public void SplitArguments_KeepsEmptyQuotedArgument()
    {
        var result = LunarLaunchProfile.SplitArguments("\"\" -Xmx1G");
        CollectionAssert.AreEqual(new[] { "", "-Xmx1G" }, result.ToArray());
    }

    [TestMethod]
    public void SplitArguments_EmptyInputYieldsNothing()
    {
        Assert.AreEqual(0, LunarLaunchProfile.SplitArguments(null).Count);
        Assert.AreEqual(0, LunarLaunchProfile.SplitArguments("").Count);
        Assert.AreEqual(0, LunarLaunchProfile.SplitArguments("   \r\n ").Count);
    }

    [TestMethod]
    public void ClampMemory_KeepsValueInRange()
    {
        Assert.AreEqual(4096, LunarLaunchProfile.ClampMemory(4096));
        // 太低抬到下限，避免 -Xmx256m 直接 OOM 起不来
        Assert.AreEqual(LunarLaunchProfile.MinMemoryMb, LunarLaunchProfile.ClampMemory(64));
        // 太高夹到上限，避免手滑输个天文数字
        Assert.AreEqual(LunarLaunchProfile.MaxMemoryMb, LunarLaunchProfile.ClampMemory(int.MaxValue));
        // 0 视为「没配」，用默认值
        Assert.AreEqual(LunarLaunchProfile.DefaultMemoryMb, LunarLaunchProfile.ClampMemory(0));
    }

    [TestMethod]
    public void NormalizeJavaPath_IgnoresMissingFile()
    {
        Assert.IsNull(LunarLaunchProfile.NormalizeJavaPath(null));
        Assert.IsNull(LunarLaunchProfile.NormalizeJavaPath(""));
        Assert.IsNull(LunarLaunchProfile.NormalizeJavaPath(@"Z:\definitely\not\here\java.exe"));
    }

    [TestMethod]
    public void DescribeSource_CoversEveryValue()
    {
        foreach (var source in System.Enum.GetValues<LunarSettingSource>())
            Assert.IsFalse(string.IsNullOrWhiteSpace(LunarLaunchProfile.DescribeSource(source)),
                $"{source} 没有对应的说明文字");
    }
}
