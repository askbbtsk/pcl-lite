using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

/// <summary>
///     Lunar 索引文件解析、日志配置生成、账户库初始化的回归测试。
///     <para>
///         全部是纯本地逻辑，不发网络请求，也不碰用户真实的 <c>~/.lunarclient</c>：
///         每个用例自己造一个临时目录，跑完删掉。
///     </para>
/// </summary>
[TestClass]
public class LunarInstallerTest
{
    #region 索引解析

    [TestMethod]
    public void Parse_ReadsPathAndHash()
    {
        var entries = LunarIndexParser.Parse(
            "assets/lunar/cosmetics/index d8467e2e551369c9947e39ed521b4197526df041");

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("assets/lunar/cosmetics/index", entries[0].RelativePath);
        Assert.AreEqual("d8467e2e551369c9947e39ed521b4197526df041", entries[0].Hash);
    }

    [TestMethod]
    public void Parse_IgnoresTrailingColumns()
    {
        // 真实的 texturesIndex.txt 有四列：路径 哈希 字节数 mtime
        var entries = LunarIndexParser.Parse(
            "assets/lunar/cosmetics/index d8467e2e551369c9947e39ed521b4197526df041 624931 1767579521");

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("d8467e2e551369c9947e39ed521b4197526df041", entries[0].Hash);
    }

    [TestMethod]
    public void Parse_HandlesCrlfAndBlankLines()
    {
        var entries = LunarIndexParser.Parse(
            "a.txt 0000000000000000000000000000000000000000\r\n\r\nb.txt 1111111111111111111111111111111111111111\r\n");

        CollectionAssert.AreEqual(
            new[] { "a.txt", "b.txt" },
            entries.Select(x => x.RelativePath).ToArray());
    }

    [TestMethod]
    public void Parse_SkipsMalformedLines()
    {
        var entries = LunarIndexParser.Parse(string.Join('\n',
            "没有哈希的行",
            "短哈希 abcdef",
            "非十六进制 zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz",
            "good.txt 2222222222222222222222222222222222222222"));

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("good.txt", entries[0].RelativePath);
    }

    [TestMethod]
    public void Parse_RejectsUnsafePaths()
    {
        // 官方启动器把这类条目判成 INDEX_UNSAFE_PATH 直接抛异常；
        // 这里退一步只跳过这一行，别让整份索引跟着废掉
        var entries = LunarIndexParser.Parse(string.Join('\n',
            "/etc/passwd 3333333333333333333333333333333333333333",
            "C:/windows/system32 4444444444444444444444444444444444444444",
            "../../escape.txt 5555555555555555555555555555555555555555",
            "ok/../../escape.txt 6666666666666666666666666666666666666666",
            "ok.txt 7777777777777777777777777777777777777777"));

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("ok.txt", entries[0].RelativePath);
    }

    [TestMethod]
    public void IsSafeRelativePath_AllowsNestedPaths()
    {
        Assert.IsTrue(LunarIndexParser.IsSafeRelativePath("assets/lunar/cosmetics/indexes/hats.json"));
        Assert.IsFalse(LunarIndexParser.IsSafeRelativePath("../x"));
        Assert.IsFalse(LunarIndexParser.IsSafeRelativePath("a/../b"));
        Assert.IsFalse(LunarIndexParser.IsSafeRelativePath(""));
        Assert.IsFalse(LunarIndexParser.IsSafeRelativePath(@"\\server\share"));
    }

    [TestMethod]
    public void Parse_EmptyInputReturnsEmpty()
    {
        Assert.AreEqual(0, LunarIndexParser.Parse(null).Count);
        Assert.AreEqual(0, LunarIndexParser.Parse("").Count);
        Assert.AreEqual(0, LunarIndexParser.Parse("\n\n\n").Count);
    }

    #endregion

    #region 日志配置

    [TestMethod]
    public void EnsureLog4jConfig_WritesResolvedPaths()
    {
        var root = CreateTempRoot();
        try
        {
            var path = LunarInstaller.EnsureLog4jConfig(root);
            Assert.IsTrue(File.Exists(path));

            var text = File.ReadAllText(path);
            Assert.IsFalse(text.Contains("absolute-path-here"), "占位符必须全部替换掉");
            Assert.IsTrue(text.Contains("latest.log"));
            Assert.IsTrue(text.Contains("%d{yyyy-MM-dd}-%i.log.gz"));
            Assert.IsTrue(text.Contains("<RollingRandomAccessFile"), "模板结构不能丢");
            Assert.AreEqual(Path.Combine(root, "logs", "game", "config.xml"), path);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    #endregion

    #region 账户库

    [TestMethod]
    public void CreateIfMissing_WritesAccountFile()
    {
        var root = CreateTempRoot();
        try
        {
            const string localId = "0123456789abcdef0123456789abcdef";
            Assert.IsTrue(LunarAccountStore.CreateIfMissing(root, localId, "bsk10ww", "lyricify",
                "694cd52b819745f0b28dad73eb299699", "token-value",
                DateTimeOffset.Parse("2026-10-28T14:00:06.902Z")));

            var path = LunarAccountStore.FilePath(root);
            Assert.IsTrue(File.Exists(path));

            var json = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.AreEqual(localId, json["activeAccountLocalId"]!.GetValue<string>());

            var account = json["accounts"]![localId]!;
            Assert.AreEqual("token-value", account["accessToken"]!.GetValue<string>());
            Assert.AreEqual("lyricify", account["minecraftProfile"]!["name"]!.GetValue<string>());
            Assert.AreEqual("694cd52b819745f0b28dad73eb299699",
                account["minecraftProfile"]!["id"]!.GetValue<string>());
            Assert.AreEqual("Xbox", account["type"]!.GetValue<string>());

            // 写完能被自己的读取逻辑认出来
            var snapshot = LunarAccountStore.Read(root);
            Assert.AreEqual(1, snapshot.Accounts.Count);
            Assert.AreEqual(localId, snapshot.ActiveLocalId);
            Assert.AreEqual("lyricify", snapshot.Accounts[0].DisplayName);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public void CreateIfMissing_DoesNotTouchExistingAccounts()
    {
        var root = CreateTempRoot();
        try
        {
            Assert.IsTrue(LunarAccountStore.CreateIfMissing(root, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "first", "First", "11111111111111111111111111111111", "t1", null));

            var before = File.ReadAllText(LunarAccountStore.FilePath(root));

            // 第二次调用不能改任何东西，哪怕换了个账号
            Assert.IsTrue(LunarAccountStore.CreateIfMissing(root, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "second", "Second", "22222222222222222222222222222222", "t2", null));

            Assert.AreEqual(before, File.ReadAllText(LunarAccountStore.FilePath(root)));

            var snapshot = LunarAccountStore.Read(root);
            Assert.AreEqual(1, snapshot.Accounts.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", snapshot.ActiveLocalId);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [TestMethod]
    public void CreateIfMissing_RejectsEmptyUuid()
    {
        var root = CreateTempRoot();
        try
        {
            Assert.IsFalse(LunarAccountStore.CreateIfMissing(root, "cccccccccccccccccccccccccccccccc",
                "x", "x", "", "t", null));
            Assert.IsFalse(File.Exists(LunarAccountStore.FilePath(root)));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    #endregion

    private static string CreateTempRoot() => Path.Combine(Path.GetTempPath(),
        "pcl-lunar-test-" + Guid.NewGuid().ToString("N")[..12]);

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }
}
