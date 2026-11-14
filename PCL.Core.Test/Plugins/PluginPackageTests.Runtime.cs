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

public sealed partial class PluginPackageTests
{
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
