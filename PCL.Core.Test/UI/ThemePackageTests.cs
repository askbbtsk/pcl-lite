using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.UI.Theme;
using PCL.Core.UI.Theme.Packages;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class ThemePackageTests
{
    private string _root = null!;
    private ThemePackageService _service = null!;
    private const string Header = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:theme="clr-namespace:PCL.Core.UI.Theme;assembly=PCL.Core">
        """;
    private const string Basic = """
        <SolidColorBrush x:Key="ColorBrush1">#FF123456</SolidColorBrush>
        <ControlTemplate x:Key="Pcl.Theme.Template.Button" TargetType="{x:Type theme:ThemePresenter}">
          <Border CornerRadius="{TemplateBinding CornerRadius}" Background="{DynamicResource ColorBrush1}">
            <ContentPresenter Content="{TemplateBinding Content}" />
          </Border>
        </ControlTemplate>
        """;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl-theme-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new ThemePackageService(Path.Combine(_root, "Themes"), Path.Combine(_root, "Temp"));
    }
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Source(string content, ThemeManifest? manifest = null)
    {
        string root = Path.Combine(_root, "Source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Theme.xaml"), Header + content + "</ResourceDictionary>", Encoding.UTF8);
        manifest ??= new ThemeManifest { Id = "test.theme", Name = "测试主题", Author = "Tester" };
        File.WriteAllBytes(Path.Combine(root, "theme.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, ThemeApi.JsonOptions));
        return root;
    }

    [TestMethod]
    public async Task InstallAndUpdatePreserveSourcesAndRejectUnconfirmedReplacement()
    {
        string source = Source(Basic);
        var package = await _service.InspectAsync(source);
        await _service.InstallAsync(package, false);
        Assert.AreEqual("test.theme", _service.Scan().Single().Manifest!.Id);
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(source, "Theme.xaml")),
            File.ReadAllBytes(Path.Combine(_service.ThemesDirectory, "test.theme", "Theme.xaml")));
        await Assert.ThrowsExactlyAsync<IOException>(() => _service.InstallAsync(package, false));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => _service.InstallAsync(package, true, cancelled.Token));
        Assert.IsTrue(File.Exists(Path.Combine(source, "Theme.xaml")));
        Assert.IsFalse(Directory.EnumerateDirectories(_root, ".theme-*").Any());
        var installed = await _service.InspectAsync(Path.Combine(_service.ThemesDirectory, "test.theme"));
        Assert.AreEqual(package.ContentHash, installed.ContentHash);
    }

    [TestMethod]
    public async Task SingleXamlImportsOnlyReferencedFilesAndGeneratesHonestMetadata()
    {
        string source = Source("""
            <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Palette.xaml" /></ResourceDictionary.MergedDictionaries>
            """ + Basic);
        File.WriteAllText(Path.Combine(source, "Palette.xaml"), Header + "<SolidColorBrush x:Key=\"Accent\">Red</SolidColorBrush></ResourceDictionary>");
        File.WriteAllText(Path.Combine(source, "private.txt"), "not a theme dependency");
        var package = await _service.InspectAsync(Path.Combine(source, "Theme.xaml"));
        Assert.AreEqual("", package.Manifest.Author);
        Assert.IsNull(package.Manifest.LauncherVersion);
        Assert.IsTrue(package.Files.ContainsKey("Palette.xaml"));
        Assert.IsFalse(package.Files.ContainsKey("private.txt"));
        Assert.AreEqual(3, package.Files.Count);
    }

    [TestMethod]
    public async Task ForceConsentChangesWhenVersionOrContentChanges()
    {
        string source = Source(Basic, new ThemeManifest { Id = "test.theme", Name = "Test", LauncherVersion = ">=9.0.0 <10.0.0" });
        var package = await _service.InspectAsync(source);
        Assert.IsFalse(package.Manifest.IsCompatible("1.7.7-hotfix-6"));
        Assert.IsTrue(package.Manifest.IsCompatible("9.1.0"));
        string key = package.ForceConsentKey("1.7.7");
        Assert.AreNotEqual(key, package.ForceConsentKey("1.7.8"));
        File.AppendAllText(Path.Combine(source, "Theme.xaml"), "\n");
        Assert.AreNotEqual(key, (await _service.InspectAsync(source)).ForceConsentKey("1.7.7"));
    }

    [TestMethod]
    public async Task ZipTraversalDuplicatesLinksAndMissingResourcesLeaveNoInstall()
    {
        foreach (string name in new[] { "../Theme.xaml", "C:/Theme.xaml", "/Theme.xaml", "Theme.xaml:stream", "folder/../Theme.xaml" })
        {
            string zip = Path.Combine(_root, Guid.NewGuid() + ".zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("bad");
            await Assert.ThrowsAsync<InvalidDataException>(() => _service.InspectAsync(zip));
        }
        string duplicate = Path.Combine(_root, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Theme.xaml");
            archive.CreateEntry("theme.xaml");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.InspectAsync(duplicate));
        string link = Path.Combine(_root, "link.zip");
        using (var archive = ZipFile.Open(link, ZipArchiveMode.Create))
            archive.CreateEntry("Theme.xaml").ExternalAttributes = unchecked((int)0xA1FF0000);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.InspectAsync(link));
        string source = Source("<Image x:Key=\"Picture\" Source=\"missing.png\" />");
        await Assert.ThrowsAsync<FileNotFoundException>(() => _service.InspectAsync(source));
        Assert.IsFalse(Directory.Exists(_service.ThemesDirectory));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "Temp")) && Directory.EnumerateFileSystemEntries(Path.Combine(_root, "Temp")).Any());
    }

    [TestMethod]
    public async Task CyclicDictionaryAndRootEscapeAreRejectedBeforeWpf()
    {
        string source = Source("""
            <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Theme.xaml" /></ResourceDictionary.MergedDictionaries>
            """);
        await Assert.ThrowsAsync<ThemePackageException>(() => _service.InspectAsync(source));
        string escape = Source("<Image x:Key=\"Picture\" Source=\"../private.png\" />");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.InspectAsync(escape));
    }

    [TestMethod]
    public void RejectsExecutableTypesEventsRemoteResourcesAndBindingEscapes()
    {
        foreach (string fragment in new[]
        {
            "<ObjectDataProvider x:Key=\"a\" />",
            "<Frame x:Key=\"a\" Source=\"https://example.com\" />",
            "<Image x:Key=\"a\" Source=\"https://example.com/a.png\" />",
            "<Image x:Key=\"a\" Source=\"file:///C:/private.png\" />",
            "<Image x:Key=\"a\" Source=\"{Binding Content}\" />",
            "<Style x:Key=\"a\"><Setter Property=\"FontFamily\" Value=\"http:evil\" /></Style>",
            "<Border x:Key=\"a\" Loaded=\"RunCode\" />",
            "<Border x:Key=\"a\" Background=\"{x:Static Application.Current}\" />",
            "<Border x:Key=\"a\" Background=\"{Binding Source={x:Static Application.Current}}\" />",
            "<Style x:Key=\"a\" TargetType=\"Image\"><Setter Property=\"Source\" Value=\"https://example.com/a.png\" /></Style>",
            "<Style x:Key=\"a\" TargetType=\"TextBox\"><Setter Property=\"IsReadOnly\" Value=\"True\" /></Style>",
            "<Border x:Key=\"a\"><Border.Resources><ResourceDictionary Source=\"../../evil.xaml\" /></Border.Resources></Border>",
            "<Border x:Key=\"{x:Static Application.Current}\" />",
            "<FontFamily x:Key=\"a\">https://example.com/font.ttf#Font</FontFamily>",
            "<Storyboard x:Key=\"a\"><DoubleAnimation Duration=\"Forever\" /></Storyboard>",
            "<Storyboard x:Key=\"a\"><DoubleAnimation Duration=\"00:00:01\" RepeatBehavior=\"Forever\" /></Storyboard>"
        })
        {
            Assert.Throws<Exception>(() => ThemeXamlPolicy.Validate(Encoding.UTF8.GetBytes(Header + fragment + "</ResourceDictionary>"), "Theme.xaml"),
                fragment);
        }
        Assert.Throws<ThemePackageException>(() => ThemeXamlPolicy.ReadDocument(
            Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/private'>]><x>&e;</x>"), "Theme.xaml"));
    }

    [TestMethod]
    public void EditableTemplatesMustContainRequiredParts()
    {
        string textbox = """<ControlTemplate x:Key="Pcl.Theme.Template.TextBox" TargetType="TextBox"><Border /></ControlTemplate>""";
        Assert.Throws<ThemePackageException>(() => ThemeXamlPolicy.Validate(
            Encoding.UTF8.GetBytes(Header + textbox + "</ResourceDictionary>"), "Theme.xaml"));
        string button = """<ControlTemplate x:Key="Pcl.Theme.Template.Button" TargetType="{x:Type theme:ThemePresenter}"><Border /></ControlTemplate>""";
        Assert.Throws<ThemePackageException>(() => ThemeXamlPolicy.Validate(
            Encoding.UTF8.GetBytes(Header + button + "</ResourceDictionary>"), "Theme.xaml"));
    }

    [TestMethod]
    public async Task FrozenSnapshotCanLoadWithoutSourceFilesAndResolveModeOverrides()
    {
        string source = Source(Basic, new ThemeManifest { Id = "test.theme", Name = "Test", Dark = "Dark.xaml" });
        File.WriteAllText(Path.Combine(source, "Dark.xaml"), Header +
            "<SolidColorBrush x:Key=\"ColorBrush1\">#FFEEEEEE</SolidColorBrush></ResourceDictionary>");
        var package = await _service.InspectAsync(source);
        Directory.Delete(source, true);
        OnSta(() =>
        {
            var loader = new ThemeResourceLoader();
            var light = loader.Load(package, false, false);
            var dark = loader.Load(package, true, false);
            Assert.AreEqual(Color.FromRgb(0x12, 0x34, 0x56), ((SolidColorBrush)light["ColorBrush1"]).Color);
            Assert.AreEqual(Color.FromRgb(0xEE, 0xEE, 0xEE), ((SolidColorBrush)dark["ColorBrush1"]).Color);
            var template = (ControlTemplate)light["Pcl.Theme.Template.Button"];
            var presenter = new ThemePresenter { Content = new TextBlock { Text = "content remains owned by host" }, Template = template };
            presenter.Resources.MergedDictionaries.Add(light);
            presenter.ApplyTemplate();
            presenter.Measure(new Size(300, 100));
            Assert.IsNotNull(presenter.Content);
        });
    }

    [TestMethod]
    public void PreviewAndInterruptedStartupStateRoundTripsWithoutTouchingNormalConfig()
    {
        string normalConfig = Path.Combine(_root, "config.v1.yml");
        File.WriteAllText(normalConfig, "user-setting: keep");
        var store = new ThemeSelectionStore(_service.ThemesDirectory);
        Assert.IsNull(store.Read().Id);
        var pending = new ThemeSelection("test.theme", "hash", "consent", true);
        store.Write(pending);
        Assert.AreEqual(pending, store.Read());
        store.Write(pending with { Pending = false });
        Assert.IsFalse(store.Read().Pending);
        store.Write(new());
        Assert.IsNull(store.Read().Id);
        Assert.AreEqual("user-setting: keep", File.ReadAllText(normalConfig));
        Assert.AreEqual(TimeSpan.FromSeconds(15), ThemeApi.PreviewDuration);
    }

    [TestMethod]
    public void PreviewDeadlineStartsAfterPresentationAndExpiredClicksCannotAccept()
    {
        var clock = new PreviewClock();
        var preview = new ThemePreviewSession(clock);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.AreEqual(15, preview.RemainingSeconds);
        preview.MarkPresented();
        clock.Advance(TimeSpan.FromSeconds(14.1));
        Assert.AreEqual(1, preview.RemainingSeconds);
        preview.MarkPresented(); // Repeated rendering must not reset the deadline.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, preview.RemainingSeconds);
        Assert.IsTrue(preview.TryFinish(true));
        Assert.AreEqual(false, preview.Result);
        Assert.IsFalse(preview.TryFinish(true));
        var keep = new ThemePreviewSession(clock);
        keep.MarkPresented();
        Assert.IsTrue(keep.TryFinish(true));
        Assert.AreEqual(true, keep.Result);
        var cancel = new ThemePreviewSession(clock);
        Assert.IsTrue(cancel.TryFinish(false));
        Assert.AreEqual(false, cancel.Result);
    }

    private sealed class PreviewClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }

    [TestMethod]
    public async Task ShippedExampleLoadsEveryModeAndCannotLoseContentSlots()
    {
        string? repository = AppContext.BaseDirectory;
        while (repository is not null && !Directory.Exists(Path.Combine(repository, "examples", "themes", "SoftSlate")))
            repository = Path.GetDirectoryName(repository.TrimEnd(Path.DirectorySeparatorChar));
        Assert.IsNotNull(repository);
        var package = await _service.InspectAsync(Path.Combine(repository, "examples", "themes", "SoftSlate"));
        // Launcher-specific editable ComboBox parts are exercised by ThemeSmoke,
        // which references the real launcher. Core tests do not load that assembly.
        var files = package.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var entryDocument = ThemeXamlPolicy.ReadDocument(files[package.Manifest.Entry], package.Manifest.Entry);
        entryDocument.Descendants(ThemeXamlPolicy.Presentation + "ControlTemplate")
            .Where(element => (string?)element.Attribute(ThemeXamlPolicy.Xaml + "Key") == "Pcl.Theme.Template.ComboBox")
            .Remove();
        files[package.Manifest.Entry] = Encoding.UTF8.GetBytes(entryDocument.ToString());
        package = package with { Files = files };
        OnSta(() =>
        {
            foreach (bool dark in new[] { false, true })
            foreach (bool glass in new[] { false, true })
            {
                var resources = new ThemeResourceLoader().Load(package, dark, glass);
                foreach (string role in new[] { "Button", "Card", "Navigation", "CheckBox", "ListItem", "Hint", "TextBox" })
                {
                    Assert.IsInstanceOfType<ControlTemplate>(resources["Pcl.Theme.Template." + role]);
                    var template = (ControlTemplate)resources["Pcl.Theme.Template." + role];
                    Assert.IsNotNull(template.LoadContent());
                }
                Assert.AreEqual(glass ? (byte)0x30 : (byte)0xFF,
                    ((SolidColorBrush)resources["Pcl.Theme.Surface"]).Color.A);
            }
        });
    }

    [TestMethod]
    public async Task MergedDictionaryAssetsResolveRelativeToTheirOwnFileAndSurviveSourceDeletion()
    {
        string source = Source("""
            <ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Nested/Images.xaml" /></ResourceDictionary.MergedDictionaries>
            """ + Basic);
        Directory.CreateDirectory(Path.Combine(source, "Nested"));
        File.WriteAllText(Path.Combine(source, "Nested", "Images.xaml"), Header +
            "<ImageBrush x:Key=\"Picture\" ImageSource=\"pixel.png\" /></ResourceDictionary>");
        OnSta(() =>
        {
            var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(source, "Nested", "pixel.png"));
            encoder.Save(output);
        });
        var package = await _service.InspectAsync(source);
        Directory.Delete(source, true);
        OnSta(() =>
        {
            var resources = new ThemeResourceLoader().Load(package, false, false);
            var brush = (ImageBrush)resources["Picture"];
            var host = new Border { Background = brush, Width = 20, Height = 20 };
            host.Resources.MergedDictionaries.Add(resources);
            host.Measure(new Size(20, 20));
            host.Arrange(new Rect(0, 0, 20, 20));
            Assert.IsNotNull(brush.ImageSource);
        });
    }

    private static void OnSta(Action test)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { test(); } catch (Exception failure) { error = failure; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
