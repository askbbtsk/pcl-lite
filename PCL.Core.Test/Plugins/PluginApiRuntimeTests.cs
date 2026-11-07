using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;

namespace PCL.Core.Test.Plugins;

[TestClass]
[DoNotParallelize]
public sealed class PluginApiRuntimeTests
{
    [TestMethod]
    public async Task LoadedPluginCanUseEveryHostApiAndDisposesEventSubscriptions()
    {
        using var certificate = CreateCertificate();
        var packagePath = Path.Combine(Path.GetTempPath(), $"pcl-api-probe-{Guid.NewGuid():N}.pclplugin");
        var pluginsDirectory = Path.Combine(Path.GetTempPath(), "pcl-api-probe-data-" + Guid.NewGuid().ToString("N"));
        using var httpClient = new HttpClient(new ProbeHttpHandler());
        var notification = string.Empty;
        var notificationKind = PluginNotificationKind.Info;
        var confirmation = string.Empty;
        var openedUrl = default(Uri);
        var openedPath = string.Empty;
        var launchRequest = string.Empty;
        var previousNotice = PluginHostBridge.ShowNotification;
        var previousConfirm = PluginHostBridge.ConfirmAsync;
        var previousGetInstances = PluginHostBridge.GetInstancesAsync;
        var previousLaunch = PluginHostBridge.LaunchInstanceAsync;
        var previousOpenUrl = PluginHostBridge.OpenUrl;
        var previousOpenPath = PluginHostBridge.OpenPath;

        try
        {
            PluginHostBridge.ShowNotification = (message, kind) =>
            {
                notification = message;
                notificationKind = kind;
            };
            PluginHostBridge.ConfirmAsync = (title, message, token) =>
            {
                token.ThrowIfCancellationRequested();
                confirmation = title + ":" + message;
                return Task.FromResult(true);
            };
            PluginHostBridge.GetInstancesAsync = token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<PluginGameInstance>>(
                [new("instance-a", "Test instance", "1.21", "C:/Games/Test", true)]);
            };
            PluginHostBridge.LaunchInstanceAsync = (id, token) =>
            {
                token.ThrowIfCancellationRequested();
                launchRequest = id;
                return Task.FromResult(id == "instance-a");
            };
            PluginHostBridge.OpenUrl = uri => openedUrl = uri;
            PluginHostBridge.OpenPath = path => openedPath = path;

            await WriteProbePackageAsync(packagePath, certificate);
            await using (var installer = new PluginRuntimeManager(pluginsDirectory))
                await installer.InstallAsync(packagePath, (_, _) => Task.FromResult(true));

            await using var runtime = new PluginRuntimeManager(pluginsDirectory, httpClient);
            await runtime.LoadEnabledAsync();

            var installed = runtime.GetInstalled().Single();
            Assert.IsTrue(installed.Running, installed.Error);
            Assert.AreEqual("probe notice", notification);
            Assert.AreEqual(PluginNotificationKind.Warning, notificationKind);
            Assert.AreEqual("Probe title:Probe message", confirmation);
            Assert.AreEqual("https://example.test/path", openedUrl?.AbsoluteUri);
            Assert.AreEqual(Path.Combine(pluginsDirectory, ".plugin-data", "runtime.api.probe"), openedPath);
            Assert.AreEqual("instance-a", launchRequest);
            Assert.IsTrue(File.Exists(Path.Combine(pluginsDirectory, ".plugin-data", "runtime.api.probe", "probe-complete.json")));
            Assert.AreEqual(1, PluginPageRegistry.GetAll().Count(page => page.PluginId == "runtime.api.probe"));

            await runtime.DisposeAsync();
            await runtime.PublishAsync(new PluginGameLaunchingEvent("instance-a", "after-dispose"));
            var eventCount = await File.ReadAllTextAsync(Path.Combine(pluginsDirectory, ".plugin-data", "runtime.api.probe", "event-count.txt"));
            Assert.AreEqual("3", eventCount, "The stopped plugin must no longer receive manager events.");
        }
        finally
        {
            PluginHostBridge.ShowNotification = previousNotice;
            PluginHostBridge.ConfirmAsync = previousConfirm;
            PluginHostBridge.GetInstancesAsync = previousGetInstances;
            PluginHostBridge.LaunchInstanceAsync = previousLaunch;
            PluginHostBridge.OpenUrl = previousOpenUrl;
            PluginHostBridge.OpenPath = previousOpenPath;
            PluginPageRegistry.RemovePlugin("runtime.api.probe");
            DeleteIfExists(packagePath);
            DeleteDirectoryWithRetry(pluginsDirectory);
        }
    }

    private static async Task WriteProbePackageAsync(string path, X509Certificate2 certificate)
    {
        var entryAssembly = Path.GetFileName(typeof(ApiProbePlugin).Assembly.Location);
        var manifest = new PluginManifest
        {
            Id = "runtime.api.probe",
            Name = "Runtime API probe",
            Version = "1.0.0",
            Author = "PCL.Core.Test",
            EntryAssembly = entryAssembly,
            EntryType = typeof(ApiProbePlugin).FullName!
        };
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PluginPackage.ManifestFileName] = JsonSerializer.SerializeToUtf8Bytes(manifest, PluginPackage.SerializerOptions),
            [entryAssembly] = await File.ReadAllBytesAsync(typeof(ApiProbePlugin).Assembly.Location)
        };
        var signature = PluginPackage.SignFiles(files, certificate);
        await using var output = File.Create(path);
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
        const string password = "runtime-api-test-only";
        var pfx = PluginAuthoring.CreateSigningCertificate("CN=PCL Plugin Runtime API Test", password);
        return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(40); }
            catch (UnauthorizedAccessException) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(40); }
        }
    }

    private sealed class ProbeHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri?.Host != "plugin.test")
                throw new InvalidOperationException("Unexpected network host in API probe.");
            var body = request.RequestUri.AbsolutePath switch
            {
                "/get" => "get-response",
                "/post" => "post-response",
                "/download" => "download-payload",
                "/download-fail" => "",
                "/download-cancel" => "",
                "/download-oversize" => "tiny-body",
                "/http-oversize" => "tiny-body",
                _ => throw new InvalidOperationException($"Unexpected probe URI: {request.RequestUri}")
            };
            if (request.RequestUri.AbsolutePath == "/post" && request.Content is null)
                throw new InvalidOperationException("POST content was not forwarded.");
            if (request.RequestUri.AbsolutePath == "/download-fail")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ApiProbePlugin.StreamingContent(() => new ApiProbePlugin.FailAfterFirstReadStream())
                });
            if (request.RequestUri.AbsolutePath == "/download-cancel")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ApiProbePlugin.StreamingContent(() => new ApiProbePlugin.CancelAfterFirstReadStream())
                });
            if (request.RequestUri.AbsolutePath is "/download-oversize" or "/http-oversize")
            {
                var content = new StringContent(body, Encoding.UTF8, "text/plain");
                content.Headers.ContentLength = request.RequestUri.AbsolutePath == "/download-oversize"
                    ? 100L * 1024 * 1024 + 1
                    : 16L * 1024 * 1024 + 1;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            });
        }
    }
}

public sealed record ApiProbeSettings(string Value, int Count);

public sealed class ApiProbePlugin : IPclPlugin
{
    public async Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        Require(context.Plugin.Id == "runtime.api.probe", "metadata id");
        Require(context.Plugin.Name == "Runtime API probe", "metadata name");
        Require(context.Plugin.Version == "1.0.0", "metadata version");
        Require(context.Plugin.Author == "PCL.Core.Test", "metadata author");
        Require(context.Plugin.ApiVersion == PluginApiVersion.Current, "metadata API version");
        Require(Directory.Exists(context.DataDirectory), "data directory");
        Require(context.Launcher.Name == "PCL Lite", "launcher name");
        Require(!string.IsNullOrWhiteSpace(context.Launcher.Version), "launcher version");
        Require(Directory.Exists(context.Launcher.PluginsDirectory), "launcher plugins directory");
        Require(context.Launcher.DataDirectory == context.DataDirectory, "launcher data directory");
        context.Logger.Trace("trace");
        context.Logger.Debug("debug");
        context.Logger.Info("info");
        context.Logger.Warn("warn");
        context.Logger.Error("error");

        await context.Storage.WriteTextAsync("nested/value.txt", "stored", cancellationToken);
        Require(await context.Storage.ReadTextAsync("nested/value.txt", cancellationToken) == "stored", "text storage roundtrip");
        await context.Storage.WriteJsonAsync("nested/value.json", new ApiProbeSettings("json", 3), cancellationToken);
        var stored = await context.Storage.ReadJsonAsync<ApiProbeSettings>("nested/value.json", cancellationToken);
        Require(stored == new ApiProbeSettings("json", 3), "JSON storage roundtrip");
        await RequireThrowsAsync<ArgumentException>(() => context.Storage.WriteTextAsync("../escape", "bad", cancellationToken), "storage traversal");
        await RequireThrowsAsync<OperationCanceledException>(() => context.Storage.ReadTextAsync("nested/value.txt", new CancellationToken(true)), "storage cancellation");

        await context.Settings.SetAsync("probe", new ApiProbeSettings("setting", 7), cancellationToken);
        Require(await context.Settings.GetAsync<ApiProbeSettings>("probe", cancellationToken) == new ApiProbeSettings("setting", 7), "settings roundtrip");
        await context.Settings.RemoveAsync("probe", cancellationToken);
        Require(await context.Settings.GetAsync<ApiProbeSettings>("probe", cancellationToken) is null, "settings removal");
        await context.Settings.SetAsync("cancel-remove", "preserved", cancellationToken);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                () => context.Settings.RemoveAsync("cancel-remove", canceled.Token), "settings removal cancellation");
        }
        Require(await context.Settings.GetAsync<string>("cancel-remove", cancellationToken) == "preserved", "canceled settings removal preservation");
        await RequireThrowsAsync<ArgumentException>(() => context.Settings.SetAsync("../escape", "bad", cancellationToken), "settings key validation");

        var eventCount = 0;
        _ = context.Events.Subscribe<PluginGameLaunchingEvent>(async (value, token) =>
        {
            token.ThrowIfCancellationRequested();
            eventCount++;
            await context.Storage.WriteTextAsync("event-count.txt", eventCount.ToString(), token);
            if (value.InstanceName == "throw") throw new InvalidOperationException("isolated event failure");
        });
        await context.Events.PublishAsync(new PluginGameLaunchingEvent("instance-a", "one"), cancellationToken);
        await context.Events.PublishAsync(new PluginGameLaunchingEvent("instance-a", "two"), cancellationToken);
        Require(eventCount == 2, "event publish/subscribe");
        await context.Events.PublishAsync(new PluginGameLaunchingEvent("instance-a", "throw"), cancellationToken);
        Require(eventCount == 3, "failing event handler isolation");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                () => context.Events.PublishAsync(new PluginGameLaunchingEvent("instance-a", "cancel"), canceled.Token), "event cancellation");
        }

        var get = await context.Network.GetAsync(new Uri("https://plugin.test/get"), cancellationToken);
        Require(get.StatusCode == 200 && get.Content == "get-response", "HTTP GET");
        Require(get.Headers.ContainsKey("Content-Type"), "HTTP response headers");
        var post = await context.Network.SendAsync(HttpMethod.Post, new Uri("https://plugin.test/post"), "request",
            "text/plain", cancellationToken);
        Require(post.StatusCode == 200 && post.Content == "post-response", "HTTP POST");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                () => context.Network.GetAsync(new Uri("https://plugin.test/get"), canceled.Token), "HTTP cancellation");
        }
        await RequireThrowsAsync<InvalidDataException>(
            () => context.Network.GetAsync(new Uri("https://plugin.test/http-oversize"), cancellationToken),
            "HTTP response size limit");

        var instances = await context.Minecraft.GetInstancesAsync(cancellationToken);
        Require(instances.Count == 1 && instances[0].Id == "instance-a" && instances[0].IsSelected, "Minecraft instance query");
        Require(await context.Minecraft.LaunchAsync("instance-a", cancellationToken), "Minecraft launch");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                () => context.Minecraft.GetInstancesAsync(canceled.Token), "Minecraft query cancellation");
            await RequireThrowsAsync<OperationCanceledException>(
                () => context.Minecraft.LaunchAsync("instance-a", canceled.Token), "Minecraft launch cancellation");
        }

        var progress = new ProbeProgress();
        await context.Launcher.DownloadAsync(new Uri("https://plugin.test/download"), "downloads/payload.txt", progress,
            cancellationToken);
        Require(await context.Storage.ReadTextAsync("downloads/payload.txt", cancellationToken) == "download-payload", "launcher download");
        Require(progress.Last is { BytesReceived: > 0 }, "download progress");
        await context.Storage.WriteTextAsync("downloads/atomic.txt", "original", cancellationToken);
        await RequireThrowsAsync<IOException>(
            () => context.Launcher.DownloadAsync(new Uri("https://plugin.test/download-fail"), "downloads/atomic.txt", cancellationToken: cancellationToken),
            "failed download response");
        Require(await context.Storage.ReadTextAsync("downloads/atomic.txt", cancellationToken) == "original", "failed download keeps old file");
        using (var canceled = new CancellationTokenSource())
        {
            var pendingDownload = context.Launcher.DownloadAsync(new Uri("https://plugin.test/download-cancel"),
                "downloads/atomic.txt", cancellationToken: canceled.Token);
            await Task.Delay(50, cancellationToken);
            canceled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(() => pendingDownload, "mid-stream download cancellation");
        }
        Require(await context.Storage.ReadTextAsync("downloads/atomic.txt", cancellationToken) == "original", "canceled download keeps old file");
        Require(!Directory.EnumerateFiles(context.DataDirectory, "*.tmp-*", SearchOption.AllDirectories).Any(),
            "failed and canceled downloads clean temporary files");
        await RequireThrowsAsync<InvalidDataException>(
            () => context.Launcher.DownloadAsync(new Uri("https://plugin.test/download-oversize"), "downloads/oversized.txt", cancellationToken: cancellationToken),
            "download size limit");
        await RequireThrowsAsync<ArgumentException>(
            () => context.Launcher.DownloadAsync(new Uri("http://plugin.test/download"), "bad.txt", cancellationToken: cancellationToken),
            "download HTTPS enforcement");
        await RequireThrowsAsync<ArgumentException>(
            () => context.Launcher.DownloadAsync(new Uri("https://plugin.test/download"), "../escape", cancellationToken: cancellationToken),
            "download path traversal");

        context.Launcher.OpenUrl(new Uri("https://example.test/path"));
        context.Launcher.OpenPath(context.DataDirectory);
        await RequireThrowsAsync<ArgumentException>(() =>
            {
                context.Launcher.OpenUrl(new Uri("file:///C:/Windows"));
                return Task.CompletedTask;
            }, "URL scheme validation");
        await RequireThrowsAsync<FileNotFoundException>(() =>
            {
                context.Launcher.OpenPath(Path.Combine(context.DataDirectory, "missing.file"));
                return Task.CompletedTask;
            }, "open path existence");

        context.Ui.RegisterPage("probe", "Probe page", () => new System.Windows.Controls.TextBlock { Text = "probe" });
        context.Ui.ShowNotification("probe notice", PluginNotificationKind.Warning);
        Require(await context.Ui.ConfirmAsync("Probe title", "Probe message", cancellationToken), "confirmation response");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                () => context.Ui.ConfirmAsync("Canceled", "confirmation", canceled.Token), "confirmation cancellation");
        }
        await context.Storage.WriteJsonAsync("probe-complete.json", new ApiProbeSettings("complete", 1), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Plugin API probe failed: {label}.");
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action, string label)
        where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Plugin API probe expected {typeof(TException).Name}: {label}.");
    }

    private sealed class ProbeProgress : IProgress<PluginDownloadProgress>
    {
        public PluginDownloadProgress? Last { get; private set; }
        public void Report(PluginDownloadProgress value) => Last = value;
    }

    public sealed class StreamingContent(Func<Stream> streamFactory) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException("Probe response bodies are consumed as streams.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(streamFactory());
    }

    public sealed class FailAfterFirstReadStream : Stream
    {
        private bool _firstRead = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_firstRead) return ValueTask.FromException<int>(new IOException("Intentional interrupted response body."));
            _firstRead = false;
            "partial"u8.CopyTo(buffer.Span);
            return ValueTask.FromResult(7);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public sealed class CancelAfterFirstReadStream : Stream
    {
        private bool _firstRead = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                "partial"u8.CopyTo(buffer.Span);
                return 7;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
