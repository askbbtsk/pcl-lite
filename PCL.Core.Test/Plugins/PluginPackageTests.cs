using System;
using System.IO.Compression;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Plugins;
using PCL.Mixin;
using PCL.Plugin.Dependency.TestSupport;
using PCL.Plugin.SDK;

namespace PCL.Core.Test.Plugins;

[TestClass]
public sealed class PluginPackageTests
{
    [TestMethod]
    public void PackageSignatureValidatesAndDetectsChangedPayload()
    {
        using var signingKey = RSA.Create(3072);
        var certificateRequest = new CertificateRequest("CN=PCL Plugin Test", signingKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
        var files = CreateFiles();
        var signature = PluginPackage.SignFiles(files, certificate);

        var valid = PluginPackage.VerifyFiles(files, signature);
        Assert.IsTrue(valid.Valid, valid.Error);
        Assert.AreEqual(certificate.Thumbprint, valid.Thumbprint);
        Assert.IsNotNull(valid.Signer);

        var replacementRequest = new CertificateRequest("CN=Replacement signer", signingKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var replacementCertificate = replacementRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
        var replacedSigner = signature with
        {
            Certificate = Convert.ToBase64String(replacementCertificate.Export(X509ContentType.Cert)),
            CertificateThumbprint = replacementCertificate.Thumbprint
        };
        Assert.IsFalse(PluginPackage.VerifyFiles(files, replacedSigner).Valid,
            "The package signature must bind the exact certificate used to sign it.");

        files["Plugin.dll"] = [9, 8, 7, 6];
        var changed = PluginPackage.VerifyFiles(files, signature);
        Assert.IsFalse(changed.Valid);
    }

    [TestMethod]
    public void PackageSignatureRejectsRsaKeysBelow2048Bits()
    {
        using var signingKey = RSA.Create(1024);
        var request = new CertificateRequest("CN=Weak test signer", signingKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
        var files = CreateFiles();
        Assert.ThrowsExactly<ArgumentException>(() => PluginPackage.SignFiles(files, certificate));

        var signedAt = DateTimeOffset.UtcNow;
        var certificateBytes = certificate.Export(X509ContentType.Cert);
        using var payload = new MemoryStream();
        using (var writer = new StreamWriter(payload, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write("signatureSchema\0");
            writer.Write("1\n");
            writer.Write("signatureAlgorithm\0RSA-SHA256-PKCS1\n");
            writer.Write("certificateThumbprint\0");
            writer.Write(certificate.Thumbprint);
            writer.Write("\ncertificateSha256\0");
            writer.Write(Convert.ToHexString(SHA256.HashData(certificateBytes)).ToLowerInvariant());
            writer.Write('\n');
            writer.Flush();
        }
        payload.Write(PluginPackage.CreateSignaturePayload(files, signedAt));
        var weakSignature = new PluginPackageSignature(1, "RSA-SHA256-PKCS1",
            Convert.ToBase64String(certificateBytes),
            Convert.ToBase64String(signingKey.SignData(payload.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            certificate.Thumbprint, signedAt);

        var verification = PluginPackage.VerifyFiles(files, weakSignature);
        Assert.IsFalse(verification.Valid);
        StringAssert.Contains(verification.Error!, "2048-bit");
    }

    [TestMethod]
    public async Task InspectReportsSignerTimeAndUnsignedPackages()
    {
        using var certificate = CreateCertificate();
        var files = CreateFiles();
        var signature = PluginPackage.SignFiles(files, certificate);
        var signedPath = CreateTempPath();
        var unsignedPath = CreateTempPath();
        try
        {
            await WritePackageAsync(signedPath, files, signature);
            var signed = await PluginPackage.InspectAsync(signedPath);
            Assert.IsTrue(signed.IsValid, signed.Error);
            Assert.IsTrue(signed.IsSigned);
            Assert.AreEqual(certificate.Thumbprint, signed.SignerThumbprint);
            Assert.AreEqual(signature.SignedAtUtc, signed.SignedAtUtc);

            await WritePackageAsync(unsignedPath, files, (PluginPackageSignature?)null);
            var unsigned = await PluginPackage.InspectAsync(unsignedPath);
            Assert.IsTrue(unsigned.IsValid, unsigned.Error);
            Assert.IsFalse(unsigned.IsSigned);
            Assert.IsNull(unsigned.SignedAtUtc);
        }
        finally
        {
            DeleteIfExists(signedPath);
            DeleteIfExists(unsignedPath);
        }
    }

    [TestMethod]
    public async Task StreamingInspectionAndExtractionVerifyLegacySignaturePayload()
    {
        using var certificate = CreateCertificate();
        var files = CreateFiles();
        var signedAt = DateTimeOffset.UtcNow;
        var certificateBytes = certificate.Export(X509ContentType.Cert);
        var hashes = CreateCanonicalFileHashes(files);
        using var legacyPayload = new MemoryStream();
        using (var writer = new StreamWriter(legacyPayload, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write("signatureSchema\0");
            writer.Write("1\n");
            writer.Write("signatureAlgorithm\0RSA-SHA256-PKCS1\n");
            writer.Write("certificateThumbprint\0");
            writer.Write(certificate.Thumbprint);
            writer.Write('\n');
            writer.Write("certificateSha256\0");
            writer.Write(Convert.ToHexString(SHA256.HashData(certificateBytes)).ToLowerInvariant());
            writer.Write('\n');
            writer.Flush();
        }
        legacyPayload.Write(PluginPackage.CreateSignaturePayload(files, signedAt));
        var legacySignature = new PluginPackageSignature(1, "RSA-SHA256-PKCS1",
            Convert.ToBase64String(certificateBytes),
            Convert.ToBase64String(certificate.GetRSAPrivateKey()!.SignData(legacyPayload.ToArray(),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            certificate.Thumbprint, signedAt);
        var legacyVerification = PluginPackage.VerifyFileHashes(hashes, legacySignature);
        Assert.IsTrue(legacyVerification.Valid, legacyVerification.Error);

        var packagePath = CreateTempPath();
        var extractionPath = Path.Combine(Path.GetTempPath(), "pcl-plugin-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WritePackageAsync(packagePath, files, legacySignature);
            var inspect = await PluginPackage.InspectAsync(packagePath);
            Assert.IsTrue(inspect.IsValid, inspect.Error);
            Assert.IsTrue(inspect.SignatureValid);
            Assert.AreEqual(PluginPackage.CreateFileContentHash(files), inspect.ContentHash);

            var packageSha256 = await ComputeFileSha256Async(packagePath);
            var extracted = await PluginPackage.ExtractVerifiedAsync(packagePath, extractionPath,
                inspect.ContentHash, packageSha256);
            Assert.IsTrue(extracted.IsValid, extracted.Error);
            CollectionAssert.AreEqual(files["Plugin.dll"], await File.ReadAllBytesAsync(Path.Combine(extractionPath, "Plugin.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(extractionPath, PluginPackage.SignatureFileName)));

            var extractedHashes = CreateCanonicalFileHashes(files);
            Assert.IsTrue(PluginPackage.VerifyFileHashes(extractedHashes, legacySignature).Valid);
        }
        finally
        {
            DeleteIfExists(packagePath);
            if (Directory.Exists(extractionPath)) Directory.Delete(extractionPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task StreamingAuthoringHandlesLargeAssetAndFailedExtractionLeavesNoDirectory()
    {
        using var certificate = CreateCertificate();
        var root = Path.Combine(Path.GetTempPath(), "pcl-plugin-large-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "large" + PluginPackage.Extension);
        var extraction = Path.Combine(root, "extraction");
        Directory.CreateDirectory(source);
        var manifest = new PluginManifest
        {
            Id = "large.probe",
            Name = "Large Probe",
            Version = "1.0.0",
            Author = "Runtime test",
            EntryAssembly = "Plugin.dll"
        };
        await File.WriteAllBytesAsync(Path.Combine(source, PluginPackage.ManifestFileName),
            JsonSerializer.SerializeToUtf8Bytes(manifest, PluginPackage.SerializerOptions));
        await File.WriteAllBytesAsync(Path.Combine(source, "Plugin.dll"), [1, 2, 3]);
        const long largeAssetLength = 40L * 1024 * 1024;
        var buffer = new byte[64 * 1024];
        new Random(42721).NextBytes(buffer);
        await using (var large = new FileStream(Path.Combine(source, "large.dat"), FileMode.CreateNew,
                         FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            for (long written = 0; written < largeAssetLength; written += buffer.Length)
                await large.WriteAsync(buffer);
        }

        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            await PluginAuthoring.CreatePackageAsync(source, output, certificate);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Assert.IsTrue(allocated < 16L * 1024 * 1024,
                $"Streaming package authoring allocated {allocated:N0} bytes for a {largeAssetLength:N0}-byte asset.");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var inspect = await PluginPackage.InspectAsync(output);
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Assert.IsTrue(allocated < 16L * 1024 * 1024,
                $"Streaming inspection allocated {allocated:N0} bytes for a {largeAssetLength:N0}-byte asset.");
            Assert.IsTrue(inspect.IsValid, inspect.Error);
            Assert.IsTrue(inspect.SignatureValid);
            Assert.AreEqual(largeAssetLength, new FileInfo(Path.Combine(source, "large.dat")).Length);

            var packageSha256 = await ComputeFileSha256Async(output);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var extractedLarge = await PluginPackage.ExtractVerifiedAsync(output, extraction,
                inspect.ContentHash, packageSha256);
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            Assert.IsTrue(allocated < 16L * 1024 * 1024,
                $"Streaming extraction allocated {allocated:N0} bytes for a {largeAssetLength:N0}-byte asset.");
            Assert.IsTrue(extractedLarge.IsValid, extractedLarge.Error);
            Assert.AreEqual(largeAssetLength, new FileInfo(Path.Combine(extraction, "large.dat")).Length);
            var directoryInspect = await PluginPackage.InspectDirectoryAsync(extraction);
            Assert.IsTrue(directoryInspect.IsValid, directoryInspect.Error);
            Assert.AreEqual(inspect.ContentHash, directoryInspect.ContentHash);
            Directory.Delete(extraction, recursive: true);

            var files = await PluginPackage.ReadPackageFilesAsync(output);
            var payload = files.Where(item => !item.Key.Equals(PluginPackage.SignatureFileName, StringComparison.Ordinal))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            var signature = JsonSerializer.Deserialize<PluginPackageSignature>(files[PluginPackage.SignatureFileName],
                PluginPackage.SerializerOptions)!;
            Assert.IsTrue(PluginPackage.VerifyFiles(payload, signature).Valid,
                "A streaming-authored package must pass the legacy dictionary verifier.");

            var tampered = output + ".tampered";
            try
            {
                await using (var stream = File.Create(tampered))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var item in payload)
                    {
                        var entry = archive.CreateEntry(item.Key);
                        await using var entryStream = entry.Open();
                        if (item.Key == "Plugin.dll") await entryStream.WriteAsync(new byte[] { 9, 9, 9 });
                        else await entryStream.WriteAsync(item.Value);
                    }
                    var signatureEntry = archive.CreateEntry(PluginPackage.SignatureFileName);
                    await using var signatureStream = signatureEntry.Open();
                    await JsonSerializer.SerializeAsync(signatureStream, signature, PluginPackage.SerializerOptions);
                }
                var tamperedHash = await ComputeFileSha256Async(tampered);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PluginPackage.ExtractVerifiedAsync(
                    tampered, extraction, inspect.ContentHash, tamperedHash));
                Assert.IsFalse(Directory.Exists(extraction), "Failed verification must remove the partial extraction.");
            }
            finally { DeleteIfExists(tampered); }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InspectRejectsTamperingAndUnsafeArchivePaths()
    {
        using var certificate = CreateCertificate();
        var files = CreateFiles();
        var signature = PluginPackage.SignFiles(files, certificate);
        var tamperedPath = CreateTempPath();
        var traversalPath = CreateTempPath();
        var duplicatePath = CreateTempPath();
        try
        {
            var modified = new Dictionary<string, byte[]>(files, StringComparer.Ordinal)
            {
                ["Plugin.dll"] = [1, 3, 3, 7]
            };
            await WritePackageAsync(tamperedPath, modified, signature);
            var tampered = await PluginPackage.InspectAsync(tamperedPath);
            Assert.IsFalse(tampered.IsValid);
            Assert.IsFalse(tampered.SignatureValid);

            await using (var stream = File.Create(traversalPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../outside.txt");
                await using var content = entry.Open();
                await content.WriteAsync(new byte[] { 1 });
            }
            var traversal = await PluginPackage.InspectAsync(traversalPath);
            Assert.IsFalse(traversal.IsValid);
            Assert.IsNotNull(traversal.Error);

            await using (var stream = File.Create(duplicatePath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var name in new[] { PluginPackage.ManifestFileName, PluginPackage.ManifestFileName })
                {
                    var entry = archive.CreateEntry(name);
                    await using var content = entry.Open();
                    await content.WriteAsync(files[PluginPackage.ManifestFileName]);
                }
            }
            var duplicate = await PluginPackage.InspectAsync(duplicatePath);
            Assert.IsFalse(duplicate.IsValid);
            Assert.IsNotNull(duplicate.Error);
        }
        finally
        {
            DeleteIfExists(tamperedPath);
            DeleteIfExists(traversalPath);
            DeleteIfExists(duplicatePath);
        }
    }

    [TestMethod]
    public void ManifestRejectsMissingEntryAndInvalidIdentifiers()
    {
        var manifest = new PluginManifest
        {
            Id = "Bad/Plugin",
            Name = "Test plugin",
            Version = "1.0.0",
            Author = "Test",
            EntryAssembly = "../Plugin.dll"
        };

        var error = PluginPackage.ValidateManifest(manifest, ["plugin.json", "Plugin.dll"]);

        Assert.IsNotNull(error);
        Assert.ThrowsExactly<ArgumentException>(() =>
            PluginPackage.ValidateRelativePath(new string('a', PluginPackage.MaximumRelativePathLength + 1)));
    }

    [TestMethod]
    public void ExperimentalFeatureManifestRequiresUniqueSignedMixinConfigurationGroups()
    {
        var files = CreateFiles();
        var featureConfig = JsonSerializer.SerializeToUtf8Bytes(new MixinConfiguration
        {
            Mixins = ["Test.ExperimentalMixin"]
        }, PluginPackage.SerializerOptions);
        files["mixins/experimental.json"] = featureConfig;
        var manifest = PluginPackage.DeserializeManifest(files[PluginPackage.ManifestFileName]) with
        {
            ExperimentalFeatures =
            [
                new PluginExperimentalFeature
                {
                    Id = "experimental.ui",
                    Name = "Experimental UI",
                    Description = "An independently selectable patch group.",
                    MixinConfigs = ["mixins/experimental.json"]
                }
            ]
        };
        files[PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(manifest,
            PluginPackage.SerializerOptions);

        Assert.IsNull(PluginPackage.ValidateManifest(manifest, files.Keys));
        Assert.IsNull(PluginPackage.ValidateMixinConfigurations(manifest, files));
        using var certificate = CreateCertificate();
        var signed = PluginPackage.SignFiles(files, certificate);
        Assert.IsTrue(PluginPackage.VerifyFiles(files, signed).Valid);

        var overlapsRequired = manifest with { MixinConfigs = ["mixins/experimental.json"] };
        Assert.IsNotNull(PluginPackage.ValidateManifest(overlapsRequired, files.Keys));
        var duplicateId = manifest with
        {
            ExperimentalFeatures = manifest.ExperimentalFeatures.Append(
                manifest.ExperimentalFeatures[0] with { Name = "Duplicate feature" }).ToArray()
        };
        Assert.IsNotNull(PluginPackage.ValidateManifest(duplicateId, files.Keys));
        var missingConfig = manifest with
        {
            ExperimentalFeatures =
            [manifest.ExperimentalFeatures[0] with { MixinConfigs = ["mixins/missing.json"] }]
        };
        Assert.IsNotNull(PluginPackage.ValidateManifest(missingConfig, files.Keys));
    }

    [TestMethod]
    public async Task ManifestAndMixinJsonAreBoundedAndNullSafe()
    {
        var files = CreateFiles();
        files[PluginPackage.ManifestFileName] = new byte[PluginPackage.MaximumManifestBytes + 1];
        var oversizedManifestPath = CreateTempPath();
        try
        {
            await WritePackageAsync(oversizedManifestPath, files, (PluginPackageSignature?)null);
            var oversized = await PluginPackage.InspectAsync(oversizedManifestPath);
            Assert.IsFalse(oversized.IsValid);
            StringAssert.Contains(oversized.Error!, "64 KiB");
        }
        finally { DeleteIfExists(oversizedManifestPath); }

        var manifest = new PluginManifest
        {
            Id = "test.plugin",
            Name = "Test plugin",
            Version = "1.0.0",
            Author = new string('a', 129),
            EntryAssembly = "Plugin.dll"
        };
        Assert.IsNotNull(PluginPackage.ValidateManifest(manifest, ["Plugin.dll"]));
        var invalidDependencyManifest = manifest with
        {
            Author = "Test",
            Dependencies = new(StringComparer.Ordinal) { ["test.plugin"] = "*" }
        };
        Assert.IsNotNull(PluginPackage.ValidateManifest(invalidDependencyManifest, ["Plugin.dll"]));
        Assert.IsFalse(PluginVersionRange.IsValid("^1.02.0"));

        foreach (var json in new[]
                 {
                     "{\"package\":\"test\",\"mixins\":null}",
                     "{\"package\":null,\"mixins\":[\"Test.Mixin\"]}",
                     "{\"package\":\"test\",\"mixins\":[\"Test.Mixin\"],\"injectors\":null}"
                 })
        {
            files = CreateFiles();
            var mixinManifest = JsonSerializer.Deserialize<PluginManifest>(files[PluginPackage.ManifestFileName],
                PluginPackage.SerializerOptions)! with { MixinConfigs = ["mixins.json"] };
            files[PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(mixinManifest,
                PluginPackage.SerializerOptions);
            files["mixins.json"] = Encoding.UTF8.GetBytes(json);
            var error = PluginPackage.ValidateMixinConfigurations(mixinManifest, files);
            Assert.IsNotNull(error, $"Expected invalid mixin config to be rejected: {json}");
        }

        files = CreateFiles();
        var path = CreateTempPath();
        try
        {
            var malformedManifest = Encoding.UTF8.GetString(files[PluginPackage.ManifestFileName])
                .Replace("\"mixinConfigs\": []", "\"mixinConfigs\": null", StringComparison.Ordinal);
            files[PluginPackage.ManifestFileName] = Encoding.UTF8.GetBytes(malformedManifest);
            await WritePackageAsync(path, files, (PluginPackageSignature?)null);
            var check = await PluginPackage.InspectAsync(path);
            Assert.IsFalse(check.IsValid);
            Assert.IsNotNull(check.Error);
        }
        finally { DeleteIfExists(path); }
    }

    [TestMethod]
    public async Task InstallRejectsOversizedPackageBeforeSnapshotAndCleansCanceledSnapshot()
    {
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-copy-test-" + Guid.NewGuid().ToString("N"));
        var oversizedPath = CreateTempPath();
        var smallPath = CreateTempPath();
        try
        {
            await using (var oversized = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write))
                oversized.SetLength(PluginPackage.MaximumPackageBytes + 1);
            await using var manager = new PluginRuntimeManager(pluginsDirectory);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                manager.InstallAsync(oversizedPath, (_, _) => Task.FromResult(true)));
            Assert.IsFalse(Directory.Exists(Path.Combine(pluginsDirectory, ".package-snapshot")) &&
                           Directory.EnumerateFiles(Path.Combine(pluginsDirectory, ".package-snapshot")).Any());

            await File.WriteAllBytesAsync(smallPath, [1, 2, 3]);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                manager.InstallAsync(smallPath, (_, _) => Task.FromResult(true), canceled.Token));
            Assert.IsFalse(Directory.Exists(Path.Combine(pluginsDirectory, ".package-snapshot")) &&
                           Directory.EnumerateFiles(Path.Combine(pluginsDirectory, ".package-snapshot")).Any());
        }
        finally
        {
            DeleteIfExists(oversizedPath);
            DeleteIfExists(smallPath);
            if (Directory.Exists(pluginsDirectory)) Directory.Delete(pluginsDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task UpdateConfirmationShowsSignerRotationAndUnsignedDowngrade()
    {
        using var originalSigner = CreateCertificate();
        using var replacementSigner = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-signer-transition-" + Guid.NewGuid().ToString("N"));
        var originalPath = CreateTempPath();
        var replacementPath = CreateTempPath();
        var unsignedPath = CreateTempPath();
        try
        {
            var files = CreateFiles();
            await WritePackageAsync(originalPath, files, originalSigner);
            await WritePackageAsync(replacementPath, files, replacementSigner);
            await WritePackageAsync(unsignedPath, files, (PluginPackageSignature?)null);

            await using var manager = new PluginRuntimeManager(pluginsDirectory);
            await manager.InstallAsync(originalPath, (_, _) => Task.FromResult(true));

            PluginPackageCheck? rotationPrompt = null;
            await Assert.ThrowsAsync<OperationCanceledException>(() => manager.InstallAsync(replacementPath,
                (check, _) =>
                {
                    rotationPrompt = check;
                    return Task.FromResult(false);
                }));
            Assert.IsNotNull(rotationPrompt);
            Assert.IsTrue(rotationPrompt.SignerChanged);
            Assert.AreEqual(originalSigner.Thumbprint, rotationPrompt.PreviousSignerThumbprint);
            Assert.AreEqual(replacementSigner.Thumbprint, rotationPrompt.SignerThumbprint);

            PluginPackageCheck? downgradePrompt = null;
            await Assert.ThrowsAsync<OperationCanceledException>(() => manager.InstallAsync(unsignedPath,
                (check, _) =>
                {
                    downgradePrompt = check;
                    return Task.FromResult(false);
                }));
            Assert.IsNotNull(downgradePrompt);
            Assert.IsTrue(downgradePrompt.SignerChanged);
            Assert.AreEqual(originalSigner.Thumbprint, downgradePrompt.PreviousSignerThumbprint);
            Assert.IsFalse(downgradePrompt.IsSigned);
        }
        finally
        {
            DeleteIfExists(originalPath);
            DeleteIfExists(replacementPath);
            DeleteIfExists(unsignedPath);
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    [TestMethod]
    public async Task LocalInstallRecordsProvenanceAndDeclinedDowngradeKeepsInstalledPackage()
    {
        using var certificate = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-market-state-" + Guid.NewGuid().ToString("N"));
        var originalPath = CreateTempPath();
        var downgradePath = CreateTempPath();
        try
        {
            var originalFiles = CreateFiles();
            await WritePackageAsync(originalPath, originalFiles, certificate);
            var downgradeFiles = CreateFiles();
            var downgradeManifest = PluginPackage.DeserializeManifest(downgradeFiles[PluginPackage.ManifestFileName]);
            downgradeFiles[PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(
                downgradeManifest with { Version = "0.9.0" }, PluginPackage.SerializerOptions);
            await WritePackageAsync(downgradePath, downgradeFiles, certificate);

            await using var manager = new PluginRuntimeManager(pluginsDirectory);
            PluginPackageCheck? initialCheck = null;
            await manager.InstallAsync(originalPath, (check, _) =>
            {
                initialCheck = check;
                return Task.FromResult(true);
            });
            Assert.IsNotNull(initialCheck);
            Assert.AreEqual("local", initialCheck.CurrentInstallSourceType);
            Assert.IsNotNull(initialCheck.PackageSha256);
            Assert.IsNotNull(initialCheck.Signer);
            Assert.IsNotNull(initialCheck.SignerThumbprint);
            Assert.IsNotNull(initialCheck.SignedAtUtc);
            var installedBefore = manager.GetInstalled().Single();
            Assert.AreEqual("local", installedBefore.InstallSourceType);
            Assert.AreEqual(initialCheck.PackageSha256, installedBefore.InstalledPackageSha256);
            Assert.IsNotNull(installedBefore.InstalledAtUtc);
            Assert.AreEqual(initialCheck.Signer, installedBefore.Signer);
            Assert.AreEqual(initialCheck.SignerThumbprint, installedBefore.SignerThumbprint);
            Assert.AreEqual(initialCheck.SignedAtUtc, installedBefore.SignedAtUtc);

            PluginPackageCheck? downgradePrompt = null;
            await Assert.ThrowsAsync<OperationCanceledException>(() => manager.InstallAsync(downgradePath,
                (check, _) =>
                {
                    downgradePrompt = check;
                    return Task.FromResult(false);
                }));
            Assert.IsNotNull(downgradePrompt);
            Assert.IsTrue(downgradePrompt.IsDowngrade);
            Assert.AreEqual("1.0.0", downgradePrompt.PreviousVersion);
            Assert.IsFalse(downgradePrompt.PackageChanged);
            Assert.AreEqual("1.0.0", manager.GetInstalled().Single().Manifest.Version);
        }
        finally
        {
            DeleteIfExists(originalPath);
            DeleteIfExists(downgradePath);
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    [TestMethod]
    public async Task MarketCandidatesDistinguishCurrentUpgradeDowngradeAndSourceOrPackageChanges()
    {
        using var certificate = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-market-candidates-" + Guid.NewGuid().ToString("N"));
        var packagePath = CreateTempPath();
        var catalogUrl = "https://market.example/plugins/index.json";
        try
        {
            await WritePackageAsync(packagePath, CreateFiles(), certificate);
            await using (var installer = new PluginRuntimeManager(pluginsDirectory))
                await installer.InstallAsync(packagePath, (_, _) => Task.FromResult(true));

            Assert.IsFalse(File.Exists(Path.Combine(pluginsDirectory, ".plugin-state", "test.plugin.json")),
                "Install metadata must not replace the state for the on-disk plugin before restart.");
            Assert.IsTrue(File.Exists(Path.Combine(pluginsDirectory, ".pending-install", ".state-test.plugin.json")));

            await using (var pendingApplier = new PluginRuntimeManager(pluginsDirectory)) { }
            Assert.IsTrue(File.Exists(Path.Combine(pluginsDirectory, ".plugin-state", "test.plugin.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(pluginsDirectory, ".pending-install")));

            var statePath = Path.Combine(pluginsDirectory, ".plugin-state", "test.plugin.json");
            await using var packageStream = File.OpenRead(packagePath);
            var packageHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
            var stateJson = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
            stateJson["InstalledVersion"] = "1.0.0";
            stateJson["InstallSourceType"] = "market";
            stateJson["InstallSourceUrl"] = catalogUrl;
            stateJson["InstallPackageUrl"] = "https://github.com/example/plugins/releases/download/v1.0.0/test.pclplugin";
            stateJson["InstalledPackageSha256"] = packageHash;
            stateJson["InstalledAtUtc"] = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(statePath, stateJson.ToJsonString());

            await using var manager = new PluginRuntimeManager(pluginsDirectory);
            await manager.PrepareEnabledAsync();
            var installed = manager.GetInstalled().Single();
            Assert.AreEqual("market", installed.InstallSourceType);
            Assert.AreEqual(catalogUrl, installed.InstallSourceUrl);
            Assert.AreEqual(packageHash, installed.InstalledPackageSha256);
            var packageCheck = await PluginPackage.InspectAsync(packagePath);
            Assert.AreEqual(packageCheck.Signer, installed.Signer);
            Assert.AreEqual(packageCheck.SignerThumbprint, installed.SignerThumbprint);
            Assert.AreEqual(packageCheck.SignedAtUtc, installed.SignedAtUtc);
            Assert.IsNotNull(installed.InstalledAtUtc);

            var current = new PluginMarketEntry("test.plugin", "Test plugin", "1.0.0", "Test signer", "",
                "https://github.com/example/plugins/releases/download/v1.0.0/test.pclplugin",
                Sha256: packageHash) { SourceUrl = catalogUrl };
            Assert.AreEqual(PluginMarketUpdateKind.Current,
                manager.GetMarketUpdateCandidates([current]).Single().Kind);
            Assert.AreEqual(PluginMarketUpdateKind.UpdateAvailable,
                manager.GetMarketUpdateCandidates([current with { Version = "1.1.0" }]).Single().Kind);
            Assert.AreEqual(PluginMarketUpdateKind.DowngradeAvailable,
                manager.GetMarketUpdateCandidates([current with { Version = "0.9.0" }]).Single().Kind);
            Assert.AreEqual(PluginMarketUpdateKind.UpdateAvailable,
                manager.GetMarketUpdateCandidates([current with { SourceUrl = "https://other.example/index.json" }]).Single().Kind);
            Assert.AreEqual(PluginMarketUpdateKind.UpdateAvailable,
                manager.GetMarketUpdateCandidates([current with { Sha256 = new string('a', 64) }]).Single().Kind);
        }
        finally
        {
            DeleteIfExists(packagePath);
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    [TestMethod]
    public async Task ExperimentalFeatureIsOffByDefaultAndPersistsUserChoiceAcrossRestart()
    {
        using var certificate = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-feature-toggle-" + Guid.NewGuid().ToString("N"));
        var packagePath = CreateTempPath();
        try
        {
            await WritePackageAsync(packagePath, CreateFeatureRuntimeFiles("runtime.feature.toggle",
                includeStableConfig: false, includeFailingConfig: false), certificate);
            await using (var installer = new PluginRuntimeManager(pluginsDirectory))
            {
                await installer.InstallAsync(packagePath, (_, _) => Task.FromResult(true));
                var feature = installer.GetInstalled().Single().ExperimentalFeatures.Single();
                Assert.IsFalse(feature.Enabled, "New experimental Mixin features must default to off.");
            }

            await using (var runtime = new PluginRuntimeManager(pluginsDirectory))
            {
                await runtime.LoadEnabledAsync();
                Assert.AreEqual("1", await ReadFeatureProbeValueAsync(pluginsDirectory, "runtime.feature.toggle",
                    "experimental.txt"));
                await runtime.SetExperimentalFeatureEnabledAsync("runtime.feature.toggle", "experimental.patch", true);
                Assert.IsTrue(runtime.GetInstalled().Single().ExperimentalFeatures.Single().Enabled);
                Assert.IsTrue(runtime.GetInstalled().Single().PendingRestart);
            }

            await using (var runtime = new PluginRuntimeManager(pluginsDirectory))
            {
                await runtime.LoadEnabledAsync();
                Assert.AreEqual("2", await ReadFeatureProbeValueAsync(pluginsDirectory, "runtime.feature.toggle",
                    "experimental.txt"));
                await runtime.SetExperimentalFeatureEnabledAsync("runtime.feature.toggle", "experimental.patch", false);
            }

            await using (var runtime = new PluginRuntimeManager(pluginsDirectory))
            {
                await runtime.LoadEnabledAsync();
                Assert.AreEqual("1", await ReadFeatureProbeValueAsync(pluginsDirectory, "runtime.feature.toggle",
                    "experimental.txt"));
            }
        }
        finally
        {
            PluginPageRegistry.RemovePlugin("runtime.feature.toggle");
            DeleteIfExists(packagePath);
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    [TestMethod]
    public async Task FailedExperimentalFeatureRollsBackOnlyItsMixinGroupsAndKeepsPluginRunning()
    {
        using var certificate = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-feature-failure-" + Guid.NewGuid().ToString("N"));
        var packagePath = CreateTempPath();
        try
        {
            await WritePackageAsync(packagePath, CreateFeatureRuntimeFiles("runtime.feature.failure",
                includeStableConfig: true, includeFailingConfig: true), certificate);
            await using (var installer = new PluginRuntimeManager(pluginsDirectory))
                await installer.InstallAsync(packagePath, (_, _) => Task.FromResult(true));

            await using (var runtime = new PluginRuntimeManager(pluginsDirectory))
            {
                await runtime.LoadEnabledAsync();
                await runtime.SetExperimentalFeatureEnabledAsync("runtime.feature.failure", "experimental.patch", true);
            }

            await using (var runtime = new PluginRuntimeManager(pluginsDirectory))
            {
                await runtime.LoadEnabledAsync();
                var installed = runtime.GetInstalled().Single();
                Assert.IsTrue(installed.Running, installed.Error);
                Assert.AreEqual("9", await ReadFeatureProbeValueAsync(pluginsDirectory, "runtime.feature.failure",
                    "stable.txt"), "The ordinary plugin Mixin must remain applied.");
                Assert.AreEqual("1", await ReadFeatureProbeValueAsync(pluginsDirectory, "runtime.feature.failure",
                    "experimental.txt"), "Earlier configurations from the failed feature group must be rolled back.");
                var feature = installed.ExperimentalFeatures.Single();
                Assert.IsFalse(feature.Enabled, "A failed experimental feature must persist as disabled.");
                Assert.IsNotNull(feature.Error);
                Assert.IsTrue(File.Exists(Path.Combine(pluginsDirectory, ".plugin-data", "runtime.feature.failure", "started.txt")),
                    "An experimental feature failure must not stop the plugin entrypoint.");
            }
        }
        finally
        {
            PluginPageRegistry.RemovePlugin("runtime.feature.failure");
            DeleteIfExists(packagePath);
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    [TestMethod]
    public async Task IncompatibleCoreRangeIsShownAtInstallAndSkippedAtStartup()
    {
        using var certificate = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-core-range-" + Guid.NewGuid().ToString("N"));
        var packagePath = CreateTempPath();
        try
        {
            var files = CreateRuntimeFiles("runtime.core.range", typeof(GoodRuntimePlugin).FullName!);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(files[PluginPackage.ManifestFileName],
                PluginPackage.SerializerOptions)! with { PclCoreVersion = ">=999.0.0" };
            files[PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(manifest,
                PluginPackage.SerializerOptions);
            await WritePackageAsync(packagePath, files, certificate);

            PluginPackageCheck? prompt = null;
            await using (var installer = new PluginRuntimeManager(pluginsDirectory))
            {
                await installer.InstallAsync(packagePath, (check, _) =>
                {
                    prompt = check;
                    return Task.FromResult(true);
                });
            }
            Assert.IsNotNull(prompt?.CompatibilityWarning);

            await using var runtime = new PluginRuntimeManager(pluginsDirectory);
            await runtime.LoadEnabledAsync();
            Assert.IsFalse(File.Exists(Path.Combine(pluginsDirectory, ".plugin-data", "runtime.core.range", "started.txt")));
            Assert.IsFalse(runtime.GetInstalled().Any(plugin => plugin.Manifest.Id == "runtime.core.range" && plugin.Running));
        }
        finally
        {
            DeleteIfExists(packagePath);
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    [TestMethod]
    public async Task MarketInstallCorroboratesGitHubDigestAndReleaseTime()
    {
        using var certificate = CreateCertificate();
        var files = CreateFiles();
        var signature = PluginPackage.SignFiles(files, certificate);
        var packagePath = CreateTempPath();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-install-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WritePackageAsync(packagePath, files, signature);
            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            var assetUrl = "https://github.com/example/plugins/releases/download/v1.0.0/test.plugin.pclplugin";
            var publishedAt = DateTimeOffset.UtcNow;
            var releaseDigest = "sha256:" + packageHash;
            var releaseJsonOverride = (string?)null;
            var downgradeAssetRedirect = false;
            using var client = new HttpClient(new TestHttpMessageHandler(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                {
                    var releaseJson = releaseJsonOverride ?? JsonSerializer.Serialize(new
                    {
                        tag_name = "v1.0.0",
                        published_at = publishedAt,
                        assets = new[] { new { name = "test.plugin.pclplugin", digest = releaseDigest } }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                    };
                }
                var packageResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packageBytes) };
                if (downgradeAssetRedirect)
                    packageResponse.RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                        "http://attacker.example/plugin.pclplugin");
                return packageResponse;
            }));
            await using var manager = new PluginRuntimeManager(pluginsDirectory, client);
            var entry = new PluginMarketEntry("test.plugin", "Test plugin", "1.0.0", "Test signer", "",
                assetUrl, Sha256: packageHash);

            downgradeAssetRedirect = true;
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                manager.InstallFromMarketAsync(entry, (_, _) => Task.FromResult(true)));
            downgradeAssetRedirect = false;

            releaseJsonOverride = new string(' ', 2 * 1024 * 1024 + 1);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                manager.InstallFromMarketAsync(entry, (_, _) => Task.FromResult(true)));
            releaseJsonOverride = null;

            releaseDigest = "sha256:" + new string('0', 64);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                manager.InstallFromMarketAsync(entry, (_, _) => Task.FromResult(true)));
            Assert.IsFalse(Directory.Exists(Path.Combine(pluginsDirectory, ".pending-install")));

            releaseDigest = "sha256:" + packageHash;
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                manager.InstallFromMarketAsync(entry, (_, _) => Task.FromResult(false)));
            Assert.IsFalse(Directory.Exists(Path.Combine(pluginsDirectory, ".pending-install")));

            await manager.InstallAsync(packagePath, (_, _) => Task.FromResult(true));
            PluginPackageCheck? sourceTransition = null;
            var sourcedEntry = entry with { SourceUrl = "https://market.example/plugins/index.json" };
            var result = await manager.InstallFromMarketAsync(sourcedEntry, (check, _) =>
            {
                sourceTransition = check;
                return Task.FromResult(true);
            });

            Assert.IsTrue(result.IsSigned);
            Assert.IsNotNull(sourceTransition);
            Assert.IsTrue(sourceTransition.SourceChanged);
            Assert.AreEqual("local", sourceTransition.PreviousInstallSourceType);
            Assert.AreEqual("market", sourceTransition.CurrentInstallSourceType);
            Assert.AreEqual(sourcedEntry.SourceUrl, sourceTransition.CurrentInstallSourceUrl);
            Assert.IsTrue(result.SignatureValid);
            Assert.IsNotNull(result.GitHubProvenance);
            Assert.AreEqual("example/plugins", result.GitHubProvenance.Repository);
            Assert.AreEqual("v1.0.0", result.GitHubProvenance.Tag);
            Assert.AreEqual(packageHash, result.GitHubProvenance.Sha256);
            Assert.AreEqual(publishedAt, result.GitHubProvenance.PublishedAtUtc);
            Assert.IsFalse(result.GitHubProvenance.SignatureTimeAfterPublication);

            publishedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            var conflictingTime = await manager.InstallFromMarketAsync(entry, (_, _) => Task.FromResult(true));
            Assert.IsTrue(conflictingTime.SignatureValid);
            Assert.IsTrue(conflictingTime.GitHubProvenance!.SignatureTimeAfterPublication);
        }
        finally
        {
            DeleteIfExists(packagePath);
            if (Directory.Exists(pluginsDirectory)) Directory.Delete(pluginsDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task MarketIndexBoundsResponseSizeEntryCountMetadataAndRedirectScheme()
    {
        var indexJson = string.Empty;
        var downgradeRedirect = false;
        using var client = new HttpClient(new TestHttpMessageHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(indexJson, Encoding.UTF8, "application/json")
            };
            if (downgradeRedirect)
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get,
                    "http://attacker.example/plugins/index.json");
            return response;
        }));
        await using var manager = new PluginRuntimeManager(Path.Combine(Path.GetTempPath(), "pcl-market-bounds-" + Guid.NewGuid().ToString("N")), client);
        var indexUri = new Uri("https://market.example/plugins/index.json");

        indexJson = new string(' ', 2 * 1024 * 1024 + 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => manager.GetMarketAsync(indexUri));

        indexJson = JsonSerializer.Serialize(new PluginMarketIndex(1,
            Enumerable.Range(0, 1001).Select(index => new PluginMarketEntry(
                $"market.plugin{index}", "Plugin", "1.0.0", "Author", "",
                "https://github.com/example/plugins/releases/download/v1.0.0/plugin.pclplugin",
                Sha256: new string('a', 64))).ToArray()));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => manager.GetMarketAsync(indexUri));

        indexJson = JsonSerializer.Serialize(new PluginMarketIndex(1,
            [new PluginMarketEntry("market.plugin", new string('n', 129), "1.0.0", "Author", "",
                "https://github.com/example/plugins/releases/download/v1.0.0/plugin.pclplugin",
                Sha256: new string('a', 64))]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => manager.GetMarketAsync(indexUri));

        indexJson = JsonSerializer.Serialize(new PluginMarketIndex(1,
            [new PluginMarketEntry("market.plugin", "Plugin", "1.0.0", "Author", "",
                "https://github.com/example/plugins/releases/download/v1.0.0/plugin.pclplugin",
                Sha256: new string('a', 64))]));
        downgradeRedirect = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => manager.GetMarketAsync(indexUri));
    }

    [TestMethod]
    public async Task MarketIndexRecordsResolvedSourceAndRequiresSemVerVersions()
    {
        var indexJson = string.Empty;
        using var client = new HttpClient(new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(indexJson, Encoding.UTF8, "application/json")
        }));
        await using var manager = new PluginRuntimeManager(
            Path.Combine(Path.GetTempPath(), "pcl-plugin-market-source-" + Guid.NewGuid().ToString("N")), client);
        var indexUri = new Uri("https://market.example/catalog/plugins.json");
        var entry = new PluginMarketEntry("market.plugin", "Plugin", "1.2.3", "Author", "",
            "https://github.com/example/plugins/releases/download/v1.2.3/plugin.pclplugin",
            Sha256: new string('a', 64));
        indexJson = JsonSerializer.Serialize(new PluginMarketIndex(1, [entry]));
        var index = await manager.GetMarketAsync(indexUri);
        Assert.AreEqual(indexUri.AbsoluteUri, index.Plugins.Single().SourceUrl);

        indexJson = JsonSerializer.Serialize(new PluginMarketIndex(1, [entry with { Version = "v1.2.3" }]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => manager.GetMarketAsync(indexUri));
    }

    [TestMethod]
    public async Task RuntimeIsolatesStartupFailureAndPluginStorage()
    {
        using var certificate = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-runtime-test-" + Guid.NewGuid().ToString("N"));
        var packages = new List<string>();
        try
        {
            await InstallRuntimePackagesAsync(pluginsDirectory, certificate, packages);
            AssertRuntimeLoadsPlugins(pluginsDirectory);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            foreach (var packagePath in packages) DeleteIfExists(packagePath);
            PluginPageRegistry.RemovePlugin("runtime.good.one");
            PluginPageRegistry.RemovePlugin("runtime.good.two");
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    [TestMethod]
    public async Task RuntimeStartsCompiledPluginsInDependencyOrder()
    {
        using var certificate = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-dependency-runtime-" + Guid.NewGuid().ToString("N"));
        var packagePaths = new List<string>();
        try
        {
            await using (var installer = new PluginRuntimeManager(pluginsDirectory))
            {
                foreach (var (id, type, dependencies) in new[]
                         {
                             ("zeta.provider", typeof(DependencyProviderPlugin).FullName!,
                                 (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()),
                             ("alpha.consumer", typeof(DependencyConsumerPlugin).FullName!,
                                 (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                                     { ["zeta.provider"] = "^1.0.0" })
                         })
                {
                    var packagePath = CreateTempPath();
                    packagePaths.Add(packagePath);
                    var files = id == "zeta.provider"
                        ? CreateDependencyProviderFiles(id, type)
                        : CreateRuntimeFiles(id, type, dependencies);
                    await WritePackageAsync(packagePath, files, certificate);
                    await installer.InstallAsync(packagePath, (_, _) => Task.FromResult(true));
                }
            }

            await using var runtime = new PluginRuntimeManager(pluginsDirectory);
            await runtime.LoadEnabledAsync();
            var installed = runtime.GetInstalled().ToDictionary(plugin => plugin.Manifest.Id);
            Assert.IsTrue(installed["zeta.provider"].Running, installed["zeta.provider"].Error);
            Assert.IsTrue(installed["alpha.consumer"].Running, installed["alpha.consumer"].Error);
            Assert.IsTrue(File.Exists(Path.Combine(pluginsDirectory, ".plugin-data", "alpha.consumer", "started.txt")));
        }
        finally
        {
            foreach (var packagePath in packagePaths) DeleteIfExists(packagePath);
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    [TestMethod]
    public async Task RuntimeBlocksDependentsWhenProviderLifecycleFails()
    {
        using var certificate = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-dependency-failure-" + Guid.NewGuid().ToString("N"));
        var packagePaths = new List<string>();
        try
        {
            await using (var installer = new PluginRuntimeManager(pluginsDirectory))
            {
                foreach (var (id, type, dependencies) in new[]
                         {
                             ("zeta.provider", typeof(FailingRuntimePlugin).FullName!,
                                 (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()),
                             ("alpha.consumer", typeof(DependencyConsumerPlugin).FullName!,
                                 (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                                     { ["zeta.provider"] = "^1.0.0" })
                         })
                {
                    var packagePath = CreateTempPath();
                    packagePaths.Add(packagePath);
                    await WritePackageAsync(packagePath, CreateRuntimeFiles(id, type, dependencies), certificate);
                    await installer.InstallAsync(packagePath, (_, _) => Task.FromResult(true));
                }
            }

            await using var runtime = new PluginRuntimeManager(pluginsDirectory);
            await runtime.LoadEnabledAsync();
            var installed = runtime.GetInstalled().ToDictionary(plugin => plugin.Manifest.Id);
            Assert.IsFalse(installed["zeta.provider"].Running);
            Assert.IsFalse(installed["alpha.consumer"].Running);
            StringAssert.Contains(installed["alpha.consumer"].Error!, "dependency failed to start");
            Assert.IsFalse(File.Exists(Path.Combine(pluginsDirectory, ".plugin-data", "alpha.consumer", "started.txt")));
        }
        finally
        {
            foreach (var packagePath in packagePaths) DeleteIfExists(packagePath);
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    [TestMethod]
    public async Task RuntimeSkipsInstalledPluginWithOversizedSignatureMetadata()
    {
        using var certificate = CreateCertificate();
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-plugin-signature-limit-" + Guid.NewGuid().ToString("N"));
        var packagePath = CreateTempPath();
        try
        {
            await WritePackageAsync(packagePath,
                CreateRuntimeFiles("runtime.signature.limit", typeof(GoodRuntimePlugin).FullName!), certificate);
            await using (var installer = new PluginRuntimeManager(pluginsDirectory))
                await installer.InstallAsync(packagePath, (_, _) => Task.FromResult(true));

            await using var runtime = new PluginRuntimeManager(pluginsDirectory);
            var signaturePath = Path.Combine(pluginsDirectory, "runtime.signature.limit", PluginPackage.SignatureFileName);
            Assert.IsTrue(File.Exists(signaturePath));
            await File.WriteAllBytesAsync(signaturePath, new byte[PluginPackage.MaximumSignatureBytes + 1]);
            await runtime.LoadEnabledAsync();

            Assert.AreEqual(0, runtime.GetInstalled().Count);
            Assert.IsFalse(File.Exists(Path.Combine(pluginsDirectory, ".plugin-data", "runtime.signature.limit", "started.txt")));
        }
        finally
        {
            DeleteIfExists(packagePath);
            DeleteRuntimeDirectory(pluginsDirectory);
        }
    }

    private static async Task InstallRuntimePackagesAsync(string pluginsDirectory, X509Certificate2 certificate,
        ICollection<string> packages)
    {
        await using (var installer = new PluginRuntimeManager(pluginsDirectory))
        {
            foreach (var (id, entryType) in new[]
                     {
                         ("runtime.good.one", typeof(GoodRuntimePlugin).FullName!),
                         ("runtime.bad", typeof(FailingRuntimePlugin).FullName!),
                         ("runtime.good.two", typeof(GoodRuntimePlugin).FullName!)
                     })
            {
                var packagePath = CreateTempPath();
                packages.Add(packagePath);
                await WritePackageAsync(packagePath, CreateRuntimeFiles(id, entryType), certificate);
                await installer.InstallAsync(packagePath, (_, _) => Task.FromResult(true));
            }
        }
    }

    private static void AssertRuntimeLoadsPlugins(string pluginsDirectory)
    {
        var runtime = new PluginRuntimeManager(pluginsDirectory);
        try
        {
            runtime.LoadEnabledAsync().GetAwaiter().GetResult();
            var installed = runtime.GetInstalled().ToDictionary(plugin => plugin.Manifest.Id, StringComparer.Ordinal);

            Assert.IsTrue(installed["runtime.good.one"].Running);
            Assert.IsFalse(installed["runtime.bad"].Running);
            Assert.IsNotNull(installed["runtime.bad"].Error);
            Assert.IsTrue(installed["runtime.good.two"].Running);
            Assert.IsTrue(File.Exists(Path.Combine(pluginsDirectory, ".plugin-data", "runtime.good.one", "started.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(pluginsDirectory, ".plugin-data", "runtime.good.two", "started.txt")));
            Assert.AreEqual(2, PluginPageRegistry.GetAll().Count(page => page.PluginId.StartsWith("runtime.good.", StringComparison.Ordinal)));
        }
        finally
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        const string password = "test-only-password";
        var pfx = PluginAuthoring.CreateSigningCertificate("CN=PCL Plugin Test", password);
        return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static Dictionary<string, byte[]> CreateFiles()
    {
        var manifest = new PluginManifest
        {
            Id = "test.plugin",
            Name = "Test plugin",
            Version = "1.0.0",
            Author = "Test signer",
            EntryAssembly = "Plugin.dll",
            EntryType = "Test.Plugin"
        };
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(manifest, PluginPackage.SerializerOptions),
            ["Plugin.dll"] = [0, 1, 2, 3]
        };
    }

    private static Dictionary<string, byte[]> CreateRuntimeFiles(string id, string entryType,
        IReadOnlyDictionary<string, string>? dependencies = null)
    {
        var manifest = new PluginManifest
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            Author = "Runtime test",
            EntryAssembly = "PCL.Core.Test.dll",
            EntryType = entryType,
            Dependencies = dependencies?.ToDictionary(item => item.Key, item => item.Value) ?? new(StringComparer.Ordinal)
        };
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(manifest, PluginPackage.SerializerOptions),
            [manifest.EntryAssembly] = File.ReadAllBytes(typeof(PluginPackageTests).Assembly.Location)
        };
    }

    private static Dictionary<string, byte[]> CreateFeatureRuntimeFiles(string id, bool includeStableConfig,
        bool includeFailingConfig)
    {
        var files = CreateRuntimeFiles(id, typeof(ExperimentalFeatureProbePlugin).FullName!);
        var featurePaths = new List<string> { "mixins/experimental.json" };
        if (includeFailingConfig) featurePaths.Add("mixins/experimental-failure.json");
        var manifest = PluginPackage.DeserializeManifest(files[PluginPackage.ManifestFileName]) with
        {
            MixinConfigs = includeStableConfig ? ["mixins/stable.json"] : [],
            ExperimentalFeatures =
            [
                new PluginExperimentalFeature
                {
                    Id = "experimental.patch",
                    Name = "Experimental patch",
                    Description = "A separately controlled compiled Mixin group.",
                    MixinConfigs = featurePaths
                }
            ]
        };
        files[PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(manifest,
            PluginPackage.SerializerOptions);
        files["mixins/experimental.json"] = JsonSerializer.SerializeToUtf8Bytes(new MixinConfiguration
        {
            Mixins = [typeof(ExperimentalFeatureMixin).FullName!]
        }, PluginPackage.SerializerOptions);
        if (includeFailingConfig)
            files["mixins/experimental-failure.json"] = JsonSerializer.SerializeToUtf8Bytes(new MixinConfiguration
            {
                Mixins = ["Missing.Feature.Mixin"]
            }, PluginPackage.SerializerOptions);
        if (includeStableConfig)
            files["mixins/stable.json"] = JsonSerializer.SerializeToUtf8Bytes(new MixinConfiguration
            {
                Mixins = [typeof(StableFeatureMixin).FullName!]
            }, PluginPackage.SerializerOptions);
        return files;
    }

    private static Task<string> ReadFeatureProbeValueAsync(string pluginsDirectory, string pluginId, string fileName) =>
        File.ReadAllTextAsync(Path.Combine(pluginsDirectory, ".plugin-data", pluginId, fileName));

    private static Dictionary<string, byte[]> CreateDependencyProviderFiles(string id, string entryType)
    {
        var assemblyPath = typeof(DependencyProviderPlugin).Assembly.Location;
        var manifest = new PluginManifest
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            Author = "Runtime test",
            EntryAssembly = Path.GetFileName(assemblyPath),
            EntryType = entryType
        };
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(manifest, PluginPackage.SerializerOptions),
            [manifest.EntryAssembly] = File.ReadAllBytes(assemblyPath)
        };
    }

    private static async Task WritePackageAsync(string path, IReadOnlyDictionary<string, byte[]> files,
        PluginPackageSignature? signature)
    {
        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, contents) in files)
        {
            var entry = archive.CreateEntry(name);
            await using var output = entry.Open();
            await output.WriteAsync(contents);
        }
        if (signature is null) return;
        var signatureEntry = archive.CreateEntry(PluginPackage.SignatureFileName);
        await using var signatureOutput = signatureEntry.Open();
        await JsonSerializer.SerializeAsync(signatureOutput, signature, PluginPackage.SerializerOptions);
    }

    private static Task WritePackageAsync(string path, IReadOnlyDictionary<string, byte[]> files, X509Certificate2 certificate) =>
        WritePackageAsync(path, files, PluginPackage.SignFiles(files, certificate));

    private static SortedDictionary<string, string> CreateCanonicalFileHashes(IReadOnlyDictionary<string, byte[]> files)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in files)
        {
            var content = item.Value;
            if (item.Key.Equals(PluginPackage.ManifestFileName, StringComparison.Ordinal))
            {
                var manifest = PluginPackage.DeserializeManifest(content);
                content = JsonSerializer.SerializeToUtf8Bytes(manifest, PluginPackage.SerializerOptions);
            }
            result.Add(item.Key, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        }
        return result;
    }

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string CreateTempPath() => Path.Combine(Path.GetTempPath(), "pcl-plugin-test-" + Guid.NewGuid().ToString("N") + PluginPackage.Extension);

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteRuntimeDirectory(string path)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }
        }
        if (Directory.Exists(path))
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(path, recursive: true); } catch { }
            };
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}

public static class ExperimentalFeatureTarget
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static int GetValue() => 1;
}

[Mixin(typeof(ExperimentalFeatureTarget))]
public static class ExperimentalFeatureMixin
{
    [Overwrite(nameof(ExperimentalFeatureTarget.GetValue))]
    public static int GetValue() => 2;
}

public static class StableFeatureTarget
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static int GetValue() => 3;
}

[Mixin(typeof(StableFeatureTarget))]
public static class StableFeatureMixin
{
    [Overwrite(nameof(StableFeatureTarget.GetValue))]
    public static int GetValue() => 9;
}

public sealed class ExperimentalFeatureProbePlugin : IPclPlugin
{
    public async Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        await context.Storage.WriteTextAsync("experimental.txt", ExperimentalFeatureTarget.GetValue().ToString(), cancellationToken);
        await context.Storage.WriteTextAsync("stable.txt", StableFeatureTarget.GetValue().ToString(), cancellationToken);
        await context.Storage.WriteTextAsync("started.txt", "yes", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class DependencyConsumerPlugin : IPclPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var dependencyReadyPath = Path.Combine(context.Launcher.PluginsDirectory, ".plugin-data", "zeta.provider", "ready.txt");
        if (!File.Exists(dependencyReadyPath) || SharedPluginApi.GetProviderToken() != "shared-provider-assembly-ready")
            throw new InvalidOperationException("The required provider was not started before this plugin.");
        return context.Storage.WriteTextAsync("started.txt", "started", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class GoodRuntimePlugin : IPclPlugin
{
    public async Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        await context.Storage.WriteTextAsync("started.txt", context.DataDirectory, cancellationToken);
        context.Ui.RegisterPage("test", context.Plugin.Id, () => new System.Windows.Controls.TextBlock());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FailingRuntimePlugin : IPclPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("intentional test startup failure");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
