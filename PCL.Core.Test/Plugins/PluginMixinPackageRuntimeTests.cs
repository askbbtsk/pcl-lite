using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Plugins;
using PCL.Mixin;
using PCL.Plugin.SDK;

namespace PCL.Core.Test.Plugins;

[TestClass]
[DoNotParallelize]
public sealed class PluginMixinPackageRuntimeTests
{
    [TestMethod]
    public async Task SignedPackagesApplyMixinRollbackFailedPluginAndRestoreHostMethodOnShutdown()
    {
        using var certificate = CreateCertificate();
        var root = Path.Combine(Path.GetTempPath(), "pcl-mixin-package-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var packageDirectory = Path.Combine(root, "packages");
            Directory.CreateDirectory(packageDirectory);
            var brokenPackage = Path.Combine(packageDirectory, "broken.pclplugin");
            var workingPackage = Path.Combine(packageDirectory, "working.pclplugin");

            await CreatePackageAsync(brokenPackage, certificate, "runtime.mixin.bad", [
                new MixinConfiguration { Mixins = [typeof(HostTargetMixin).FullName!] },
                new MixinConfiguration { Mixins = ["PCL.Core.Test.Plugins.MissingMixin"] }
            ]);
            await CreatePackageAsync(workingPackage, certificate, "runtime.mixin.good", [
                new MixinConfiguration { Mixins = [typeof(HostTargetMixin).FullName!] }
            ]);

            var pluginsDirectory = Path.Combine(root, "Plugins");
            await using (var installer = new PluginRuntimeManager(pluginsDirectory))
            {
                await installer.InstallAsync(brokenPackage, (_, _) => Task.FromResult(true));
                await installer.InstallAsync(workingPackage, (_, _) => Task.FromResult(true));
            }

            Assert.AreEqual(12, HostTarget.Compute(6), "The host method starts with its unmodified result.");

            await using (var runtime = new PluginRuntimeManager(pluginsDirectory))
            {
                await runtime.LoadEnabledAsync();

                var installed = runtime.GetInstalled().ToDictionary(item => item.Manifest.Id);
                Assert.IsFalse(installed["runtime.mixin.bad"].Running,
                    "A required config failure must fail only its own plugin.");
                Assert.IsNotNull(installed["runtime.mixin.bad"].Error);
                Assert.IsTrue(installed["runtime.mixin.good"].Running,
                    "A later plugin must still load after another package fails.");
                Assert.AreEqual(17, HostTarget.Compute(6),
                    "The signed package must apply its Mixin to the already loaded host method.");
            }

            Assert.AreEqual(12, HostTarget.Compute(6),
                "Disposing the plugin manager must unpatch the host method before unloading plugin assemblies.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                for (var attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
                {
                    try { Directory.Delete(root, recursive: true); }
                    catch (IOException) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(40); }
                    catch (UnauthorizedAccessException) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(40); }
                }
            }
        }
    }

    private static async Task CreatePackageAsync(string packagePath, X509Certificate2 certificate,
        string pluginId, IReadOnlyList<MixinConfiguration> configurations)
    {
        var entryAssembly = Path.GetFileName(typeof(PluginMixinPackageRuntimeTests).Assembly.Location);
        var configPaths = configurations.Select((_, index) => $"mixins/config-{index}.json").ToArray();
        var manifest = new PluginManifest
        {
            Id = pluginId,
            Name = pluginId,
            Version = "1.0.0",
            Author = "PCL.Core.Test",
            EntryAssembly = entryAssembly,
            EntryType = typeof(MixinPackageProbePlugin).FullName!,
            MixinConfigs = configPaths
        };

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(manifest, PluginPackage.SerializerOptions),
            [entryAssembly] = await File.ReadAllBytesAsync(typeof(PluginMixinPackageRuntimeTests).Assembly.Location)
        };
        for (var index = 0; index < configurations.Count; index++)
            files[configPaths[index]] = JsonSerializer.SerializeToUtf8Bytes(configurations[index], PluginPackage.SerializerOptions);

        var signature = PluginPackage.SignFiles(files, certificate);
        await using var output = File.Create(packagePath);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var (name, contents) in files)
        {
            var entry = archive.CreateEntry(name);
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(contents);
        }
        var signatureEntry = archive.CreateEntry(PluginPackage.SignatureFileName);
        await using var signatureStream = signatureEntry.Open();
        await JsonSerializer.SerializeAsync(signatureStream, signature, PluginPackage.SerializerOptions);
    }

    private static X509Certificate2 CreateCertificate()
    {
        const string password = "mixin-package-e2e-test-only";
        var pfx = PluginAuthoring.CreateSigningCertificate("CN=PCL Mixin Package Runtime Test", password);
        return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
    }

    public static class HostTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Compute(int value) => value * 2;
    }

    [Mixin("PCL.Core.Test.Plugins.PluginMixinPackageRuntimeTests+HostTarget, PCL.Core.Test")]
    public static class HostTargetMixin
    {
        [Inject(nameof(HostTarget.Compute), At = MixinAt.Return)]
        private static void AddFive([Return] ref int result) => result += 5;
    }
}

public sealed class MixinPackageProbePlugin : IPclPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
