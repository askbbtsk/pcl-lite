using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.IO.Net;
using PCL.Plugin.SDK;
using PCL.Mixin;

namespace PCL.Core.Plugins;

public sealed partial class PluginRuntimeManager
{

    private sealed class PluginContext(PluginRuntimeManager owner, PluginManifest manifest) : IPluginContext, IDisposable
    {
        private readonly PluginEvents _events = new(owner);
        public IPluginMetadata Plugin { get; } = new PluginMetadata(manifest);
        public string DataDirectory { get; } = Path.Combine(owner._pluginsDirectory, ".plugin-data", manifest.Id);
        public IPluginLogger Logger { get; } = new PluginLogger(manifest.Id);
        public IPluginStorage Storage { get; } = new PluginStorage(Path.Combine(owner._pluginsDirectory, ".plugin-data", manifest.Id));
        public IPluginSettings Settings { get; } = new PluginSettings(Path.Combine(owner._pluginsDirectory, ".plugin-data", manifest.Id));
        public IPluginUi Ui { get; } = new PluginUi(manifest.Id);
        public IPluginEvents Events => _events;
        public IPluginNetwork Network { get; } = new PluginNetwork(owner._httpClient);
        public IPluginMinecraft Minecraft { get; } = new PluginMinecraft(owner);
        public IPluginLauncher Launcher { get; } = new PluginLauncher(owner._pluginsDirectory,
            Path.Combine(owner._pluginsDirectory, ".plugin-data", manifest.Id), owner._httpClient);
        public IPluginServices Services { get; } = new PluginServices(owner, manifest.Id);
        public void Dispose()
        {
            _events.Dispose();
            owner._RemovePluginServices(manifest.Id);
        }
    }


    private sealed record PluginMetadata(PluginManifest Manifest) : IPluginMetadata
    {
        public string Id => Manifest.Id; public string Name => Manifest.Name; public string Version => Manifest.Version;
        public string Author => Manifest.Author; public string Description => Manifest.Description; public int ApiVersion => Manifest.ApiVersion;
    }


    private sealed class PluginLogger(string id) : IPluginLogger
    {
        public void Trace(string message) => LogWrapper.Trace("Plugin/" + id, message);
        public void Debug(string message) => LogWrapper.Debug("Plugin/" + id, message);
        public void Info(string message) => LogWrapper.Info("Plugin/" + id, message);
        public void Warn(string message, Exception? exception = null) => LogWrapper.Warn(exception, "Plugin/" + id, message);
        public void Error(string message, Exception? exception = null) => LogWrapper.Error(exception, "Plugin/" + id, message);
    }


    private sealed class PluginServices(PluginRuntimeManager owner, string pluginId) : IPluginServices
    {
        public bool Register<TService>(TService service) where TService : class
        {
            ArgumentNullException.ThrowIfNull(service);
            return owner._RegisterService(pluginId, service);
        }
        public TService? Get<TService>() where TService : class => owner._GetService<TService>();
        public bool Unregister<TService>() where TService : class => owner._UnregisterService<TService>(pluginId);
    }


    private sealed class PluginStorage(string root) : IPluginStorage
    {
        public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(relativePath);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        public async Task WriteTextAsync(string relativePath, string contents, CancellationToken cancellationToken = default)
        {
            var path = Resolve(relativePath);
            var contentBytes = System.Text.Encoding.UTF8.GetByteCount(contents);
            if (contentBytes > MaximumPluginStoredFileBytes)
                throw new InvalidDataException("Plugin storage file exceeds the 64 MiB per-file limit.");
            var rootPath = Path.GetFullPath(root);
            var gate = _GetPluginDataLock(rootPath);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? temporaryPath = null;
            try
            {
                path = Resolve(relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                path = Resolve(relativePath);
                temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
                var usage = PluginRuntimeManager._GetPluginDataUsage(rootPath);
                var replacedBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                var projectedBytes = checked(usage.Bytes - replacedBytes + contentBytes);
                var projectedFiles = usage.Files + (File.Exists(path) ? 0 : 1);
                if (projectedBytes > MaximumPluginDataBytes || projectedFiles > MaximumPluginDataFiles)
                    throw new InvalidDataException("Plugin data directory exceeds its storage quota.");
                await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try { if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                gate.Release();
            }
        }
        public async Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            await using var input = await OpenReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (input is null) return null;
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumPluginStoredFileBytes)
                    throw new InvalidDataException("Plugin storage file exceeds the 64 MiB per-file limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            return output.ToArray();
        }
        public async Task WriteBytesAsync(string relativePath, ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken = default)
        {
            using var source = new MemoryStream(contents.ToArray(), writable: false);
            await WriteStreamAsync(relativePath, source, contents.Length, cancellationToken).ConfigureAwait(false);
        }
        public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(relativePath);
            if (!File.Exists(path)) return Task.FromResult<Stream?>(null);
            if (new FileInfo(path).Length > MaximumPluginStoredFileBytes)
                throw new InvalidDataException("Plugin storage file exceeds the 64 MiB per-file limit.");
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<Stream?>(stream);
        }
        public async Task WriteStreamAsync(string relativePath, Stream source, long? expectedLength = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead) throw new ArgumentException("Source stream must be readable.", nameof(source));
            if (expectedLength is < 0 or > MaximumPluginStoredFileBytes)
                throw new InvalidDataException("Plugin storage file exceeds the 64 MiB per-file limit.");
            var rootPath = Path.GetFullPath(root);
            var gate = PluginRuntimeManager._GetPluginDataLock(rootPath);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            string? temporaryPath = null;
            try
            {
                var path = Resolve(relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
                var usage = PluginRuntimeManager._GetPluginDataUsage(rootPath);
                var replacedBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (!File.Exists(path) && usage.Files >= MaximumPluginDataFiles)
                    throw new InvalidDataException("Plugin data directory exceeds the 10,000 file limit.");
                await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        total = checked(total + count);
                        if (total > MaximumPluginStoredFileBytes ||
                            checked(usage.Bytes - replacedBytes + total) > MaximumPluginDataBytes)
                            throw new InvalidDataException("Plugin data directory exceeds its storage quota.");
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }
                    if (expectedLength is { } expected && total != expected)
                        throw new InvalidDataException("Plugin storage stream length did not match the expected length.");
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try { if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                gate.Release();
            }
        }
        public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(relativePath);
            return Task.FromResult(File.Exists(path) || Directory.Exists(path));
        }
        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default) =>
            RemoveFileAsync(relativePath, cancellationToken);
        public Task<IReadOnlyList<string>> ListAsync(string relativeDirectory = "",
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = string.IsNullOrEmpty(relativeDirectory)
                ? Path.GetFullPath(root)
                : Resolve(relativeDirectory);
            if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<string>>([]);
            var entries = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Plugin data cannot contain symbolic links or junctions.");
                entries.Add(Path.GetFileName(entry));
            }
            entries.Sort(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyList<string>>(entries);
        }
        public async Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken = default)
        {
            var text = await ReadTextAsync(relativePath, cancellationToken).ConfigureAwait(false);
            return text is null ? default : JsonSerializer.Deserialize<T>(text);
        }
        public Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken = default) =>
            WriteTextAsync(relativePath, JsonSerializer.Serialize(value), cancellationToken);
        public async Task RemoveFileAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            var rootPath = Path.GetFullPath(root);
            var gate = PluginRuntimeManager._GetPluginDataLock(rootPath);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Resolve(relativePath);
                if (File.Exists(path)) File.Delete(path);
            }
            finally
            {
                gate.Release();
            }
        }
        private string Resolve(string relative)
        {
            PluginPackage.ValidateRelativePath(relative);
            var rootPath = Path.GetFullPath(root);
            var path = Path.GetFullPath(Path.Combine(rootPath, relative));
            if (!path.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Path escapes plugin storage.", nameof(relative));
            var dataRootAncestor = rootPath;
            for (var depth = 0; depth < 3 && dataRootAncestor is not null; depth++)
            {
                try
                {
                    if ((File.GetAttributes(dataRootAncestor) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Plugin data cannot be stored below a symbolic link or junction.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                dataRootAncestor = Path.GetDirectoryName(dataRootAncestor);
            }
            var current = rootPath;
            foreach (var segment in Path.GetRelativePath(rootPath, path)
                         .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Plugin data cannot contain symbolic links or junctions.");
                }
                catch (FileNotFoundException) { break; }
                catch (DirectoryNotFoundException) { break; }
            }
            return path;
        }

    }


    private sealed class PluginSettings(string root) : IPluginSettings
    {
        private readonly PluginStorage _storage = new(root);
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            _storage.ReadJsonAsync<T>("settings/" + _SafeKey(key) + ".json", cancellationToken);
        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) =>
            _storage.WriteJsonAsync("settings/" + _SafeKey(key) + ".json", value, cancellationToken);
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            _storage.RemoveFileAsync("settings/" + _SafeKey(key) + ".json", cancellationToken);
        private static string _SafeKey(string key) => string.IsNullOrWhiteSpace(key) || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            ? throw new ArgumentException("Settings keys may contain letters, digits, '-' and '_'.", nameof(key)) : key;
    }


    private sealed class PluginUi(string pluginId) : IPluginUi
    {
        public void RegisterPage(string id, string title, Func<System.Windows.FrameworkElement> createView)
            => RegisterPage(id, title, createView, new PluginPageLifecycle());

        public void RegisterPage(string id, string title, Func<System.Windows.FrameworkElement> createView,
            PluginPageLifecycle lifecycle)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) throw new ArgumentException("Invalid page id.", nameof(id));
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(createView);
            ArgumentNullException.ThrowIfNull(lifecycle);
            PluginPageRegistry.Register(new(pluginId, id, title, createView, lifecycle));
        }
        public bool UnregisterPage(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) throw new ArgumentException("Invalid page id.", nameof(id));
            return PluginPageRegistry.Unregister(pluginId, id);
        }
        public System.Windows.FrameworkElement CreateButton(string text, Func<Task> onClick,
            PluginButtonStyle style = PluginButtonStyle.Normal)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            ArgumentNullException.ThrowIfNull(onClick);
            return PluginHostBridge.CreateButton?.Invoke(pluginId, text, onClick, style)
                   ?? throw new InvalidOperationException("The PCL button factory is not available.");
        }
        public void ShowNotification(string message, PluginNotificationKind kind = PluginNotificationKind.Info) =>
            PluginHostBridge.ShowNotification?.Invoke(message, kind);
        public void ShowNotification(string message, PluginNotificationKind kind, Func<Task>? onClick) =>
            (PluginHostBridge.ShowNotificationWithAction ?? ((text, level, _) => PluginHostBridge.ShowNotification?.Invoke(text, level)))
            .Invoke(message, kind, onClick);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
            PluginHostBridge.ConfirmAsync?.Invoke(title, message, cancellationToken) ??
            Task.FromResult(System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes);
        public async Task<int> ChooseAsync(string title, string message, PluginDialogOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PluginHostBridge.ChooseAsync is { } chooser)
                return await chooser(title, message, options, cancellationToken).ConfigureAwait(false);
            return await ConfirmAsync(title, message, cancellationToken).ConfigureAwait(false) ? 1 : 2;
        }
    }


    private sealed class PluginEvents(PluginRuntimeManager owner) : IPluginEvents, IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];
        private readonly object _lock = new();
        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        {
            var map = owner._handlers.GetOrAdd(typeof(TEvent), _ => new());
            var key = Guid.NewGuid();
            map[key] = (value, token) => handler((TEvent)value, token);
            var subscription = new Subscription(() => map.TryRemove(key, out _));
            lock (_lock) _subscriptions.Add(subscription);
            return subscription;
        }
        public Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default) =>
            owner.PublishAsync(eventData, cancellationToken);

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var subscription in _subscriptions) subscription.Dispose();
                _subscriptions.Clear();
            }
        }
    }


    private sealed class PluginNetwork(HttpClient client) : IPluginNetwork
    {
        public Task<PluginHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Get, uri, content: null, contentType: "application/json", headers: new Dictionary<string, string>(), cancellationToken: cancellationToken);
        public Task<PluginHttpResponse> GetAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
            TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            SendAsync(HttpMethod.Get, uri, content: null, contentType: "application/json", headers: headers, timeout: timeout, cancellationToken: cancellationToken);
        public async Task<PluginHttpResponse> SendAsync(HttpMethod method, Uri uri, string? content = null,
            string contentType = "application/json", CancellationToken cancellationToken = default)
            => await SendAsync(method, uri, content, contentType, new Dictionary<string, string>(), null,
                cancellationToken).ConfigureAwait(false);
        public async Task<PluginHttpResponse> SendAsync(HttpMethod method, Uri uri, string? content,
            string contentType, IReadOnlyDictionary<string, string> headers, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (method is null) throw new ArgumentNullException(nameof(method));
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http"))
                throw new ArgumentException("Plugin HTTP requests require an absolute HTTP(S) URI.", nameof(uri));
            if (content is not null && System.Text.Encoding.UTF8.GetByteCount(content) > MaximumPluginHttpBodyBytes)
                throw new InvalidDataException("Plugin HTTP request content exceeds the 16 MiB limit.");
            using var request = new HttpRequestMessage(method, uri);
            if (content is not null) request.Content = new StringContent(content, System.Text.Encoding.UTF8, contentType);
            foreach (var header in headers ?? throw new ArgumentNullException(nameof(headers)))
            {
                if (request.Headers.TryAddWithoutValidation(header.Key, header.Value)) continue;
                if (request.Content is null || !request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    throw new ArgumentException($"Invalid HTTP request header: {header.Key}", nameof(headers));
            }
            using var timeoutSource = timeout is { } value
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (timeoutSource is not null) timeoutSource.CancelAfter(timeout!.Value);
            var requestToken = timeoutSource?.Token ?? cancellationToken;
            using var response = await client.SendAsync(request, requestToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is > MaximumPluginHttpBodyBytes)
                throw new InvalidDataException("Plugin HTTP response exceeds the 16 MiB limit.");
            await using var responseStream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using var bodyStream = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await responseStream.ReadAsync(buffer, requestToken).ConfigureAwait(false)) > 0)
            {
                if (bodyStream.Length + count > MaximumPluginHttpBodyBytes)
                    throw new InvalidDataException("Plugin HTTP response exceeds the 16 MiB limit.");
                await bodyStream.WriteAsync(buffer.AsMemory(0, count), requestToken).ConfigureAwait(false);
            }
            var bodyBytes = bodyStream.ToArray();
            var mediaType = response.Content.Headers.ContentType;
            var body = _GetResponseEncoding(mediaType?.CharSet).GetString(bodyBytes);
            return new PluginHttpResponse((int)response.StatusCode, body, response.Headers.Concat(response.Content.Headers)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase))
            {
                ContentBytes = bodyBytes,
                ContentType = mediaType?.ToString()
            };
        }

        private static System.Text.Encoding _GetResponseEncoding(string? charset)
        {
            if (string.IsNullOrWhiteSpace(charset)) return System.Text.Encoding.UTF8;
            try { return System.Text.Encoding.GetEncoding(charset.Trim().Trim('"', '\'')); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return System.Text.Encoding.UTF8;
            }
        }
    }


    private sealed class PluginMinecraft(PluginRuntimeManager owner) : IPluginMinecraft
    {
        public Task<IReadOnlyList<PluginGameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) => owner.GetInstancesAsync(cancellationToken);
        public Task<PluginGameInstance?> GetSelectedInstanceAsync(CancellationToken cancellationToken = default) => owner.GetSelectedInstanceAsync(cancellationToken);
        public Task<IReadOnlyDictionary<string, string>> GetInstanceConfigurationAsync(string instanceId, CancellationToken cancellationToken = default) => owner.GetInstanceConfigurationAsync(instanceId, cancellationToken);
        public Task<bool> LaunchAsync(string instanceId, CancellationToken cancellationToken = default) => owner.LaunchAsync(instanceId, cancellationToken);
        public Task<bool> LaunchAsync(string instanceId, PluginLaunchOptions options, CancellationToken cancellationToken = default) => owner.LaunchAsync(instanceId, options, cancellationToken);
    }


    private sealed class PluginLauncher(string pluginsDirectory, string dataDirectory, HttpClient httpClient) : IPluginLauncher
    {
        public string Name => "PCL Lite";
        public string Version => typeof(PluginRuntimeManager).Assembly.GetName().Version?.ToString() ?? "unknown";
        public string CoreVersion => Basics.VersionName;
        public string DataDirectory => dataDirectory;
        public string PluginsDirectory => pluginsDirectory;
        public async Task DownloadAsync(Uri uri, string relativePath, IProgress<PluginDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => await DownloadCoreAsync(uri, relativePath, null, progress, cancellationToken).ConfigureAwait(false);
        public async Task DownloadAsync(Uri uri, string relativePath, string expectedSha256,
            IProgress<PluginDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
            => await DownloadCoreAsync(uri, relativePath, expectedSha256, progress, cancellationToken).ConfigureAwait(false);
        private async Task DownloadCoreAsync(Uri uri, string relativePath, string? expectedSha256,
            IProgress<PluginDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            if (expectedSha256 is not null && (expectedSha256.Length != 64 ||
                    expectedSha256.Any(c => !Uri.IsHexDigit(c))))
                throw new ArgumentException("Expected SHA-256 must contain 64 hexadecimal characters.", nameof(expectedSha256));
            PluginPackage.ValidateRelativePath(relativePath);
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Downloads must use an absolute HTTPS URI.", nameof(uri));
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (finalUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Plugin download redirected away from HTTPS.");
            if (response.Content.Headers.ContentLength is > MaximumPluginDownloadBytes)
                throw new InvalidDataException("Plugin download exceeds the 64 MiB per-file limit.");
            var target = Path.GetFullPath(Path.Combine(dataDirectory, relativePath));
            if (!target.StartsWith(Path.GetFullPath(dataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Download destination escapes plugin data directory.", nameof(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dataRoot = Path.GetFullPath(dataDirectory);
            var gate = _GetPluginDataLock(dataRoot);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var temporaryPath = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var usage = _GetPluginDataUsage(dataRoot);
                var replacedBytes = File.Exists(target) ? new FileInfo(target).Length : 0;
                var expectedLength = response.Content.Headers.ContentLength;
                if (expectedLength is { } knownLength &&
                    checked(usage.Bytes - replacedBytes + knownLength) > MaximumPluginDataBytes)
                    throw new InvalidDataException("Plugin data directory would exceed the 256 MiB storage quota.");
                if (!File.Exists(target) && usage.Files >= MaximumPluginDataFiles)
                    throw new InvalidDataException("Plugin data directory exceeds the 10,000 file limit.");
                using var hash = expectedSha256 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[81920]; long total = 0; int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        total = checked(total + count);
                        if (total > MaximumPluginDownloadBytes)
                            throw new InvalidDataException("Plugin download exceeds the 64 MiB per-file limit.");
                        if (checked(usage.Bytes - replacedBytes + total) > MaximumPluginDataBytes)
                            throw new InvalidDataException("Plugin data directory would exceed the 256 MiB storage quota.");
                        hash?.AppendData(buffer, 0, count);
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        progress?.Report(new(total, response.Content.Headers.ContentLength));
                    }
                    if (expectedLength is { } expected && total != expected)
                        throw new InvalidDataException("Plugin download ended before the declared content length.");
                    if (expectedSha256 is not null && !string.Equals(
                            Convert.ToHexString(hash!.GetHashAndReset()), expectedSha256,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Plugin download SHA-256 does not match the expected digest.");
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, target, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                gate.Release();
            }
        }
        public void OpenUrl(Uri uri)
        {
            if (uri.Scheme is not ("https" or "http")) throw new ArgumentException("Only HTTP(S) URLs are supported.", nameof(uri));
            if (PluginHostBridge.OpenUrl is { } opener) opener(uri);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        public void OpenPath(string path)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("Path does not exist.", full);
            if (PluginHostBridge.OpenPath is { } opener) opener(full);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true });
        }
    }


    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

}
