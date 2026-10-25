using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Updates;
using System;

namespace PCL.Core.Test;

/// <summary>
/// 更新清单指针的解析测试。
/// <para>
/// 重点是那几个「实战翻车」的用例：反代返回的 <c>404: Not Found</c>、
/// 被墙时回的 HTML 错误页、以及内容为空但 HTTP 200 的情况——
/// 它们都必须是「这条通道失败」，而不是「清单写错了」。
/// </para>
/// </summary>
[TestClass]
public class UpdateManifestPointerTest
{
    [TestMethod]
    public void ExtractShouldReadSingleLineJson()
    {
        Assert.AreEqual(
            "askbbtsk/pcl-lite/releases/latest",
            UpdateManifestPointer.Extract("{\"update\": \"askbbtsk/pcl-lite/releases/latest\"}"));
    }

    [TestMethod]
    public void ExtractShouldReadBareJsonString()
    {
        Assert.AreEqual(
            "askbbtsk/pcl-lite/releases/latest",
            UpdateManifestPointer.Extract("\"askbbtsk/pcl-lite/releases/latest\""));
    }

    [TestMethod]
    public void ExtractShouldReadPlainText()
    {
        Assert.AreEqual(
            "askbbtsk/pcl-lite/releases/latest",
            UpdateManifestPointer.Extract("askbbtsk/pcl-lite/releases/latest"));
    }

    [TestMethod]
    public void ExtractShouldReadShortForm()
    {
        Assert.AreEqual("askbbtsk/pcl-lite", UpdateManifestPointer.Extract("askbbtsk/pcl-lite"));
    }

    [TestMethod]
    public void ExtractShouldTolerateBomAndNewline()
    {
        Assert.AreEqual(
            "askbbtsk/pcl-lite/releases/latest",
            UpdateManifestPointer.Extract("\uFEFF{\"update\":\"askbbtsk/pcl-lite/releases/latest\"}\n"));
    }

    [TestMethod]
    public void ExtractShouldFallBackToSoleStringValue()
    {
        Assert.AreEqual(
            "askbbtsk/pcl-lite",
            UpdateManifestPointer.Extract("{\"whatever\": \"askbbtsk/pcl-lite\"}"));
    }

    /// <summary>实战 bug：反代 404 的响应体被当成了指针。</summary>
    [TestMethod]
    public void ExtractShouldRejectNotFoundBody()
    {
        var ex = Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract("404: Not Found"));
        Assert.IsTrue(ex.Message.Contains("不是仓库指针"), ex.Message);
    }

    [TestMethod]
    public void ExtractShouldRejectHtmlErrorPage()
    {
        Assert.ThrowsExactly<FormatException>(
            () => UpdateManifestPointer.Extract("<!DOCTYPE html><html><body>502 Bad Gateway</body></html>"));
    }

    [TestMethod]
    public void ExtractShouldRejectEmptyBody()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract("   \n\t "));
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract(null));
    }

    [TestMethod]
    public void ExtractShouldRejectJsonWithoutPointer()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract("{\"update\": \"\"}"));
        Assert.ThrowsExactly<FormatException>(
            () => UpdateManifestPointer.Extract("{\"a\": \"askbbtsk/pcl-lite\", \"b\": \"askbbtsk/pcl-lite-service\"}"));
    }

    [TestMethod]
    public void ExtractShouldRejectBrokenJson()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Extract("{\"update\": "));
    }

    [TestMethod]
    public void LooksLikePointerShouldAcceptOwnerRepoForms()
    {
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("askbbtsk/pcl-lite"));
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("askbbtsk/pcl-lite/releases/latest"));
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("/askbbtsk/pcl-lite/"));
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("https://github.com/askbbtsk/pcl-lite"));
        Assert.IsTrue(UpdateManifestPointer.LooksLikePointer("github.com/askbbtsk/pcl-lite"));
    }

    [TestMethod]
    public void LooksLikePointerShouldRejectGarbage()
    {
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer(null));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer(""));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("404: Not Found"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("Not Found"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("Pcl-Lite"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("Pcl-Lite / Pcl-lite"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("<html>"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer("{\"update\":\"askbbtsk/pcl-lite\"}"));
        Assert.IsFalse(UpdateManifestPointer.LooksLikePointer(new string('a', 300) + "/b"));
    }

    [TestMethod]
    public void ParseShouldSplitOwnerAndRepo()
    {
        var (owner, repo) = UpdateManifestPointer.Parse("askbbtsk/pcl-lite/releases/latest");
        Assert.AreEqual("askbbtsk", owner);
        Assert.AreEqual("pcl-lite", repo);
    }

    [TestMethod]
    public void ParseShouldHandleFullUrl()
    {
        var (owner, repo) = UpdateManifestPointer.Parse("https://github.com/askbbtsk/pcl-lite/releases/latest");
        Assert.AreEqual("askbbtsk", owner);
        Assert.AreEqual("pcl-lite", repo);
    }

    [TestMethod]
    public void ParseShouldStripGitSuffixAndQuotes()
    {
        var (owner, repo) = UpdateManifestPointer.Parse("\"https://github.com/askbbtsk/pcl-lite.git\"");
        Assert.AreEqual("askbbtsk", owner);
        Assert.AreEqual("pcl-lite", repo);
    }

    [TestMethod]
    public void ParseShouldRejectNonPointer()
    {
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Parse("404: Not Found"));
        Assert.ThrowsExactly<FormatException>(() => UpdateManifestPointer.Parse(""));
    }
}
