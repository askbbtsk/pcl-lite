using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Localization;

namespace PCL.Core.Test;

[TestClass]
public class HomepageLanguageApiTest
{
    #region 代码归一化

    [TestMethod]
    public void NormalizeCodeShouldLowercaseAndUnifySeparator()
    {
        Assert.AreEqual("zh-cn", HomepageLanguageApi.NormalizeCode("zh-CN"));
        Assert.AreEqual("zh-cn", HomepageLanguageApi.NormalizeCode("zh_CN"));
        Assert.AreEqual("en-us", HomepageLanguageApi.NormalizeCode(" EN-us "));
        Assert.AreEqual(string.Empty, HomepageLanguageApi.NormalizeCode(null));
        Assert.AreEqual(string.Empty, HomepageLanguageApi.NormalizeCode("  "));
    }

    [TestMethod]
    public void GetNeutralCodeShouldStripRegion()
    {
        Assert.AreEqual("zh", HomepageLanguageApi.GetNeutralCode("zh-CN"));
        Assert.AreEqual("zh", HomepageLanguageApi.GetNeutralCode("zh-TW"));
        Assert.AreEqual("en", HomepageLanguageApi.GetNeutralCode("en-GB"));
        Assert.AreEqual("zh", HomepageLanguageApi.GetNeutralCode("zh"));
        Assert.AreEqual(string.Empty, HomepageLanguageApi.GetNeutralCode(string.Empty));
    }

    #endregion

    #region 标签匹配

    [TestMethod]
    public void LanguageTagShouldMatchExactAndNeutralCode()
    {
        Assert.IsTrue(HomepageLanguageApi.IsLanguageTagMatch("lang:zh-CN", "zh-CN"));
        Assert.IsTrue(HomepageLanguageApi.IsLanguageTagMatch("lang:zh-cn", "zh-CN"));
        Assert.IsFalse(HomepageLanguageApi.IsLanguageTagMatch("lang:zh-TW", "zh-CN"));

        // 中性分支应同时命中同语言的不同区域
        Assert.IsTrue(HomepageLanguageApi.IsLanguageTagMatch("lang:zh", "zh-CN"));
        Assert.IsTrue(HomepageLanguageApi.IsLanguageTagMatch("lang:zh", "zh-TW"));
        Assert.IsTrue(HomepageLanguageApi.IsLanguageTagMatch("lang:en", "en-US"));
        Assert.IsTrue(HomepageLanguageApi.IsLanguageTagMatch("lang:en", "en-GB"));

        Assert.IsFalse(HomepageLanguageApi.IsLanguageTagMatch("lang:ja", "zh-CN"));
        Assert.IsFalse(HomepageLanguageApi.IsLanguageTagMatch("lang:en-US", "zh-CN"));
    }

    [TestMethod]
    public void NonLanguageTagShouldNeverMatch()
    {
        Assert.IsFalse(HomepageLanguageApi.IsLanguageTag("welcome-status"));
        Assert.IsFalse(HomepageLanguageApi.IsLanguageTag(null));
        Assert.IsFalse(HomepageLanguageApi.IsLanguageTag(42));
        Assert.IsFalse(HomepageLanguageApi.IsLanguageTagMatch("welcome-status", "zh-CN"));
    }

    [TestMethod]
    public void DefaultLanguageTagShouldBeRecognized()
    {
        Assert.IsTrue(HomepageLanguageApi.IsDefaultLanguageTag("lang:default"));
        Assert.IsTrue(HomepageLanguageApi.IsDefaultLanguageTag(" lang:DEFAULT "));
        Assert.IsFalse(HomepageLanguageApi.IsDefaultLanguageTag("lang:zh-CN"));
        Assert.IsFalse(HomepageLanguageApi.IsDefaultLanguageTag(null));
    }

    #endregion

    #region 整页内容选择

    [TestMethod]
    public void PlainXamlShouldPassThroughUnchanged()
    {
        const string xaml = "<TextBlock Text=\"你好\" />";
        Assert.AreEqual(xaml, HomepageLanguageApi.SelectContent(xaml, "zh-CN"));
        // 原版协议：除语言协商外不做任何文本改写，前导空白也原样保留
        Assert.AreEqual("  " + xaml, HomepageLanguageApi.SelectContent("  " + xaml, "en-US"));
        Assert.AreEqual("\n" + xaml, HomepageLanguageApi.SelectContent("\n" + xaml, "en-US"));
    }

    [TestMethod]
    public void EmptyContentShouldStayEmpty()
    {
        Assert.AreEqual(string.Empty, HomepageLanguageApi.SelectContent(string.Empty, "zh-CN"));
        Assert.AreEqual(string.Empty, HomepageLanguageApi.SelectContent(null, "zh-CN"));
        Assert.AreEqual("   ", HomepageLanguageApi.SelectContent("   ", "zh-CN"));
    }

    [TestMethod]
    public void LanguageEnvelopeShouldSelectExactBranch()
    {
        var content = """
        {
          "format": "pcl-lite-homepage-lang",
          "version": 1,
          "default": "<TextBlock Text=\"fallback\" />",
          "languages": {
            "zh-CN": "<TextBlock Text=\"简体\" />",
            "zh-TW": "<TextBlock Text=\"繁體\" />",
            "en": "<TextBlock Text=\"English\" />"
          }
        }
        """;

        Assert.AreEqual("<TextBlock Text=\"简体\" />", HomepageLanguageApi.SelectContent(content, "zh-CN"));
        Assert.AreEqual("<TextBlock Text=\"繁體\" />", HomepageLanguageApi.SelectContent(content, "zh-TW"));
    }

    [TestMethod]
    public void LanguageEnvelopeShouldFallBackToNeutralBranch()
    {
        var content = """
        {
          "languages": {
            "en": "<TextBlock Text=\"English\" />"
          }
        }
        """;

        Assert.AreEqual("<TextBlock Text=\"English\" />", HomepageLanguageApi.SelectContent(content, "en-US"));
        Assert.AreEqual("<TextBlock Text=\"English\" />", HomepageLanguageApi.SelectContent(content, "en-GB"));
    }

    [TestMethod]
    public void LanguageEnvelopeShouldFallBackToDefaultBranch()
    {
        var content = """
        {
          "default": "<TextBlock Text=\"fallback\" />",
          "languages": {
            "zh-CN": "<TextBlock Text=\"简体\" />"
          }
        }
        """;

        Assert.AreEqual("<TextBlock Text=\"fallback\" />", HomepageLanguageApi.SelectContent(content, "fr-FR"));
        Assert.AreEqual("<TextBlock Text=\"简体\" />", HomepageLanguageApi.SelectContent(content, "zh-CN"));
    }

    [TestMethod]
    public void LanguageEnvelopeWithoutAnyMatchShouldBlank()
    {
        var content = """
        {
          "languages": {
            "zh-CN": "<TextBlock Text=\"简体\" />"
          }
        }
        """;

        Assert.AreEqual(string.Empty, HomepageLanguageApi.SelectContent(content, "ja-JP"));
    }

    [TestMethod]
    public void NonEnvelopeJsonShouldPassThrough()
    {
        const string content = "{ \"some\": \"other\", \"json\": 1 }";
        Assert.AreEqual(content, HomepageLanguageApi.SelectContent(content, "zh-CN"));
    }

    [TestMethod]
    public void MalformedJsonShouldPassThroughForXamlParser()
    {
        const string content = "{ this is not json at all ";
        Assert.AreEqual(content, HomepageLanguageApi.SelectContent(content, "zh-CN"));
    }

    #endregion

    #region 可视化树过滤

    /// <summary>
    ///     WPF 控件要求 STA 线程，测试运行器默认是 MTA，所以可视化树相关用例都放到 STA 线程里跑。
    /// </summary>
    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static StackPanel BuildTree()
    {
        var root = new StackPanel();

        var plain = new TextBlock { Text = "plain" };
        root.Children.Add(plain);

        var zh = new TextBlock { Text = "简体", Tag = "lang:zh-CN" };
        var tw = new TextBlock { Text = "繁體", Tag = "lang:zh-TW" };
        var en = new TextBlock { Text = "English", Tag = "lang:en" };
        root.Children.Add(zh);
        root.Children.Add(tw);
        root.Children.Add(en);

        // 嵌套：卡片内容里再放语言分支（含 default 兜底）
        var card = new Border();
        var cardPanel = new StackPanel();
        cardPanel.Children.Add(new TextBlock { Text = "卡片标题" });
        cardPanel.Children.Add(new TextBlock { Text = "卡片英文", Tag = "lang:en" });
        cardPanel.Children.Add(new TextBlock { Text = "卡片兜底", Tag = "lang:default" });
        card.Child = cardPanel;
        root.Children.Add(card);

        return root;
    }

    [TestMethod]
    public void ApplyToTreeShouldKeepOnlyMatchingBranch()
    {
        RunInSta(() =>
        {
            var root = BuildTree();
            var removed = HomepageLanguageApi.ApplyToTree(root, "zh-CN");

            // 根下移除 zh-TW 与 en 两个分支，嵌套面板里再移除一个 en 分支
            Assert.AreEqual(3, removed);
            Assert.IsTrue(root.Children.OfType<TextBlock>().Any(b => (string?)b.Tag == "lang:zh-CN"));
            Assert.IsFalse(root.Children.OfType<TextBlock>().Any(b => (string?)b.Tag == "lang:zh-TW"));
            Assert.IsFalse(root.Children.OfType<TextBlock>().Any(b => (string?)b.Tag == "lang:en"));
            Assert.AreEqual(1, root.Children.OfType<TextBlock>().Count(b => b.Tag is null));
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldKeepNeutralBranchForEveryRegion()
    {
        RunInSta(() =>
        {
            foreach (var code in new[] { "en-US", "en-GB" })
            {
                var root = BuildTree();
                HomepageLanguageApi.ApplyToTree(root, code);
                Assert.IsTrue(root.Children.OfType<TextBlock>().Any(b => (string?)b.Tag == "lang:en"), code);
                Assert.IsFalse(root.Children.OfType<TextBlock>().Any(b => (string?)b.Tag == "lang:zh-CN"), code);
                Assert.IsFalse(root.Children.OfType<TextBlock>().Any(b => (string?)b.Tag == "lang:zh-TW"), code);
            }
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldRecurseIntoNestedPanels()
    {
        RunInSta(() =>
        {
            // zh-CN：嵌套面板里 lang:en 分支被移除，default 兜底保留
            var root = BuildTree();
            HomepageLanguageApi.ApplyToTree(root, "zh-CN");

            var border = root.Children.OfType<Border>().Single();
            var panel = (StackPanel)border.Child;
            Assert.AreEqual(2, panel.Children.Count);
            Assert.AreEqual("卡片标题", ((TextBlock)panel.Children[0]).Text);
            Assert.AreEqual("卡片兜底", ((TextBlock)panel.Children[1]).Text);
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldRecurseIntoNestedPanelsForEnglish()
    {
        RunInSta(() =>
        {
            // en-US：嵌套面板里 lang:en 分支命中，default 兜底让位
            var root = BuildTree();
            HomepageLanguageApi.ApplyToTree(root, "en-US");

            var border = root.Children.OfType<Border>().Single();
            var panel = (StackPanel)border.Child;
            Assert.AreEqual(2, panel.Children.Count);
            Assert.AreEqual("卡片标题", ((TextBlock)panel.Children[0]).Text);
            Assert.AreEqual("卡片英文", ((TextBlock)panel.Children[1]).Text);
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldUseDefaultBranchWhenNoMatch()
    {
        RunInSta(() =>
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = "日文", Tag = "lang:ja" });
            root.Children.Add(new TextBlock { Text = "兜底", Tag = "lang:default" });

            var removed = HomepageLanguageApi.ApplyToTree(root, "zh-CN");

            Assert.AreEqual(1, removed);
            Assert.AreEqual(1, root.Children.Count);
            Assert.AreEqual("兜底", ((TextBlock)root.Children[0]).Text);
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldPreferSpecificBranchOverDefault()
    {
        RunInSta(() =>
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = "简体", Tag = "lang:zh-CN" });
            root.Children.Add(new TextBlock { Text = "兜底", Tag = "lang:default" });

            HomepageLanguageApi.ApplyToTree(root, "zh-CN");

            Assert.AreEqual(1, root.Children.Count);
            Assert.AreEqual("简体", ((TextBlock)root.Children[0]).Text);
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldKeepEverythingWhenNothingMatches()
    {
        RunInSta(() =>
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = "日文", Tag = "lang:ja" });
            root.Children.Add(new TextBlock { Text = "法文", Tag = "lang:fr" });

            var removed = HomepageLanguageApi.ApplyToTree(root, "zh-CN");

            // 没有任何分支命中时保留全部，避免主页因为漏翻变成空白
            Assert.AreEqual(0, removed);
            Assert.AreEqual(2, root.Children.Count);
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldIgnoreUntaggedElements()
    {
        RunInSta(() =>
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = "a" });
            root.Children.Add(new TextBlock { Text = "b", Tag = "welcome-status" });

            var removed = HomepageLanguageApi.ApplyToTree(root, "zh-CN");

            Assert.AreEqual(0, removed);
            Assert.AreEqual(2, root.Children.Count);
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldHandleContentControlChildren()
    {
        RunInSta(() =>
        {
            var button = new Button();
            button.Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "英文", Tag = "lang:en" },
                    new TextBlock { Text = "中文", Tag = "lang:zh-CN" }
                }
            };

            HomepageLanguageApi.ApplyToTree(button, "zh-CN");

            var panel = (StackPanel)button.Content;
            Assert.AreEqual(1, panel.Children.Count);
            Assert.AreEqual("中文", ((TextBlock)panel.Children[0]).Text);
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldHandleNullRoot()
    {
        Assert.AreEqual(0, HomepageLanguageApi.ApplyToTree(null, "zh-CN"));
    }

    #endregion

    #region 双端兼容写法（非默认分支预先 Collapsed）

    /// <summary>
    /// 按双端兼容写法搭一棵树：非默认分支都带 Visibility=Collapsed，
    /// 这样原版 PCL 只会看到 default 分支。
    /// </summary>
    private static StackPanel BuildCompatTree()
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock
            { Text = "English", Tag = "lang:en", Visibility = Visibility.Collapsed });
        root.Children.Add(new TextBlock
            { Text = "繁體", Tag = "lang:zh-TW", Visibility = Visibility.Collapsed });
        root.Children.Add(new TextBlock { Text = "简体兜底", Tag = "lang:default" });
        return root;
    }

    [TestMethod]
    public void ApplyToTreeShouldRevealCollapsedMatchingBranch()
    {
        RunInSta(() =>
        {
            var root = BuildCompatTree();

            var removed = HomepageLanguageApi.ApplyToTree(root, "en-US", out var revealed);

            Assert.AreEqual(2, removed, "应当移除 zh-TW 与 default 两个分支");
            Assert.AreEqual(1, revealed, "命中的 en 分支应当从 Collapsed 改回 Visible");

            Assert.AreEqual(1, root.Children.Count);
            var winner = (TextBlock)root.Children[0];
            Assert.AreEqual("English", winner.Text);
            Assert.AreEqual(Visibility.Visible, winner.Visibility);
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldKeepDefaultVisibleWhenNoBranchMatches()
    {
        RunInSta(() =>
        {
            var root = BuildCompatTree();

            var removed = HomepageLanguageApi.ApplyToTree(root, "zh-CN", out var revealed);

            // zh-CN 只命中 default，en 与 zh-TW 都被移除，不需要 reveal（default 本来就是可见的）
            Assert.AreEqual(2, removed);
            Assert.AreEqual(0, revealed);

            Assert.AreEqual(1, root.Children.Count);
            Assert.AreEqual("简体兜底", ((TextBlock)root.Children[0]).Text);
        });
    }

    [TestMethod]
    public void ApplyToTreeShouldNotReportRevealForAlreadyVisibleWinner()
    {
        RunInSta(() =>
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = "English", Tag = "lang:en" });
            root.Children.Add(new TextBlock { Text = "兜底", Tag = "lang:default" });

            HomepageLanguageApi.ApplyToTree(root, "en-US", out var revealed);

            Assert.AreEqual(0, revealed, "本来就可见的分支不该计入 reveal");
        });
    }

    #endregion
}
