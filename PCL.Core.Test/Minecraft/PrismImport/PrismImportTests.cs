using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.PrismImport;

namespace PCL.Core.Test.Minecraft.PrismImport;

[TestClass]
public sealed class PrismImportTests
{
    private string _root = null!;
    private string _source = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLPrismTests", Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "prism"); Directory.CreateDirectory(Path.Combine(_source, ".minecraft"));
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"}");
        File.WriteAllText(Path.Combine(_source, "instance.cfg"), "name=Prism instance\nJavaPath=C:/private/java.exe\nOverrideJava=true\n");
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [TestMethod]
    public void ReaderPreservesAllFlagCombinationsUnknownFieldsAndDoesNotWriteIntoSource()
    {
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"}," + string.Join(",", Enumerable.Range(0, 8).Select(value =>
            "{\"uid\":\"unknown-" + value + "\",\"version\":\"1\",\"important\":" + Bool(value, 1)
            + ",\"dependencyOnly\":" + Bool(value, 2) + ",\"disabled\":" + Bool(value, 4) + ",\"future\":{\"keep\":true}}")));
        var before = File.ReadAllBytes(Path.Combine(_source, "mmc-pack.json"));
        var read = PrismInstanceReader.Read(_source);
        Assert.AreEqual(9, read.Components.Count);
        for (var value = 0; value < 8; value++)
        {
            var component = read.Components[value + 1];
            Assert.AreEqual((value & 1) != 0, component.Important);
            Assert.AreEqual((value & 2) != 0, component.DependencyOnly);
            Assert.AreEqual((value & 4) != 0, component.Disabled);
            Assert.IsNotNull(component.Original["future"]);
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(_source, "mmc-pack.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_source, "PCL")));
        Assert.IsTrue(read.Warnings.Any(warning => warning.Contains("JavaPath", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ActiveQuiltAndUnknownComponentsBlockInsteadOfSilentlyInstallingVanilla()
    {
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"org.quiltmc.quilt-loader\",\"version\":\"1\"}");
        Assert.IsFalse(PrismInstanceReader.Read(_source).CanImport);
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"org.quiltmc.quilt-loader\",\"version\":\"1\",\"disabled\":true}");
        var read = PrismInstanceReader.Read(_source);
        Assert.IsTrue(read.CanImport);
        Assert.IsNull(read.LoaderUid);
        Assert.AreEqual(2, read.Components.Count);
    }

    [TestMethod]
    public void PatchOrderAndUnknownFieldsArePreservedButUnmappedActiveOperationsBlock()
    {
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"net.fabricmc.fabric-loader\",\"version\":\"0.16.0\"}");
        Patch("a.json", "{\"uid\":\"net.fabricmc.fabric-loader\",\"order\":-100,\"+jvmArgs\":[\"last\"]}");
        Patch("z.json", "{\"uid\":\"net.minecraft\",\"order\":100,\"+jvmArgs\":[\"first\"]}");
        var read = PrismInstanceReader.Read(_source);
        Assert.IsTrue(read.CanImport);
        CollectionAssert.AreEqual(new[] { "z.json", "a.json" }, read.Patches.Select(patch => patch.FileName).ToArray());
        var installed = JsonNode.Parse("{\"libraries\":[],\"arguments\":{\"jvm\":[\"original\"],\"game\":[]}}")!.AsObject();
        var composed = PrismPatchComposer.Apply(installed, read);
        CollectionAssert.AreEqual(new[] { "original", "first", "last" }, composed["arguments"]!["jvm"]!.AsArray().Select(value => value!.ToString()).ToArray());
        Assert.AreEqual(1, installed["arguments"]!["jvm"]!.AsArray().Count);
        Patch("z.json", "{\"uid\":\"net.minecraft\",\"order\":100,\"-libraries\":[\"custom\"]}");
        read = PrismInstanceReader.Read(_source);
        Assert.IsFalse(read.CanImport);
        Assert.IsNotNull(read.Patches.First().Original["-libraries"]);
    }

    [TestMethod]
    public void DuplicateActiveComponentOrMultiplePatchesForOneUidAreRejected()
    {
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"net.fabricmc.fabric-loader\",\"version\":\"0.16.0\"},{\"uid\":\"net.fabricmc.fabric-loader\",\"version\":\"0.17.0\"}");
        Assert.IsFalse(PrismInstanceReader.Read(_source).CanImport);
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"}");
        Patch("a.json", "{\"uid\":\"net.minecraft\",\"order\":1}");
        Patch("b.json", "{\"uid\":\"net.minecraft\",\"order\":2}");
        Assert.IsFalse(PrismInstanceReader.Read(_source).CanImport);
    }

    [TestMethod]
    public void DisabledPatchDoesNotParticipateAndNoJavaSetIntersectionCannotRelaxEarlierConstraint()
    {
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"custom\",\"version\":\"1\",\"disabled\":true}");
        Patch("disabled.json", "{\"uid\":\"custom\",\"order\":5,\"unknownOp\":true}");
        Patch("java.json", "{\"uid\":\"net.minecraft\",\"order\":10,\"compatibleJavaMajors\":[8,17]}");
        var read = PrismInstanceReader.Read(_source); Assert.IsTrue(read.CanImport);
        Assert.ThrowsExactly<InvalidDataException>(() => PrismPatchComposer.Apply(
            JsonNode.Parse("{\"libraries\":[],\"compatibleJavaMajors\":[21]}")!.AsObject(), read));
    }

    [TestMethod]
    public void UntrustedVersionAndPatchPathsCannotEscapeExistingInstallerRoots()
    {
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"../escaped\"}");
        Assert.IsFalse(PrismInstanceReader.Read(_source).CanImport);
        Manifest("{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"}");
        Patch("bad.json", "{\"uid\":\"net.minecraft\",\"+libraries\":[{\"name\":\"bad:../../escape:1\"}]}");
        var read = PrismInstanceReader.Read(_source);
        Assert.ThrowsExactly<InvalidDataException>(() => PrismPatchComposer.Apply(JsonNode.Parse("{\"libraries\":[]}")!.AsObject(), read));
        Patch("bad.json", "{\"uid\":\"net.minecraft\",\"assetIndex\":{\"id\":\"../escape\"}}");
        read = PrismInstanceReader.Read(_source);
        Assert.ThrowsExactly<InvalidDataException>(() => PrismPatchComposer.Apply(JsonNode.Parse("{\"libraries\":[]}")!.AsObject(), read));
    }

    [TestMethod]
    public void ContentCopyIncludesActualGameFilesAndDisabledModsButExcludesCredentialsAndLauncherExecutables()
    {
        var game = Path.Combine(_source, ".minecraft");
        Directory.CreateDirectory(Path.Combine(game, "mods")); File.WriteAllText(Path.Combine(game, "mods", "mod.jar.disabled"), "mod");
        File.WriteAllText(Path.Combine(game, "options.txt"), "user-options");
        File.WriteAllText(Path.Combine(game, "accounts.json"), "secret"); File.WriteAllText(Path.Combine(game, "launcher.exe"), "binary");
        Directory.CreateDirectory(Path.Combine(game, "PCL", "Enhancements"));
        File.WriteAllText(Path.Combine(game, "PCL", "Enhancements", "instance.json"), "old-identity");
        var read = PrismInstanceReader.Read(_source); var stage = Path.Combine(_root, "stage"); Directory.CreateDirectory(stage);
        var copied = PrismImportContent.Copy(read, stage);
        Assert.AreEqual(2, copied.Files.Count);
        Assert.AreEqual("mod", File.ReadAllText(Path.Combine(stage, "mods", "mod.jar.disabled")));
        Assert.IsFalse(File.Exists(Path.Combine(stage, "mods", "mod.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(stage, "accounts.json")));
        Assert.IsFalse(File.Exists(Path.Combine(stage, "launcher.exe")));
        Assert.IsFalse(Directory.Exists(Path.Combine(stage, "PCL")));
        Assert.AreEqual("secret", File.ReadAllText(Path.Combine(game, "accounts.json")));
        Assert.IsTrue(copied.Skipped.Count >= 3);
    }

    [TestMethod]
    public void SourceMetadataAndCopiedContentChangesAreDetected()
    {
        var read = PrismInstanceReader.Read(_source);
        Assert.IsTrue(PrismInstanceReader.InputsUnchanged(read));
        File.AppendAllText(Path.Combine(_source, "instance.cfg"), "name=changed\n");
        Assert.IsFalse(PrismInstanceReader.InputsUnchanged(read));
        read = PrismInstanceReader.Read(_source);
        File.WriteAllText(Path.Combine(read.GameDirectory, "options.txt"), "old");
        var stage = Path.Combine(_root, "stage"); Directory.CreateDirectory(stage);
        var copied = PrismImportContent.Copy(read, stage);
        File.WriteAllText(Path.Combine(read.GameDirectory, "options.txt"), "new");
        Assert.ThrowsExactly<IOException>(() => PrismImportContent.VerifySource(copied.Files));
    }

    [TestMethod]
    public void AddedGameFileDuringInstallationInvalidatesTheCopiedSnapshot()
    {
        var read = PrismInstanceReader.Read(_source);
        File.WriteAllText(Path.Combine(read.GameDirectory, "options.txt"), "existing");
        var stage = Path.Combine(_root, "stage"); Directory.CreateDirectory(stage);
        var copied = PrismImportContent.Copy(read, stage);
        PrismImportContent.VerifySource(read, copied);
        Directory.CreateDirectory(Path.Combine(read.GameDirectory, "config"));
        File.WriteAllText(Path.Combine(read.GameDirectory, "config", "new-user-file.txt"), "new-user-content");
        Assert.ThrowsExactly<IOException>(() => PrismImportContent.VerifySource(read, copied));
        Assert.AreEqual("new-user-content", File.ReadAllText(Path.Combine(read.GameDirectory, "config", "new-user-file.txt")));
    }

    [TestMethod]
    public async Task TransactionCommitsOnlyNewTargetAndCancellationLeavesSourceAndExistingTargetAlone()
    {
        var read = PrismInstanceReader.Read(_source); var target = Path.Combine(_root, "imported");
        await PrismImportTransaction.ExecuteAsync(read, target, (_, _) => Task.CompletedTask,
            (stage, _) =>
            {
                File.WriteAllText(Path.Combine(stage.Directory, stage.Name + ".json"), "{\"id\":\"stage\"}");
                File.WriteAllText(Path.Combine(stage.Directory, stage.Name + ".jar"), "jar"); return Task.CompletedTask;
            });
        Assert.IsTrue(File.Exists(Path.Combine(target, "imported.json")));
        Assert.IsTrue(File.Exists(Path.Combine(target, "imported.jar")));
        Assert.IsTrue(PrismInstanceReader.InputsUnchanged(read));
        await Assert.ThrowsExactlyAsync<IOException>(() => PrismImportTransaction.ExecuteAsync(read, target,
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask));
        var other = Path.Combine(_root, "cancelled"); using var cancel = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => PrismImportTransaction.ExecuteAsync(read, other,
            (_, _) => { cancel.Cancel(); return Task.CompletedTask; },
            (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancel.Token));
        Assert.IsFalse(Directory.Exists(other));
        Assert.AreEqual(0, Directory.GetDirectories(_root, ".pcl-prism-import-*").Length);
        Assert.IsTrue(PrismInstanceReader.InputsUnchanged(read));
    }

    [TestMethod]
    public void LinkedPrismGameDirectoryIsRejectedWithoutFollowingIt()
    {
        var external = Path.Combine(_root, "external"); Directory.CreateDirectory(external);
        Directory.Delete(Path.Combine(_source, ".minecraft"));
        try { Directory.CreateSymbolicLink(Path.Combine(_source, ".minecraft"), external); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { Assert.Inconclusive("Link creation unavailable: " + error.Message); return; }
        Assert.ThrowsExactly<InvalidDataException>(() => PrismInstanceReader.Read(_source));
    }

    private void Manifest(string components) => File.WriteAllText(Path.Combine(_source, "mmc-pack.json"),
        "{\"formatVersion\":1,\"components\":[" + components + "],\"unknownManifestField\":{\"keep\":true}}");
    private void Patch(string name, string json)
    { Directory.CreateDirectory(Path.Combine(_source, "patches")); File.WriteAllText(Path.Combine(_source, "patches", name), json); }
    private static string Bool(int value, int mask) => (value & mask) != 0 ? "true" : "false";
}
