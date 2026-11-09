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

    private async Task<PluginState> _PreparePluginAsync(string directory, PluginManifest manifest, PluginState state,
        CancellationToken cancellationToken)
    {
        var assemblyPath = Path.Combine(directory, manifest.EntryAssembly.Replace('/', Path.DirectorySeparatorChar));
        var sharedAssemblies = _ResolveDependencyAssemblies(manifest);
        var loadContext = new PluginAssemblyLoadContext(directory, assemblyPath, sharedAssemblies);
        var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        try
        {
            state = await _ApplyPluginMixinsAsync(assembly, manifest, state, cancellationToken).ConfigureAwait(false);
            var type = assembly.GetType(manifest.EntryType, throwOnError: true, ignoreCase: false)!;
            if (!typeof(IPclPlugin).IsAssignableFrom(type)) throw new InvalidDataException("Plugin entry type must implement IPclPlugin.");
            lock (_sync) _prepared.Add(manifest.Id, new(manifest, loadContext, assembly, type));
            return state;
        }
        catch
        {
            // A required later config may fail after earlier configs already patched methods.
            _mixinRuntime?.RollbackAssembly(assembly);
            loadContext.Unload();
            throw;
        }
    }


    private Dictionary<string, Assembly> _ResolveDependencyAssemblies(PluginManifest manifest)
    {
        var result = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void AddDependency(string pluginId)
        {
            if (!visited.Add(pluginId)) return;
            if (!_prepared.TryGetValue(pluginId, out var dependency))
                throw new InvalidDataException($"Required plugin dependency is not prepared: {pluginId}.");
            var assembly = dependency.Assembly;
            var name = assembly.GetName().Name;
            if (!string.IsNullOrWhiteSpace(name) && result.TryGetValue(name, out var existing))
            {
                if (!ReferenceEquals(existing, assembly))
                    throw new InvalidDataException($"Conflicting dependency assemblies use the same name: {name}.");
            }
            else if (!string.IsNullOrWhiteSpace(name)) result[name] = assembly;

            foreach (var transitiveDependency in dependency.Manifest.Dependencies.Keys.Order(StringComparer.Ordinal))
                AddDependency(transitiveDependency);
        }

        foreach (var dependencyId in manifest.Dependencies.Keys.Order(StringComparer.Ordinal))
            AddDependency(dependencyId);
        return result;
    }


    private async Task _StartPluginAsync(PreparedPlugin prepared, CancellationToken cancellationToken)
    {
        if (Activator.CreateInstance(prepared.EntryType) is not IPclPlugin plugin)
            throw new InvalidDataException("Plugin entry type must have a public parameterless constructor.");
        Directory.CreateDirectory(Path.Combine(_pluginsDirectory, ".plugin-data", prepared.Manifest.Id));
        var context = new PluginContext(this, prepared.Manifest);
        try
        {
            await plugin.StartAsync(context, cancellationToken).ConfigureAwait(false);
            lock (_sync) _running.Add(new(plugin, context, prepared.LoadContext, prepared.Manifest, prepared.Assembly));
            _current = this;
        }
        catch
        {
            PluginPageRegistry.RemovePlugin(prepared.Manifest.Id);
            context.Dispose();
            throw;
        }
    }


    private void _RollbackPrepared(PreparedPlugin prepared)
    {
        PluginPageRegistry.RemovePlugin(prepared.Manifest.Id);
        _mixinRuntime?.RollbackAssembly(prepared.Assembly);
        prepared.LoadContext.Unload();
    }


    private async Task<PluginState> _ApplyPluginMixinsAsync(Assembly assembly, PluginManifest manifest, PluginState state,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.MixinConfig)) paths.Add(manifest.MixinConfig);
        paths.AddRange((manifest.MixinConfigs ?? []).Where(path => !string.IsNullOrWhiteSpace(path)));
        foreach (var relativePath in paths.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuration = await _ReadMixinConfigurationAsync(assembly, relativePath, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var result = (_mixinRuntime ??= new MixinRuntime("pcl-lite.plugins"))
                    .ApplyConfiguration(assembly, configuration, manifest.Id + "/" + relativePath);
                LogWrapper.Info("Plugin", $"插件 Mixin 已应用：{manifest.Id}/{relativePath}；" +
                    $"类型={result.MixinCount}；目标方法={result.TargetMethodCount}");
                foreach (var warning in result.Warnings)
                    LogWrapper.Warn("Plugin", $"Mixin 诊断 {manifest.Id}/{relativePath}：{warning}");
            }
            catch (Exception ex) when (!configuration.Required)
            {
                LogWrapper.Warn(ex, "Plugin", $"可选 Mixin 配置失败，跳过：{manifest.Id}/{relativePath}");
            }
        }

        foreach (var feature in manifest.ExperimentalFeatures ?? [])
        {
            if (state.ExperimentalFeatures?.GetValueOrDefault(feature.Id) != true) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var applicationIds = new List<string>();
            try
            {
                foreach (var relativePath in feature.MixinConfigs)
                {
                    var configuration = await _ReadMixinConfigurationAsync(assembly, relativePath, cancellationToken)
                        .ConfigureAwait(false);
                    var applicationId = $"{manifest.Id}/experimental/{feature.Id}/{relativePath}";
                    var result = (_mixinRuntime ??= new MixinRuntime("pcl-lite.plugins"))
                        .ApplyConfiguration(assembly, configuration, applicationId);
                    applicationIds.Add(applicationId);
                    LogWrapper.Info("Plugin", $"实验 Mixin 已应用：{manifest.Id}/{feature.Id}/{relativePath}；" +
                        $"类型={result.MixinCount}；目标方法={result.TargetMethodCount}");
                    foreach (var warning in result.Warnings)
                        LogWrapper.Warn("Plugin", $"实验 Mixin 诊断 {manifest.Id}/{feature.Id}/{relativePath}：{warning}");
                }
            }
            catch (Exception ex)
            {
                foreach (var applicationId in applicationIds)
                    _mixinRuntime?.RollbackApplication(assembly, applicationId);
                var featureStates = new Dictionary<string, bool>(state.ExperimentalFeatures ?? new(), StringComparer.Ordinal)
                {
                    [feature.Id] = false
                };
                var featureErrors = new Dictionary<string, string>(state.ExperimentalFeatureErrors ?? new(), StringComparer.Ordinal)
                {
                    [feature.Id] = ex.Message
                };
                state = state with
                {
                    ExperimentalFeatures = featureStates,
                    ExperimentalFeatureErrors = featureErrors
                };
                await _WriteStateAsync(manifest.Id, state, cancellationToken).ConfigureAwait(false);
                LogWrapper.Warn(ex, "Plugin", $"实验 Mixin 功能已单独关闭：{manifest.Id}/{feature.Id}");
            }
        }
        return state;
    }


    private static async Task<MixinConfiguration> _ReadMixinConfigurationAsync(Assembly assembly, string relativePath,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetDirectoryName(assembly.Location)!, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<MixinConfiguration>(stream,
                       PluginPackage.SerializerOptions, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidDataException($"Mixin configuration is empty: {relativePath}");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new InvalidDataException($"Mixin configuration cannot be read: {relativePath}", ex);
        }
    }


    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var plugin in _running.AsEnumerable().Reverse())
        {
            try { await plugin.Plugin.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", $"插件 {plugin.Manifest.Id} 停止失败"); }
            PluginPageRegistry.RemovePlugin(plugin.Manifest.Id);
            plugin.Context.Dispose();
            _mixinRuntime?.RollbackAssembly(plugin.Assembly);
            plugin.LoadContext.Unload();
        }
        foreach (var prepared in _prepared.Values.ToArray()) _RollbackPrepared(prepared);
        _prepared.Clear();
        _running.Clear();
        if (ReferenceEquals(_current, this)) _current = null;
        _mixinRuntime?.Dispose();
        _mixinRuntime = null;
        if (_ownsHttpClient) _httpClient.Dispose();
    }


    private void _AddInstalled(InstalledPlugin plugin) { lock (_sync) _installed.Add(plugin); }


    private static InstalledPlugin _WithInstallMetadata(InstalledPlugin plugin, PluginState? state) => state is null
        ? plugin
        : plugin with
        {
            InstallSourceType = state.InstallSourceType,
            InstallSourceUrl = state.InstallSourceUrl,
            InstallPackageUrl = state.InstallPackageUrl,
            InstalledPackageSha256 = state.InstalledPackageSha256,
            InstalledAtUtc = state.InstalledAtUtc,
            Signer = state.Signer,
            SignerThumbprint = state.ExpectedSigner,
            SignedAtUtc = state.SignedAtUtc,
            ExperimentalFeatures = _BuildInstalledFeatures(plugin.Manifest, state)
        };


    private static Dictionary<string, bool> _GetDeclaredFeatureStates(PluginManifest manifest, PluginState state)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var feature in manifest.ExperimentalFeatures ?? [])
            result[feature.Id] = state.ExperimentalFeatures?.GetValueOrDefault(feature.Id) ?? false;
        return result;
    }


    private static Dictionary<string, string> _GetDeclaredFeatureErrors(PluginManifest manifest, PluginState state)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var feature in manifest.ExperimentalFeatures ?? [])
            if (state.ExperimentalFeatureErrors?.TryGetValue(feature.Id, out var error) == true)
                result[feature.Id] = error;
        return result;
    }


    private static IReadOnlyList<InstalledPluginFeature> _BuildInstalledFeatures(PluginManifest manifest,
        PluginState? state)
    {
        if (state is null) return [];
        return (manifest.ExperimentalFeatures ?? []).Select(feature => new InstalledPluginFeature(feature,
            state.ExperimentalFeatures?.GetValueOrDefault(feature.Id) ?? false,
            state.ExperimentalFeatureErrors?.GetValueOrDefault(feature.Id))).ToArray();
    }


    private async Task<bool> _IsEnabledAsync(string id, CancellationToken cancellationToken)
    {
        var state = await _ReadStateAsync(id, cancellationToken).ConfigureAwait(false);
        return state.Enabled && !state.UninstallPending;
    }


    private static SemaphoreSlim _GetPluginDataLock(string directory) =>
        PluginDataLocks.GetOrAdd(Path.GetFullPath(directory), _ => new SemaphoreSlim(1, 1));


    private static (long Bytes, int Files) _GetPluginDataUsage(string directory)
    {
        if (!Directory.Exists(directory)) return (0, 0);
        var pending = new Stack<string>();
        pending.Push(directory);
        long bytes = 0;
        var files = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Plugin data cannot contain symbolic links or junctions.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else
                {
                    bytes = checked(bytes + new FileInfo(entry).Length);
                    if (++files > MaximumPluginDataFiles)
                        throw new InvalidDataException("Plugin data directory exceeds the 10,000 file limit.");
                }
            }
        }
        return (bytes, files);
    }


    private async Task<PluginState> _ReadStateAsync(string id, CancellationToken cancellationToken)
    {
        var pendingPath = Path.Combine(_pluginsDirectory, ".pending-install", ".state-" + id + ".json");
        var path = File.Exists(pendingPath) ? pendingPath : Path.Combine(_stateDirectory, id + ".json");
        if (!File.Exists(path)) return new(true, false);
        try
        {
            return JsonSerializer.Deserialize<PluginState>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false))
                   ?? new PluginState(true, false);
        }
        catch { return new(true, false); }
    }


    private async Task<string?> _ReadInstalledVersionAsync(string id, CancellationToken cancellationToken)
    {
        var pendingDirectory = Path.Combine(_pluginsDirectory, ".pending-install", id);
        var directory = Directory.Exists(pendingDirectory)
            ? pendingDirectory
            : Path.Combine(_pluginsDirectory, id);
        if (!Directory.Exists(directory)) return null;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Plugin installation directory cannot be a symbolic link or junction.");
        var manifestPath = Path.Combine(directory, PluginPackage.ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("Installed plugin manifest is missing; refusing to replace an unreadable plugin.");
        if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Installed plugin manifest cannot be a symbolic link.");
        var bytes = await _ReadBoundedFileAsync(manifestPath, PluginPackage.MaximumManifestBytes, cancellationToken)
            .ConfigureAwait(false);
        var manifest = PluginPackage.DeserializeManifest(bytes);
        if (!string.Equals(manifest.Id, id, StringComparison.Ordinal) ||
            !PluginVersionRange.IsValidVersion(manifest.Version))
            throw new InvalidDataException("Installed plugin manifest has an invalid id or version.");
        return manifest.Version;
    }


    private async Task _WriteStateAsync(string id, PluginState state, CancellationToken cancellationToken)
    {
        var pendingPath = Path.Combine(_pluginsDirectory, ".pending-install", ".state-" + id + ".json");
        var path = File.Exists(pendingPath) ? pendingPath : Path.Combine(_stateDirectory, id + ".json");
        var temp = path + ".tmp";
        Directory.CreateDirectory(_stateDirectory);
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }


    private static async Task _WritePendingStateAsync(string path, PluginState state,
        CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }


    private async Task<PluginGitHubProvenance> _VerifyGitHubReleaseAsync(string packagePath, string assetUrl,
        PluginPackageCheck check, CancellationToken cancellationToken)
    {
        if (!_TryParseGitHubReleaseAsset(assetUrl, out var asset))
            throw new InvalidDataException("The package GitHub release asset URL is invalid.");
        var apiUri = new Uri($"https://api.github.com/repos/{asset.Owner}/{asset.Repository}/releases/tags/{Uri.EscapeDataString(asset.Tag)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
        request.Headers.UserAgent.ParseAdd("PCL-Lite-PluginManager/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? apiUri;
        if (finalUri.Scheme != Uri.UriSchemeHttps ||
            !finalUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub release API redirected to an unexpected endpoint.");
        var releaseBytes = await _ReadBoundedContentAsync(response.Content, MaximumGitHubApiResponseBytes,
            cancellationToken).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize<GitHubReleaseResponse>(releaseBytes, GitHubJsonOptions)
                      ?? throw new InvalidDataException("GitHub returned an empty release response.");
        if (!string.Equals(release.TagName, asset.Tag, StringComparison.Ordinal))
            throw new InvalidDataException("GitHub release tag does not match the package URL.");
        if (release.Assets is null || release.Assets.Count > 1000)
            throw new InvalidDataException("GitHub release contains an invalid number of assets.");
        var releaseAsset = release.Assets.FirstOrDefault(item => item is not null &&
            string.Equals(item.Name, asset.AssetName, StringComparison.Ordinal));
        if (releaseAsset is null)
            throw new InvalidDataException("GitHub release does not contain the named plugin package asset.");
        if (string.IsNullOrWhiteSpace(releaseAsset.Digest) ||
            !releaseAsset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub did not provide a SHA-256 digest for this release asset.");
        await using var packageStream = File.OpenRead(packagePath);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false));
        var releaseHash = releaseAsset.Digest["sha256:".Length..];
        if (!string.Equals(actualHash, releaseHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin package does not match the GitHub release asset SHA-256.");
        var publishedAt = release.PublishedAt?.ToUniversalTime()
                          ?? throw new InvalidDataException("GitHub release is missing its publish timestamp.");
        return new PluginGitHubProvenance($"{asset.Owner}/{asset.Repository}", release.TagName, publishedAt,
            actualHash.ToLowerInvariant(), check.SignedAtUtc is { } signedAt && signedAt > publishedAt);
    }


    private static bool _TryParseGitHubReleaseAsset(string? value, out GitHubReleaseAssetReference asset)
    {
        asset = default;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length != 6 || !segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase) ||
            !segments[3].Equals("download", StringComparison.OrdinalIgnoreCase)) return false;
        var owner = segments[0];
        var repository = segments[1];
        var tag = segments[4];
        var assetName = segments[5];
        static bool IsSafeRepositorySegment(string segment) => !string.IsNullOrWhiteSpace(segment) &&
            segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
        if (!IsSafeRepositorySegment(owner) || !IsSafeRepositorySegment(repository) || owner is "." or ".." ||
            repository is "." or ".." ||
            string.IsNullOrWhiteSpace(tag) || tag.Contains('\\') || tag.Contains('\0') ||
            string.IsNullOrWhiteSpace(assetName) || assetName.Contains('/') || assetName.Contains('\\') ||
            !assetName.EndsWith(PluginPackage.Extension, StringComparison.OrdinalIgnoreCase)) return false;
        asset = new(owner, repository, tag, assetName);
        return true;
    }


    private readonly record struct GitHubReleaseAssetReference(string Owner, string Repository, string Tag, string AssetName);

    private sealed record GitHubReleaseResponse(string TagName, DateTimeOffset? PublishedAt, IReadOnlyList<GitHubReleaseAssetResponse> Assets);

    private sealed record GitHubReleaseAssetResponse(string Name, string? Digest);


    private static async Task<byte[]> _ReadBoundedFileAsync(string path, long maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > maximumBytes) throw new InvalidDataException("File exceeds its allowed size limit.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream((int)Math.Min(maximumBytes, info.Length));
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("File exceeds its allowed size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }


    private static async Task<byte[]> _ReadBoundedContentAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
            throw new InvalidDataException($"HTTP response exceeds the {maximumBytes}-byte limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, content.Headers.ContentLength is { } size
            ? (int)size
            : 16 * 1024));
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException($"HTTP response exceeds the {maximumBytes}-byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }


    private void _CleanupPendingDeletes()
    {
        var pendingDeleteDirectory = Path.Combine(_pluginsDirectory, PendingDeleteDirectoryName);
        try { if (Directory.Exists(pendingDeleteDirectory)) Directory.Delete(pendingDeleteDirectory, recursive: true); }
        catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", "无法清理待卸载插件缓存"); }
        try { if (Directory.Exists(Path.Combine(_pluginsDirectory, ".market-download"))) Directory.Delete(Path.Combine(_pluginsDirectory, ".market-download"), recursive: true); }
        catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", "无法清理插件市场下载缓存"); }
        try { if (Directory.Exists(Path.Combine(_pluginsDirectory, ".package-snapshot"))) Directory.Delete(Path.Combine(_pluginsDirectory, ".package-snapshot"), recursive: true); }
        catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", "无法清理插件包临时缓存"); }
    }


    private void _ApplyPendingChanges()
    {
        var pendingInstall = Path.Combine(_pluginsDirectory, ".pending-install");
        if (Directory.Exists(pendingInstall))
        {
            var pendingFailure = false;
            foreach (var staged in Directory.EnumerateDirectories(pendingInstall)
                         .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)))
            {
                var id = Path.GetFileName(staged);
                try
                {
                    _ValidateId(id);
                    var target = Path.Combine(_pluginsDirectory, id);
                    var backup = Path.Combine(_pluginsDirectory, ".replace-backup-" + id);
                    var statePath = Path.Combine(_stateDirectory, id + ".json");
                    var stateBackup = statePath + ".rollback";
                    var stateWasPresent = File.Exists(statePath);
                    var stateUpdated = false;
                    var movedOld = Directory.Exists(backup);
                    var movedNew = false;
                    try
                    {
                        if (Directory.Exists(target))
                        {
                            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                            Directory.Move(target, backup);
                            movedOld = true;
                        }
                        if (stateWasPresent) File.Copy(statePath, stateBackup, overwrite: true);
                        var pendingState = Path.Combine(pendingInstall, ".state-" + id + ".json");
                        if (File.Exists(pendingState))
                        {
                            var pendingPluginState = JsonSerializer.Deserialize<PluginState>(File.ReadAllBytes(pendingState))
                                                     ?? throw new InvalidDataException("Pending plugin state is invalid.");
                            if (!_InstalledDirectoryMatchesState(staged, pendingPluginState))
                                throw new InvalidDataException("Pending plugin files do not match their staged state.");
                        }
                        Directory.Move(staged, target);
                        movedNew = true;
                        if (File.Exists(pendingState))
                        {
                            var desiredState = JsonSerializer.Deserialize<PluginState>(File.ReadAllBytes(pendingState))
                                               ?? throw new InvalidDataException("Pending plugin state is invalid.");
                            var stateTemp = statePath + ".tmp";
                            File.WriteAllText(stateTemp, JsonSerializer.Serialize(desiredState));
                            File.Move(stateTemp, statePath, overwrite: true);
                            stateUpdated = true;
                        }
                    }
                    catch
                    {
                        if (movedNew && Directory.Exists(target) && !Directory.Exists(staged))
                            Directory.Move(target, staged);
                        if (movedOld && Directory.Exists(backup) && !Directory.Exists(target))
                            Directory.Move(backup, target);
                        if (stateUpdated)
                        {
                            if (stateWasPresent && File.Exists(stateBackup)) File.Copy(stateBackup, statePath, overwrite: true);
                            else if (!stateWasPresent && File.Exists(statePath)) File.Delete(statePath);
                        }
                        throw;
                    }
                    var committedState = Path.Combine(pendingInstall, ".state-" + id + ".json");
                    try { if (File.Exists(committedState)) File.Delete(committedState); }
                    catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", $"无法清理已提交的插件状态：{id}"); }
                    if (Directory.Exists(backup))
                    {
                        try { Directory.Delete(backup, recursive: true); }
                        catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", $"无法清理旧插件备份：{id}"); }
                    }
                    if (File.Exists(stateBackup)) File.Delete(stateBackup);
                }
                catch (Exception ex)
                {
                    pendingFailure = true;
                    LogWrapper.Warn(ex, "Plugin", $"应用插件更新失败：{id}；将在下次启动重试");
                }
            }
            foreach (var pendingState in Directory.EnumerateFiles(pendingInstall, ".state-*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(pendingState)[".state-".Length..];
                try
                {
                    _ValidateId(id);
                    var staged = Path.Combine(pendingInstall, id);
                    if (Directory.Exists(staged)) continue;
                    var target = Path.Combine(_pluginsDirectory, id);
                    var backup = Path.Combine(_pluginsDirectory, ".replace-backup-" + id);
                    var state = JsonSerializer.Deserialize<PluginState>(File.ReadAllBytes(pendingState));
                    if (state is not null && Directory.Exists(target) && _InstalledDirectoryMatchesState(target, state))
                    {
                        File.Move(pendingState, Path.Combine(_stateDirectory, id + ".json"), overwrite: true);
                        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                    }
                    else
                    {
                        if (Directory.Exists(backup))
                        {
                            if (Directory.Exists(target))
                                Directory.Move(target, Path.Combine(pendingInstall, ".rejected-" + id + "-" + Guid.NewGuid().ToString("N")));
                            Directory.Move(backup, target);
                        }
                        File.Delete(pendingState);
                    }
                }
                catch (Exception ex)
                {
                    pendingFailure = true;
                    LogWrapper.Warn(ex, "Plugin", $"恢复插件状态失败：{id}；将在下次启动重试");
                }
            }
            if (!pendingFailure)
            {
                try { Directory.Delete(pendingInstall, recursive: true); } catch { }
            }
        }

        foreach (var backup in Directory.EnumerateDirectories(_pluginsDirectory, ".replace-backup-*"))
        {
            var id = Path.GetFileName(backup)[".replace-backup-".Length..];
            try
            {
                _ValidateId(id);
                var target = Path.Combine(_pluginsDirectory, id);
                if (Directory.Exists(target)) Directory.Delete(backup, recursive: true);
                else Directory.Move(backup, target);
            }
            catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", $"恢复或清理插件备份失败：{id}"); }
        }

        foreach (var statePath in Directory.EnumerateFiles(_stateDirectory, "*.json"))
        {
            try
            {
                var state = JsonSerializer.Deserialize<PluginState>(File.ReadAllBytes(statePath));
                if (state?.UninstallPending != true) continue;
                var id = Path.GetFileNameWithoutExtension(statePath);
                _ValidateId(id);
                var target = Path.Combine(_pluginsDirectory, id);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                File.Delete(statePath);
            }
            catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", $"应用插件卸载失败：{Path.GetFileName(statePath)}"); }
        }
    }


    private static bool _InstalledDirectoryMatchesState(string directory, PluginState state)
    {
        if (string.IsNullOrWhiteSpace(state.ExpectedHash)) return false;
        try
        {
            var check = PluginPackage.InspectDirectoryAsync(directory, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (check.Error is not null || !check.IsValid ||
                !string.Equals(state.ExpectedHash, check.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(check.Manifest.Id, Path.GetFileName(directory), StringComparison.Ordinal) ||
                !string.Equals(check.Manifest.Version, state.InstalledVersion, StringComparison.Ordinal)) return false;
            return check.IsSigned
                ? state.ExpectedSigner is not null && string.Equals(check.SignerThumbprint, state.ExpectedSigner,
                    StringComparison.OrdinalIgnoreCase)
                : state.UnsignedApproved && state.ExpectedSigner is null;
        }
        catch { return false; }
    }


    private static void _ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-')))
            throw new ArgumentException("Invalid plugin id.", nameof(id));
    }


    private sealed record PluginState(bool Enabled, bool UnsignedApproved, bool UninstallPending = false,
        string? ExpectedHash = null, string? ExpectedSigner = null, string? InstalledVersion = null,
        string? InstallSourceType = null, string? InstallSourceUrl = null, string? InstallPackageUrl = null,
        string? InstalledPackageSha256 = null, DateTimeOffset? InstalledAtUtc = null,
        string? Signer = null, DateTimeOffset? SignedAtUtc = null)
    {
        public Dictionary<string, bool> ExperimentalFeatures { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ExperimentalFeatureErrors { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record RunningPlugin(IPclPlugin Plugin, PluginContext Context, PluginAssemblyLoadContext LoadContext,
        PluginManifest Manifest, Assembly Assembly);

    private sealed record PreparedPlugin(PluginManifest Manifest, PluginAssemblyLoadContext LoadContext,
        Assembly Assembly, Type EntryType);


    private sealed class PluginAssemblyLoadContext(string pluginDirectory, string entryAssemblyPath,
        IReadOnlyDictionary<string, Assembly> sharedDependencies) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(Path.GetFullPath(entryAssemblyPath));
        public IReadOnlyDictionary<string, Assembly> SharedDependencies { get; } = sharedDependencies;
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(IPclPlugin).Assembly.GetName().Name) return typeof(IPclPlugin).Assembly;
            if (assemblyName.Name is not null && SharedDependencies.TryGetValue(assemblyName.Name, out var sharedAssembly))
            {
                if (!AssemblyName.ReferenceMatchesDefinition(assemblyName, sharedAssembly.GetName()))
                    throw new FileLoadException($"Plugin dependency assembly identity does not match: {assemblyName.FullName}.");
                return sharedAssembly;
            }
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

}
