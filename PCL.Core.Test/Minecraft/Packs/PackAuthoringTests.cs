using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Java;
using PCL.Core.Minecraft.Java.UserPreference;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Utils;

namespace PCL.Core.Test.Minecraft.Packs;

[TestClass]
public sealed class PackAuthoringTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "PackAuthoring", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void LegacyAndLfOptionsAreCompatibleWithoutEnablingSlimMode()
    {
        var legacy = PackExportOptions.Parse(new Dictionary<string, string> { ["IncludeLauncher"] = "True" });
        Assert.AreEqual(BundledLauncher.Lite, legacy.Launcher);
        Assert.AreEqual("x64", legacy.LightArchitecture);
        Assert.IsFalse(legacy.IncludeVersionJava);
        Assert.IsTrue(legacy.VersionArgumentIndie);
        var lf = PackExportOptions.Parse(new Dictionary<string, string>
        {
            ["IncludeJava"] = "True", ["VersionIsolation"] = "False", ["LivingModpackMode"] = "True",
            ["LauncherType"] = "Light", ["LightArchitecture"] = "ARM64",
        });
        Assert.AreEqual(BundledLauncher.Light, lf.Launcher);
        Assert.IsTrue(lf.IncludeVersionJava);
        Assert.IsFalse(lf.VersionArgumentIndie);
        Assert.IsTrue(lf.IgnoredModpackMode);
        Assert.ThrowsExactly<InvalidDataException>(() => PackExportOptions.Parse(new Dictionary<string, string> { ["LightArchitecture"] = "x86" }));
    }

    [TestMethod]
    public void LightSelectionPinsArchitectureOriginAndChecksum()
    {
        var hash = new string('a', 64);
        var json = Release(hash);
        var x64 = LightReleaseResolver.Resolve(json, "x64");
        Assert.AreEqual("PCL-Light_1.0.0_x64.exe", x64.Name);
        Assert.AreEqual(hash, x64.Sha256);
        Assert.AreEqual("PCL-Light_1.0.0_ARM64.exe", LightReleaseResolver.Resolve(json, "ARM64").Name);
        Assert.AreEqual(hash, LightReleaseResolver.ReadChecksum(hash + "  " + x64.Name, x64.Name));
        Assert.ThrowsExactly<InvalidDataException>(() => LightReleaseResolver.ReadChecksum(hash + "  other.exe", x64.Name));
        Assert.ThrowsExactly<InvalidDataException>(() => LightReleaseResolver.Resolve(json.Replace("github.com", "evil.example"), "x64"));
        Assert.ThrowsExactly<InvalidDataException>(() => LightReleaseResolver.Resolve(json.Replace("\"prerelease\":false", "\"prerelease\":true"), "x64"));
    }

    [TestMethod]
    public void LightMissingDigestRequiresChecksumFileAndUniqueEntry()
    {
        var release = LightReleaseResolver.Resolve(Release(null), "x64");
        Assert.IsNull(release.Sha256);
        Assert.IsNotNull(release.ChecksumsUrl);
        var line = new string('b', 64) + " *" + release.Name;
        Assert.ThrowsExactly<InvalidDataException>(() => LightReleaseResolver.ReadChecksum(line + "\n" + line, release.Name));
    }

    [TestMethod]
    public void AtomicArchiveCancellationPreservesExistingOutput()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "new");
        var target = Path.Combine(_root, "pack.zip");
        File.WriteAllText(target, "old");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PackExportArchive.Create(source, target, cancel.Token));
        Assert.AreEqual("old", File.ReadAllText(target));
        Assert.AreEqual(0, Directory.GetFiles(_root, "*.tmp").Length);
        PackExportArchive.Create(source, target);
        using var archive = ZipFile.OpenRead(target);
        Assert.AreEqual("file.txt", archive.Entries.Single().FullName);
    }

    [TestMethod]
    public void FilterExcludesInternalArtifactsButKeepsPlayerContent()
    {
        foreach (var path in new[] { "PCLLiteData/WorldBackups/a.zip", "PCL/ModpackOrigin.json", "PCL/PackUpdateBackups/x", "PCL/ResourceRecycle/x", "launcher_accounts.json", ".x.pcl-update-abc/file" })
            Assert.IsTrue(PackExportContent.IsInternalPath(path), path);
        Assert.IsFalse(PackExportContent.IsInternalPath("config/player.json"));
        Assert.IsFalse(PackExportContent.IsInternalPath("saves/World/level.dat"));
        var source = Path.Combine(_root, "source"); var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(Path.Combine(source, "PCLLiteData"));
        File.WriteAllText(Path.Combine(source, "PCLLiteData", "private.json"), "private");
        File.WriteAllText(Path.Combine(source, "options.txt"), "player settings");
        PackExportContent.CopyTree(source, output);
        Assert.IsFalse(Directory.Exists(Path.Combine(output, "PCLLiteData")));
        Assert.AreEqual("player settings", File.ReadAllText(Path.Combine(output, "options.txt")));
    }

    [TestMethod]
    public void PortableJavaStaysInsideInstanceAndSurvivesMovingIt()
    {
        var instance = Path.Combine(_root, "instance");
        var javaRoot = Path.Combine(instance, "jre");
        Directory.CreateDirectory(Path.Combine(javaRoot, "bin")); Directory.CreateDirectory(Path.Combine(javaRoot, "lib"));
        File.WriteAllText(Path.Combine(javaRoot, "bin", "java.exe"), "java bytes");
        JavaInstallation? Parse(string path) => new(Path.GetDirectoryName(path)!, new Version(21, 0, 1), JavaBrandType.OpenJDK, MachineType.AMD64, true, true);
        var java = InstanceJavaBundler.Find(instance, parse: Parse)!;
        Assert.AreEqual(Path.Combine("jre", "bin", "java.exe"), java.RelativeExePath);
        var stage = Path.Combine(_root, "stage", "runtime", "java");
        InstanceJavaBundler.Copy(java, instance, stage);
        Assert.AreEqual("java bytes", File.ReadAllText(Path.Combine(stage, "bin", "java.exe")));
        var preference = new UseInstanceRelativePath(@"runtime\java\bin\java.exe");
        var json = JsonSerializer.Serialize<JavaPreference>(preference);
        Assert.IsInstanceOfType<UseInstanceRelativePath>(JsonSerializer.Deserialize<JavaPreference>(json));
        var moved = Path.Combine(_root, "moved"); Directory.Move(Path.GetDirectoryName(Path.GetDirectoryName(stage)!)!, moved);
        Assert.IsTrue(File.Exists(PackExportContent.ResolveInstanceJava(moved, preference.RelativePath)));
        Assert.ThrowsExactly<InvalidDataException>(() => PackExportContent.ResolveInstanceJava(instance, "../outside/java.exe"));
        Assert.IsNull(InstanceJavaBundler.Find(Path.Combine(_root, "empty"), parse: Parse));
    }

    [TestMethod]
    public void ExplicitIsolationSurvivesEmptyClientOverridesPass()
    {
        var isolated = PackExportContent.ReadIsolation("False", "1");
        Assert.IsFalse(isolated);
        Assert.IsFalse(PackExportContent.ReadIsolation(isolated.ToString(), "2"));
        Assert.IsTrue(PackExportContent.ReadIsolation(null, null));
        Assert.IsFalse(PackExportContent.ReadIsolation(null, "2"));
    }

    [TestMethod]
    public void SharedIsolationPlacesGameFilesInEffectiveRootAndKeepsJavaInInstance()
    {
        var game = Path.Combine(_root, "minecraft"); var instance = Path.Combine(game, "versions", "Pack");
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        Directory.CreateDirectory(Path.Combine(instance, "runtime"));
        Directory.CreateDirectory(Path.Combine(instance, "PCL"));
        File.WriteAllText(Path.Combine(instance, "mods", "a.jar"), "mod bytes");
        File.WriteAllText(Path.Combine(instance, "runtime", "java.exe"), "runtime");
        File.WriteAllText(Path.Combine(instance, "PCL", "Setup.ini"), "isolation false");
        var paths = new[] { "mods/a.jar", "runtime/java.exe", "PCL/Setup.ini" };
        PackInstallationLayout.Apply(instance, game, paths, isolated: true);
        Assert.IsFalse(Directory.Exists(Path.Combine(game, "mods")));
        PackInstallationLayout.Apply(instance, game, paths, isolated: false);
        Assert.AreEqual("mod bytes", File.ReadAllText(Path.Combine(game, "mods", "a.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(instance, "runtime", "java.exe")));
        Assert.IsTrue(File.Exists(Path.Combine(instance, "PCL", "Setup.ini")));
    }

    [TestMethod]
    public void SharedIsolationDoesNotOverwriteDifferentPlayerFiles()
    {
        var game = Path.Combine(_root, "minecraft"); var instance = Path.Combine(game, "versions", "Pack");
        Directory.CreateDirectory(Path.Combine(instance, "mods")); Directory.CreateDirectory(Path.Combine(game, "mods"));
        File.WriteAllText(Path.Combine(instance, "mods", "a.jar"), "pack");
        File.WriteAllText(Path.Combine(game, "mods", "a.jar"), "player");
        Assert.ThrowsExactly<IOException>(() => PackInstallationLayout.Apply(instance, game, new[] { "mods/a.jar" }, false));
        Assert.AreEqual("pack", File.ReadAllText(Path.Combine(instance, "mods", "a.jar")));
        Assert.AreEqual("player", File.ReadAllText(Path.Combine(game, "mods", "a.jar")));
    }

    [TestMethod]
    public void OuterLauncherArchiveContainsOnlyTheChosenLauncherAndNestedMrpack()
    {
        var export = Path.Combine(_root, "export"); var pack = Path.Combine(export, "modpack");
        Directory.CreateDirectory(Path.Combine(pack, "overrides", "config"));
        File.WriteAllText(Path.Combine(pack, "modrinth.index.json"), "{\"formatVersion\":1,\"game\":\"minecraft\"}");
        File.WriteAllText(Path.Combine(pack, "overrides", "config", "pack.json"), "authored config");
        File.WriteAllText(Path.Combine(export, LightReleaseResolver.BundledFileName), "selected light executable");
        PackExportArchive.Create(pack, Path.Combine(export, "modpack.mrpack"));
        Directory.Delete(pack, true);
        var destination = Path.Combine(_root, "export.zip"); PackExportArchive.Create(export, destination);
        using var archive = ZipFile.OpenRead(destination);
        CollectionAssert.AreEquivalent(new[] { "PCL Light.exe", "modpack.mrpack" }, archive.Entries.Select(entry => entry.FullName).ToArray());
        using var nestedBytes = new MemoryStream();
        using (var stream = archive.GetEntry("modpack.mrpack")!.Open()) stream.CopyTo(nestedBytes);
        nestedBytes.Position = 0;
        using var nested = new ZipArchive(nestedBytes);
        using var reader = new StreamReader(nested.GetEntry("overrides/config/pack.json")!.Open());
        Assert.AreEqual("authored config", reader.ReadToEnd());
    }

    private static string Release(string? hash) => JsonSerializer.Serialize(new
    {
        tag_name = "v1.0.0", draft = false, prerelease = false,
        assets = new[] { "PCL-Light_1.0.0_x64.exe", "PCL-Light_1.0.0_ARM64.exe", "SHA256SUMS.txt" }.Select(name => new
        {
            name, digest = hash is null ? null : "sha256:" + hash,
            browser_download_url = $"https://github.com/{LightReleaseResolver.Repository}/releases/download/v1.0.0/{name}",
        }),
    });
}
