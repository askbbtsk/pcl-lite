using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Input;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Navigation;

namespace PCL.Core.Test.UI.Navigation;

[TestClass]
public class CommandPaletteSearchTests
{
    private static readonly CommandPaletteEntry[] AllPageEntries =
    [
        new("launch", "启动", "打开页面", "常用操作", "home launch 主页 啟動"),
        new("instances", "实例选择", "打开页面", "常用操作", "instances 实例 實例"),
        new("tasks", "任务管理", "打开页面", "常用操作", "tasks downloads 任务 任務 下载 下載"),
        new("install", "自动安装", "打开页面", "常用操作", "install minecraft 安装 安裝"),
        new("mods", "Mod 下载", "打开页面", "常用操作", "download mods 模组 模組"),
        new("java", "Java 管理", "打开页面", "设置", "java runtime jdk 运行时 執行階段"),
        new("launch-settings", "启动设置", "打开页面", "设置", "memory ram jvm 内存 記憶體 启动 啟動 参数 參數"),
        new("appearance", "个性化", "打开页面", "设置", "theme wallpaper background glass 主题 主題 壁纸 桌布 背景 玻璃"),
        new("language", "语言", "打开页面", "设置", "language locale 语言 語言"),
        new("game-settings", "游戏管理", "打开页面", "设置", "game isolation mods 游戏 遊戲 隔离 隔離 模组 模組"),
        new("misc", "其他", "打开页面", "设置", "proxy network mcp 代理 网络 網路"),
        new("update", "更新", "打开页面", "设置", "update channel 更新 通道"),
        new("about", "关于", "打开页面", "设置", "about version 关于 關於 版本"),
        new("plugins", "插件", "打开页面", "常用操作", "plugins 插件 外掛")
    ];

    [TestMethod]
    public void TitleMatchesOutrankMetadataAndPreserveTies()
    {
        CommandPaletteEntry[] entries =
        [
            new("metadata", "Other", "", "", "fabric"),
            new("substring", "My Fabric pack", "", ""),
            new("prefix", "Fabric API", "", ""),
            new("exact", "Fabric", "", ""),
            new("tie", "Fabric", "", "")
        ];
        CollectionAssert.AreEqual(new[] { "exact", "tie", "prefix", "substring", "metadata" },
            CommandPaletteSearch.Search(entries, "FABRIC").Select(entry => entry.Id).ToArray());
    }

    [TestMethod]
    public void EveryWordMustMatchButMayMatchDifferentFields()
    {
        CommandPaletteEntry[] entries =
        [
            new("sodium", "Sodium", "sodium-fabric.jar.disabled", "已安装模组", "渲染"),
            new("iris", "Iris", "iris-fabric.jar", "已安装模组")
        ];
        Assert.AreEqual("sodium", CommandPaletteSearch.Search(entries, "  sodium\tDISABLED\n模组  ").Single().Id);
        Assert.AreEqual(0, CommandPaletteSearch.Search(entries, "sodium forge").Count);
        Assert.AreEqual("sodium", CommandPaletteSearch.Search(entries, "渲染").Single().Id);
    }

    [TestMethod]
    public void EmptyQueriesKeepCatalogOrderAndLimitResults()
    {
        var entries = Enumerable.Range(0, 1000).Select(index => new CommandPaletteEntry(index.ToString(), "Same", "", "")).ToArray();
        var matches = CommandPaletteSearch.Search(entries, " \t");
        Assert.AreEqual(60, matches.Count);
        Assert.AreEqual("0", matches[0].Id);
        Assert.AreEqual("59", matches[^1].Id);
        Assert.AreEqual(0, CommandPaletteSearch.Search(entries, null, 0).Count);
        Assert.AreEqual(0, CommandPaletteSearch.Search([], "missing").Count);
    }

    [TestMethod]
    public void SameTitlesRemainSeparateCommands()
    {
        CommandPaletteEntry[] entries = [new("instance:a", "Pack", "", ""), new("instance:b", "Pack", "", "")];
        Assert.AreEqual(2, CommandPaletteSearch.Search(entries, "pack").Count);
    }

    [TestMethod]
    public void SwitchingInstanceWhileAlreadyInInstanceSetupRefreshesOverviewSidebarAndTitle()
    {
        var activeInstance = "InstanceA";
        var displayedOverviewInstance = "InstanceA";
        var displayedSidebarInstance = "InstanceA";
        var displayedTitleInstance = "InstanceA";
        var navigateCalls = 0;

        CommandPaletteSearch.SwitchManagedInstance(
            "InstanceB",
            isAlreadyInInstanceSetup: true,
            selected => activeInstance = selected,
            () => navigateCalls++,
            () => displayedSidebarInstance = activeInstance,
            () => displayedOverviewInstance = activeInstance,
            () => displayedTitleInstance = activeInstance);

        Assert.AreEqual(1, navigateCalls);
        Assert.AreEqual("InstanceB", activeInstance);
        Assert.AreEqual("InstanceB", displayedSidebarInstance);
        Assert.AreEqual("InstanceB", displayedOverviewInstance);
        Assert.AreEqual("InstanceB", displayedTitleInstance);
    }

    [TestMethod]
    public void OpeningInstanceFromOutsideInstanceSetupNavigatesWithoutDuplicateReload()
    {
        var activeInstance = "InstanceA";
        var sidebarRefreshes = 0;
        var overviewRefreshes = 0;
        var titleRefreshes = 0;

        CommandPaletteSearch.SwitchManagedInstance(
            "InstanceB",
            isAlreadyInInstanceSetup: false,
            selected => activeInstance = selected,
            () => { },
            () => sidebarRefreshes++,
            () => overviewRefreshes++,
            () => titleRefreshes++);

        Assert.AreEqual("InstanceB", activeInstance);
        Assert.AreEqual(0, sidebarRefreshes);
        Assert.AreEqual(0, overviewRefreshes);
        Assert.AreEqual(0, titleRefreshes);
    }

    [TestMethod]
    public void DeferredModScanSkipsNestedJarsRestoresStateAndHonorsCancellation()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "pcl-palette-jij-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var modAPath = Path.Combine(tempRoot, "alpha-parent.jar");
            var modBPath = Path.Combine(tempRoot, "beta-parent.jar");
            CreateModJarWithNestedJar(modAPath, "alpha_parent", "Alpha Parent", "META-INF/jars/nested-heavy-a.jar");
            CreateModJarWithNestedJar(modBPath, "beta_parent", "Beta Parent", "META-INF/jars/nested-heavy-b.jar");

            var deferJijOnMiss = false;
            var nestedArchiveExtractions = 0;

            var results = CommandPaletteSearch.ScanModFilesWithDeferredNestedArchive(
                tempRoot,
                path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase),
                path =>
                {
                    using var archive = ZipFile.OpenRead(path);
                    Assert.IsNotNull(archive.GetEntry("fabric.mod.json"));
                    if (!deferJijOnMiss)
                    {
                        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("META-INF/jars/", StringComparison.Ordinal)))
                        {
                            using var nestedStream = entry.Open();
                            nestedArchiveExtractions++;
                        }
                    }
                    return Path.GetFileNameWithoutExtension(path);
                },
                title => title,
                () => deferJijOnMiss = true,
                () => deferJijOnMiss = false,
                CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "alpha-parent", "beta-parent" }, results.ToArray());
            Assert.AreEqual(0, nestedArchiveExtractions, "Command palette scan must defer nested Jar-in-Jar extraction.");
            Assert.IsFalse(deferJijOnMiss, "Deferred state must be restored in finally.");

            using var cts = new CancellationTokenSource();
            var visitedCount = 0;
            Assert.Throws<OperationCanceledException>(() =>
                CommandPaletteSearch.ScanModFilesWithDeferredNestedArchive(
                    tempRoot,
                    path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase),
                    path =>
                    {
                        visitedCount++;
                        cts.Cancel();
                        return Path.GetFileName(path);
                    },
                    title => title,
                    () => deferJijOnMiss = true,
                    () => deferJijOnMiss = false,
                    cts.Token));

            Assert.AreEqual(1, visitedCount, "Cancellation must stop scanning remaining mod files.");
            Assert.IsFalse(deferJijOnMiss, "Deferred state must be restored after cancellation.");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public void FeatureHidingExcludesHiddenPagesUntilForceShowIsEnabled()
    {
        var hiddenPluginsAndDownload = new CommandPalettePageVisibility(
            HidePagePlugins: true,
            HidePageDownload: true,
            HideFunctionSelect: true);
        var visibleIds = CommandPaletteSearch.FilterVisiblePages(AllPageEntries, hiddenPluginsAndDownload)
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.IsFalse(visibleIds.Contains("plugins"));
        Assert.IsFalse(visibleIds.Contains("install"));
        Assert.IsFalse(visibleIds.Contains("mods"));
        Assert.IsFalse(visibleIds.Contains("instances"));
        Assert.IsTrue(visibleIds.Contains("launch"));
        Assert.IsTrue(visibleIds.Contains("java"));

        var hiddenSetupSubpages = new CommandPalettePageVisibility(
            HideSetupJava: true,
            HideSetupUi: true,
            HideSetupLauncherLanguage: true,
            HideSetupUpdate: true);
        var setupFiltered = CommandPaletteSearch.FilterVisiblePages(AllPageEntries, hiddenSetupSubpages)
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.IsFalse(setupFiltered.Contains("java"));
        Assert.IsTrue(setupFiltered.Contains("launch-settings"));
        Assert.IsFalse(setupFiltered.Contains("appearance"));
        Assert.IsFalse(setupFiltered.Contains("language"));
        Assert.IsFalse(setupFiltered.Contains("update"));
        Assert.IsTrue(setupFiltered.Contains("game-settings"));
        Assert.IsTrue(setupFiltered.Contains("misc"));
        Assert.IsTrue(setupFiltered.Contains("about"));

        var hiddenAllSetup = new CommandPalettePageVisibility(HidePageSetup: true);
        var noSetup = CommandPaletteSearch.FilterVisiblePages(AllPageEntries, hiddenAllSetup)
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsFalse(noSetup.Overlaps(new[] { "java", "launch-settings", "appearance", "language", "game-settings", "misc", "update", "about" }));

        var forceShowAll = hiddenPluginsAndDownload with { HidePageSetup = true, HiddenForceShow = true };
        Assert.AreEqual(AllPageEntries.Length, CommandPaletteSearch.FilterVisiblePages(AllPageEntries, forceShowAll).Count);
    }

    [TestMethod]
    public void CloseButtonFocusActivatesCloseOnEnterAndSpaceWithoutExecutingSelectedResult()
    {
        Assert.IsTrue(CommandPaletteSearch.ShouldActivateCloseButtonOnKey(Key.Enter, isCloseButtonFocused: true));
        Assert.IsTrue(CommandPaletteSearch.ShouldActivateCloseButtonOnKey(Key.Space, isCloseButtonFocused: true));
        Assert.IsFalse(CommandPaletteSearch.ShouldActivateCloseButtonOnKey(Key.Down, isCloseButtonFocused: true));
        Assert.IsFalse(CommandPaletteSearch.ShouldActivateCloseButtonOnKey(Key.Enter, isCloseButtonFocused: false));
    }

    [TestMethod]
    public void ShippedCommandPaletteUsesPclControlsAndAvoidsNativeButtonOrListBox()
    {
        var repoRoot = FindRepositoryRoot();
        var xamlPath = Path.Combine(repoRoot, "Plain Craft Launcher 2", "Controls", "CommandPalette.xaml");
        var codeBehindPath = Path.Combine(repoRoot, "Plain Craft Launcher 2", "Controls", "CommandPalette.xaml.cs");
        var formMainPalettePath = Path.Combine(repoRoot, "Plain Craft Launcher 2", "FormMain.CommandPalette.cs");

        var doc = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace local = "clr-namespace:PCL";

        Assert.AreEqual(0, doc.Descendants(presentation + "Button").Count(), "CommandPalette.xaml must not use native WPF Button.");
        Assert.AreEqual(0, doc.Descendants(presentation + "ListBox").Count(), "CommandPalette.xaml must not use native WPF ListBox.");

        var closeButton = doc.Descendants(local + "MyButton")
            .SingleOrDefault(e => (string?)e.Attribute(xaml + "Name") == "CloseButton");
        Assert.IsNotNull(closeButton, "CommandPalette.xaml must use local:MyButton named CloseButton.");
        Assert.AreEqual("Esc", (string?)closeButton.Attribute("Text"));

        var codeBehind = File.ReadAllText(codeBehindPath);
        StringAssert.Contains(codeBehind, "ShouldActivateCloseButtonOnKey");

        var formMainPalette = File.ReadAllText(formMainPalettePath);
        StringAssert.Contains(formMainPalette, "ModLocalComp.DeferJijOnMiss = true");
        StringAssert.Contains(formMainPalette, "ModJarInJarCache.UseInstance(instancePath)");
        StringAssert.Contains(formMainPalette, "RefreshModDisabled()");
        StringAssert.Contains(formMainPalette, "RefreshPackDetails()");
        StringAssert.Contains(formMainPalette, "IsPalettePageHidden");
    }

    private static void CreateModJarWithNestedJar(string jarPath, string id, string name, string nestedEntryPath)
    {
        using var fileStream = File.Create(jarPath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
        var metadataEntry = archive.CreateEntry("fabric.mod.json");
        using (var writer = new StreamWriter(metadataEntry.Open(), new UTF8Encoding(false)))
        {
            writer.Write($$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "version": "1.0.0",
              "name": "{{name}}",
              "jars": [
                { "file": "{{nestedEntryPath}}" }
              ]
            }
            """);
        }

        var nestedEntry = archive.CreateEntry(nestedEntryPath);
        using var nestedStream = nestedEntry.Open();
        using var nestedZip = new ZipArchive(nestedStream, ZipArchiveMode.Create);
        var innerMetadata = nestedZip.CreateEntry("fabric.mod.json");
        using var innerWriter = new StreamWriter(innerMetadata.Open(), new UTF8Encoding(false));
        innerWriter.Write("""{"schemaVersion":1,"id":"nested_mod","version":"1.0.0","name":"Nested Mod"}""");
    }

    private static string FindRepositoryRoot() => TestRepository.FindRoot();
}
