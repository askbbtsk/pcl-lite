using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Utils.OS;

namespace PCL.Core.Test;

/// <summary>
/// 用户文件夹名打码的测试。
/// <para>
/// 这里锁死的核心是「按路径结构打码，不是整串替换」：
/// 只有 <c>Users</c> 路径段后面那一段会被换掉，别的数字一个都不许动。
/// 以前的实现是 <c>raw.Replace(userProfile, ...)</c>，用户名是 <c>18857</c> 这种纯数字时
/// 会把日志里的版本号、文件大小一起改掉，而且只认当前用户和反斜杠。
/// </para>
/// </summary>
[TestClass]
public class UserPathMaskerTest
{
    [TestMethod]
    public void ShouldMaskUserFolderName()
    {
        Assert.AreEqual(@"C:\Users\*****\AppData\Roaming\.minecraft",
            UserPathMasker.MaskUserFolders(@"C:\Users\18857\AppData\Roaming\.minecraft"));
    }

    [TestMethod]
    public void ShouldAlwaysUseFiveAsterisks()
    {
        // 星号个数不能等于名字长度，否则等于把长度也告诉别人
        Assert.AreEqual(@"C:\Users\*****\a", UserPathMasker.MaskUserFolders(@"C:\Users\a\a"));
        Assert.AreEqual(@"C:\Users\*****\a", UserPathMasker.MaskUserFolders(@"C:\Users\averylongusername\a"));
    }

    [TestMethod]
    public void ShouldMaskEveryUserFolderInText()
    {
        var raw = @"java at C:\Users\18857\java\bin\java.exe, mc at C:\Users\Public\mc and D:\Users\someone\x";
        var masked = UserPathMasker.MaskUserFolders(raw);
        Assert.AreEqual(@"java at C:\Users\*****\java\bin\java.exe, mc at C:\Users\*****\mc and D:\Users\*****\x", masked);
    }

    [TestMethod]
    public void ShouldMaskForwardSlashPaths()
    {
        Assert.AreEqual("C:/Users/*****/x", UserPathMasker.MaskUserFolders("C:/Users/18857/x"));
    }

    [TestMethod]
    public void ShouldHandleLongPathPrefix()
    {
        Assert.AreEqual(@"\\?\C:\Users\*****\x", UserPathMasker.MaskUserFolders(@"\\?\C:\Users\18857\x"));
    }

    [TestMethod]
    public void ShouldMaskUserFolderAtEndOfText()
    {
        Assert.AreEqual(@"C:\Users\*****", UserPathMasker.MaskUserFolders(@"C:\Users\18857"));
    }

    [TestMethod]
    public void ShouldNotTouchLookalikeSegments()
    {
        // MyUsers 不是 Users 路径段；紧挨着的数字也不能被当名字
        const string raw = @"C:\MyUsers\18857\x and users of the app";
        Assert.AreEqual(raw, UserPathMasker.MaskUserFolders(raw));
    }

    [TestMethod]
    public void ShouldKeepOtherNumbersIntact()
    {
        // 用户名恰好是纯数字时，日志里别的数字不能被牵连
        const string raw = "18857 18857 C:\\Users\\18857\\x 1.7.0-pre-4 19380008";
        Assert.AreEqual("18857 18857 C:\\Users\\*****\\x 1.7.0-pre-4 19380008",
            UserPathMasker.MaskUserFolders(raw));
    }

    [TestMethod]
    public void ShouldBeIdempotent()
    {
        var once = UserPathMasker.MaskUserFolders(@"C:\Users\18857\x");
        Assert.AreEqual(once, UserPathMasker.MaskUserFolders(once));
    }

    [TestMethod]
    public void ShouldReturnInputWhenNothingToMask()
    {
        Assert.AreEqual(string.Empty, UserPathMasker.MaskUserFolders(null));
        Assert.AreEqual(string.Empty, UserPathMasker.MaskUserFolders(""));
        Assert.AreEqual("nothing here", UserPathMasker.MaskUserFolders("nothing here"));
    }

    [TestMethod]
    public void ApplyShouldFollowEnabledSwitch()
    {
        var raw = @"C:\Users\18857\x";
        try
        {
            UserPathMasker.Enabled = true;
            Assert.AreEqual(@"C:\Users\*****\x", UserPathMasker.Apply(raw));

            UserPathMasker.Enabled = false;
            Assert.AreEqual(raw, UserPathMasker.Apply(raw));
        }
        finally
        {
            UserPathMasker.Enabled = true; // 全局状态，别污染别的用例
        }
    }
}
