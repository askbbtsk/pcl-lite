using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Enhancements;

namespace PCL.Core.Test.Minecraft.Enhancements;

[TestClass]
public sealed class InstanceEnhancementTests
{
    private string _root = null!;
    private string _instance = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLEnhancementTests", Guid.NewGuid().ToString("N"));
        _instance = Path.Combine(_root, "versions", "test");
        Directory.CreateDirectory(_instance);
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [TestMethod]
    public void ConstructionAndMissingReadsDoNotCreateSidecars()
    {
        var store = new InstanceEnhancementStore(_instance);
        Assert.AreEqual(EnhancementReadStatus.Missing, store.ReadDeclaration().Status);
        Assert.AreEqual(EnhancementReadStatus.Missing, store.ReadResolved().Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(_instance, "PCL")));
    }

    [TestMethod]
    public void AllFlagCombinationsAndUnknownFieldsRoundTripWithoutDroppingDisabledComponents()
    {
        var store = new InstanceEnhancementStore(_instance);
        var components = Enumerable.Range(0, 8).Select(value => new ComponentDescriptor
        {
            Id = "component-" + value, Version = "1", Important = (value & 1) != 0,
            DependencyOnly = (value & 2) != 0, Disabled = (value & 4) != 0,
        }).ToList();
        var identity = Guid.NewGuid();
        store.SaveDeclaration(new() { InstanceId = identity, Components = components });
        var loaded = store.ReadDeclaration();
        Assert.AreEqual(EnhancementReadStatus.Valid, loaded.Status);
        Assert.AreEqual(identity, loaded.Value!.InstanceId);
        CollectionAssert.AreEqual(components.ToArray(), loaded.Value.Components.ToArray());
        var text = File.ReadAllText(store.DeclarationPath).Replace("\"schemaVersion\": 1", "\"customField\": {\"keep\":true}, \"schemaVersion\": 1");
        File.WriteAllText(store.DeclarationPath, text);
        loaded = store.ReadDeclaration();
        store.SaveDeclaration(loaded.Value!, loaded.ContentSha256);
        Assert.IsTrue(File.ReadAllText(store.DeclarationPath).Contains("customField", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DeclarationWritesRejectLostUpdatesAndDoNotOverwriteInvalidOrNewerSchema()
    {
        var store = new InstanceEnhancementStore(_instance);
        store.SaveDeclaration(new());
        var loaded = store.ReadDeclaration();
        File.WriteAllText(store.DeclarationPath, "{\"schemaVersion\":99,\"components\":[]}");
        var newer = File.ReadAllText(store.DeclarationPath);
        Assert.AreEqual(EnhancementReadStatus.UnsupportedSchema, store.ReadDeclaration().Status);
        Assert.ThrowsExactly<IOException>(() => store.SaveDeclaration(new(), loaded.ContentSha256));
        Assert.ThrowsExactly<IOException>(() => store.SaveDeclaration(new(), store.ReadDeclaration().ContentSha256));
        Assert.AreEqual(newer, File.ReadAllText(store.DeclarationPath));
        File.WriteAllText(store.DeclarationPath, "broken");
        Assert.AreEqual(EnhancementReadStatus.Invalid, store.ReadDeclaration().Status);
        Assert.ThrowsExactly<IOException>(() => store.SaveDeclaration(new()));
        Assert.AreEqual("broken", File.ReadAllText(store.DeclarationPath));
    }

    [TestMethod]
    public void RegenerationReplacesCorruptDerivedReportButPreservesUserDeclarationAndUnknownSchema()
    {
        var store = new InstanceEnhancementStore(_instance);
        store.SaveDeclaration(new() { Components = [new() { Id = "disabled", Disabled = true }] });
        var declaration = File.ReadAllBytes(store.DeclarationPath);
        File.WriteAllText(store.ResolvedPath, "bad-cache");
        var report = InstanceEnhancementAnalyzer.Analyze(Input());
        Assert.IsTrue(store.TrySaveResolved(report, out var error), error);
        Assert.AreEqual(report.Fingerprint, store.ReadResolved().Value!.Fingerprint);
        CollectionAssert.AreEqual(declaration, File.ReadAllBytes(store.DeclarationPath));
        File.WriteAllText(store.ResolvedPath, "{\"schemaVersion\":99}");
        Assert.IsFalse(store.TrySaveResolved(report, out error));
        Assert.AreEqual("{\"schemaVersion\":99}", File.ReadAllText(store.ResolvedPath));
        Assert.IsTrue(report.InputsStable); // The fresh in-memory result remains usable.
    }

    [TestMethod]
    public void CacheFailureAndUnstableInputsDoNotRequireWritingGameFiles()
    {
        var store = new InstanceEnhancementStore(_instance);
        Directory.CreateDirectory(Path.Combine(_instance, "PCL"));
        File.WriteAllText(store.DirectoryPath, "occupied-by-user");
        var report = InstanceEnhancementAnalyzer.Analyze(Input());
        Assert.IsFalse(store.TrySaveResolved(report, out _));
        Assert.AreEqual("occupied-by-user", File.ReadAllText(store.DirectoryPath));
        Assert.IsFalse(store.TrySaveResolved(report with { InputsStable = false }, out _));
    }

    [TestMethod]
    public void HealthCheckDistinguishesMissingDamagedUnverifiedAndVerifiedWithoutRepair()
    {
        var libraries = Path.Combine(_root, "libraries");
        Directory.CreateDirectory(libraries);
        var valid = Path.Combine(libraries, "valid.jar");
        var corrupt = Path.Combine(libraries, "corrupt.jar");
        var unknown = Path.Combine(libraries, "unknown.jar");
        File.WriteAllText(valid, "valid"); File.WriteAllText(corrupt, "changed"); File.WriteAllText(unknown, "local");
        var input = Input() with
        {
            AllowedLibraryRoots = [libraries],
            Libraries = [new(valid, "net.fabricmc:fabric-loader:0.16.0", Sha256: Hash("valid")),
                new(corrupt, Sha256: Hash("expected")), new(unknown), new(Path.Combine(libraries, "missing.jar"))],
        };
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        var report = InstanceEnhancementAnalyzer.Analyze(input);
        CollectionAssert.AreEqual(new[] { LibraryHealthStatus.Valid, LibraryHealthStatus.Damaged,
            LibraryHealthStatus.Unverified, LibraryHealthStatus.Missing }, report.Libraries.Select(entry => entry.Status).ToArray());
        Assert.AreEqual("0.16.0", report.Components.Single(component => component.Id == "net.fabricmc.fabric-loader").Version);
        Assert.AreEqual("changed", File.ReadAllText(corrupt));
        CollectionAssert.AreEquivalent(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(Path.Combine(_instance, "PCL")));
    }

    [TestMethod]
    public void CurrentDiskStateIsRecheckedEvenWhenResolutionFingerprintDidNotChange()
    {
        var file = Path.Combine(_instance, "library.jar");
        var input = Input() with { Libraries = [new(file, Sha256: Hash("correct"))] };
        var before = InstanceEnhancementAnalyzer.Analyze(input);
        File.WriteAllText(file, "correct");
        var after = InstanceEnhancementAnalyzer.Analyze(input);
        Assert.AreEqual(before.Fingerprint, after.Fingerprint);
        Assert.AreEqual(LibraryHealthStatus.Missing, before.Libraries[0].Status);
        Assert.AreEqual(LibraryHealthStatus.Valid, after.Libraries[0].Status);
    }

    [TestMethod]
    public void FingerprintIncludesSourceContentsOrderJavaArchitectureAndParserAdapter()
    {
        var file = Path.Combine(_instance, "test.json");
        File.WriteAllText(file, "original");
        var input = Input() with { SourceFiles = [file] };
        var original = InstanceEnhancementAnalyzer.CalculateFingerprint(input);
        File.WriteAllText(file, "modified");
        Assert.AreNotEqual(original, InstanceEnhancementAnalyzer.CalculateFingerprint(input));
        var changed = InstanceEnhancementAnalyzer.CalculateFingerprint(input);
        foreach (var alternative in new[]
        {
            input with { Java = new(21, "x64", "java.exe", "21.0.7") },
            input with { Architecture = "ARM64" }, input with { AdapterVersion = "2" },
            input with { EffectiveVersionJson = "{\"libraries\":[],\"id\":\"different\"}" },
            input with { SourceMetadataSha256 = Hash("new-meta") },
        }) Assert.AreNotEqual(changed, InstanceEnhancementAnalyzer.CalculateFingerprint(alternative));
    }

    [TestMethod]
    public void UnsafeLibraryAndCancelledAnalysisNeverReadOrWriteOutsideApprovedRoots()
    {
        var outside = Path.Combine(_root, "outside.jar");
        File.WriteAllText(outside, "private");
        var result = InstanceEnhancementAnalyzer.Analyze(Input() with { Libraries = [new(outside)] });
        Assert.AreEqual(LibraryHealthStatus.UnsafePath, result.Libraries[0].Status);
        Assert.IsNull(result.Libraries[0].ActualSha256);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => InstanceEnhancementAnalyzer.Analyze(Input(), cancel.Token));
        var store = new InstanceEnhancementStore(_instance);
        Assert.ThrowsExactly<OperationCanceledException>(() => store.SaveDeclaration(new(), cancellationToken: cancel.Token));
        Assert.IsFalse(Directory.Exists(store.DirectoryPath));
    }

    [TestMethod]
    public void ReportContainsOrderedSourceAndRuntimeEvidenceWithoutRawSecretsOrExternalPaths()
    {
        var first = Path.Combine(_instance, "base.json");
        var second = Path.Combine(_instance, "PCL", "patch.json");
        var external = Path.Combine(_root, "external-user", "shared.json");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        Directory.CreateDirectory(Path.GetDirectoryName(external)!);
        File.WriteAllText(first, "base metadata"); File.WriteAllText(second, "patch metadata"); File.WriteAllText(external, "external metadata");
        var javaPath = Path.Combine(_root, "private-runtime", "bin", "java.exe");
        const string effective = "{\"id\":\"test\",\"arguments\":{\"game\":[\"--accessToken\",\"private-token-value\"]}}";
        var input = Input() with
        {
            EffectiveVersionJson = effective, SourceFiles = [second, first, external],
            Platform = "windows", Architecture = "ARM64", AdapterVersion = "adapter-3",
            Java = new(21, "ARM64", javaPath, "21.0.7"), SourceMetadataSha256 = Hash("upstream metadata"),
        };
        var report = InstanceEnhancementAnalyzer.Analyze(input);
        Assert.AreEqual(InstanceEnhancementAnalyzer.CalculateFingerprint(input), report.Fingerprint);
        Assert.AreEqual(Hash(effective), report.EffectiveVersionSha256);
        Assert.AreEqual(input.MinecraftVersion, report.MinecraftVersion);
        Assert.AreEqual("windows", report.Platform); Assert.AreEqual("ARM64", report.Architecture);
        Assert.AreEqual("adapter-3", report.AdapterVersion); Assert.AreEqual(input.SourceMetadataSha256, report.SourceMetadataSha256);
        Assert.AreEqual(21, report.Java!.MajorVersion); Assert.AreEqual("ARM64", report.Java.Architecture);
        Assert.AreEqual("21.0.7", report.Java.FullVersion); Assert.AreEqual(Hash(javaPath), report.Java.RuntimeIdentitySha256);
        Assert.AreEqual("PCL/patch.json", report.SourceEvidence[0].RelativePath);
        Assert.AreEqual(Hash("patch metadata"), report.SourceEvidence[0].ContentSha256);
        Assert.AreEqual("base.json", report.SourceEvidence[1].RelativePath);
        Assert.IsFalse(report.SourceEvidence[1].IsExternal);
        Assert.IsTrue(report.SourceEvidence[2].IsExternal); Assert.IsNull(report.SourceEvidence[2].RelativePath);
        Assert.AreEqual("shared.json", report.SourceEvidence[2].FileName);
        Assert.AreEqual(Hash(Path.TrimEndingDirectorySeparator(Path.GetFullPath(external)).ToUpperInvariant()),
            report.SourceEvidence[2].PathIdentitySha256);
        var serialized = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.IsFalse(serialized.Contains("private-token-value", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("private-runtime", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("external-user", StringComparison.Ordinal));
        var reversed = InstanceEnhancementAnalyzer.Analyze(input with { SourceFiles = [first, second, external] });
        Assert.AreNotEqual(report.Fingerprint, reversed.Fingerprint);
        Assert.AreEqual("base.json", reversed.SourceEvidence[0].RelativePath);
        File.WriteAllText(first, "user edited base");
        var changed = InstanceEnhancementAnalyzer.Analyze(input);
        Assert.AreNotEqual(report.Fingerprint, changed.Fingerprint);
        Assert.AreEqual(Hash("user edited base"), changed.SourceEvidence[1].ContentSha256);
    }

    [TestMethod]
    public void LibraryReportPreservesDeclaredChecksAndLocalAuthoritySeparatelyFromObservedBytes()
    {
        var file = Path.Combine(_instance, "observed.jar");
        File.WriteAllText(file, "observed bytes");
        var declaredSha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("expected bytes")));
        var declaredSha256 = Hash("expected bytes");
        var input = Input() with { Libraries = [new(file, "test:library:1", declaredSha1, 14, declaredSha256, IsLocal: true)] };
        var report = InstanceEnhancementAnalyzer.Analyze(input);
        Assert.AreEqual(declaredSha1, report.Libraries[0].DeclaredSha1);
        Assert.AreEqual(declaredSha256, report.Libraries[0].DeclaredSha256);
        Assert.AreEqual(14L, report.Libraries[0].DeclaredSize);
        Assert.IsTrue(report.Libraries[0].IsLocal);
        Assert.AreEqual(Hash("observed bytes"), report.Libraries[0].ActualSha256);
        Assert.AreEqual(LibraryHealthStatus.Damaged, report.Libraries[0].Status);
        var missing = InstanceEnhancementAnalyzer.Analyze(input with
        { Libraries = [new(Path.Combine(_instance, "missing.jar"), Sha256: declaredSha256, Size: 14)] });
        Assert.AreEqual(declaredSha256, missing.Libraries[0].DeclaredSha256);
        Assert.AreEqual(LibraryHealthStatus.Missing, missing.Libraries[0].Status);
        Assert.IsNull(missing.Libraries[0].ActualSha256);
    }

    private EnhancementAnalysisInput Input() => new()
    {
        InstanceDirectory = _instance, MinecraftVersion = "1.21.1", EffectiveVersionJson = "{\"id\":\"test\",\"libraries\":[]}",
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
