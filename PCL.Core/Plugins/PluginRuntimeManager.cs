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

public sealed record InstalledPlugin(PluginManifest Manifest, string Directory, bool Enabled, bool Running,
    string? Error = null, bool PendingRestart = false)
{
    public string? InstallSourceType { get; init; }
    public string? InstallSourceUrl { get; init; }
    public string? InstallPackageUrl { get; init; }
    public string? InstalledPackageSha256 { get; init; }
    public DateTimeOffset? InstalledAtUtc { get; init; }
    public string? Signer { get; init; }
    public string? SignerThumbprint { get; init; }
    public DateTimeOffset? SignedAtUtc { get; init; }
    public IReadOnlyList<InstalledPluginFeature> ExperimentalFeatures { get; init; } = [];
}

public sealed record InstalledPluginFeature(PluginExperimentalFeature Manifest, bool Enabled, string? Error = null);

public sealed record PluginMarketEntry(string Id, string Name, string Version, string Author, string Description,
    string PackageUrl, string? Website = null, string? Sha256 = null)
{
    [JsonIgnore]
    public string? SourceUrl { get; init; }
}

public sealed record PluginMarketIndex(int SchemaVersion, IReadOnlyList<PluginMarketEntry> Plugins);

public enum PluginMarketUpdateKind { NotInstalled, Current, UpdateAvailable, DowngradeAvailable }

public sealed record PluginMarketUpdateCandidate(PluginMarketEntry Entry, InstalledPlugin? Installed,
    PluginMarketUpdateKind Kind);

public sealed record PluginPage(string PluginId, string PageId, string Title, Func<System.Windows.FrameworkElement> CreateView);

/// <summary>Plugin-owned pages exposed to the host UI.</summary>
public static class PluginPageRegistry
{
    private static readonly ConcurrentDictionary<string, PluginPage> Pages = new(StringComparer.Ordinal);
    public static event Action? Changed;
    public static IReadOnlyCollection<PluginPage> GetAll() => Pages.Values.ToArray();
    public static void Register(PluginPage page)
    {
        if (!Pages.TryAdd($"{page.PluginId}/{page.PageId}", page))
            throw new InvalidOperationException($"Plugin page is already registered: {page.PluginId}/{page.PageId}");
        Changed?.Invoke();
    }
    public static void RemovePlugin(string pluginId)
    {
        foreach (var key in Pages.Keys.Where(key => key.StartsWith(pluginId + "/", StringComparison.Ordinal)))
            Pages.TryRemove(key, out _);
        Changed?.Invoke();
    }
}

/// <summary>Callbacks supplied by the WPF host for launcher-specific APIs.</summary>
public static class PluginHostBridge
{
    public static Func<CancellationToken, Task<IReadOnlyList<PluginGameInstance>>>? GetInstancesAsync { get; set; }
    public static Func<string, CancellationToken, Task<bool>>? LaunchInstanceAsync { get; set; }
    public static Action<string, PluginNotificationKind>? ShowNotification { get; set; }
    public static Func<string, string, CancellationToken, Task<bool>>? ConfirmAsync { get; set; }
    public static Action<Uri>? OpenUrl { get; set; }
    public static Action<string>? OpenPath { get; set; }
}

public sealed class PluginRuntimeManager : IAsyncDisposable
{
    private const string PendingDeleteDirectoryName = ".pending-delete";
    private const long MaximumPluginDataBytes = 256L * 1024 * 1024;
    private const int MaximumPluginDataFiles = 10_000;
    private const long MaximumPluginStoredFileBytes = 64L * 1024 * 1024;
    private const long MaximumPluginDownloadBytes = MaximumPluginStoredFileBytes;
    private const long MaximumPluginHttpBodyBytes = 16L * 1024 * 1024;
    private const int MaximumMarketIndexBytes = 2 * 1024 * 1024;
    private const int MaximumMarketEntries = 1000;
    private const int MaximumGitHubApiResponseBytes = 2 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PluginDataLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _pluginsDirectory;
    private readonly string _stateDirectory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly List<RunningPlugin> _running = [];
    private readonly List<InstalledPlugin> _installed = [];
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Guid, Func<object, CancellationToken, Task>>> _handlers = new();
    private MixinRuntime? _mixinRuntime;
    private readonly Dictionary<string, PreparedPlugin> _prepared = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private bool _disposed;
    private static readonly JsonSerializerOptions GitHubJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public PluginRuntimeManager(string? pluginsDirectory = null, HttpClient? httpClient = null)
    {
        _pluginsDirectory = Path.GetFullPath(pluginsDirectory ?? Path.Combine(Paths.Data, "Plugins"));
        _stateDirectory = Path.Combine(_pluginsDirectory, ".plugin-state");
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Directory.CreateDirectory(_pluginsDirectory);
        Directory.CreateDirectory(_stateDirectory);
        _ApplyPendingChanges();
        _CleanupPendingDeletes();
    }

    public string PluginsDirectory => _pluginsDirectory;
    public static bool IsSafeMode =>
        string.Equals(Environment.GetEnvironmentVariable("PCL_LITE_SAFE_MODE"), "1", StringComparison.Ordinal) ||
        Basics.CommandLineArguments.Any(argument => string.Equals(argument, "--safe-mode", StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<InstalledPlugin> GetInstalled() { lock (_sync) return _installed.ToArray(); }
    public IReadOnlyList<InstalledPlugin> GetMarketInstallState(IReadOnlyList<PluginMarketEntry> market)
    {
        var installed = GetInstalled().ToDictionary(item => item.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        return market.Where(item => installed.ContainsKey(item.Id))
            .Select(item => installed[item.Id]).ToArray();
    }

    /// <summary>Compares a validated market catalog with installed versions without downloading packages.</summary>
    public IReadOnlyList<PluginMarketUpdateCandidate> GetMarketUpdateCandidates(IReadOnlyList<PluginMarketEntry> market)
    {
        var installed = GetInstalled().ToDictionary(item => item.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        return market.Select(entry =>
        {
            if (!installed.TryGetValue(entry.Id, out var current))
                return new PluginMarketUpdateCandidate(entry, null, PluginMarketUpdateKind.NotInstalled);
            if (!PluginVersionRange.TryCompareVersions(entry.Version, current.Manifest.Version, out var comparison))
                throw new InvalidDataException($"Invalid market or installed plugin version: {entry.Id}.");
            var sourceChanged = !string.Equals(current.InstallSourceType, "market", StringComparison.Ordinal) ||
                                (entry.SourceUrl is not null &&
                                 !string.Equals(current.InstallSourceUrl, entry.SourceUrl, StringComparison.Ordinal));
            var packageChanged = entry.Sha256 is not null && current.InstalledPackageSha256 is not null &&
                                 !string.Equals(entry.Sha256, current.InstalledPackageSha256,
                                     StringComparison.OrdinalIgnoreCase);
            var kind = comparison > 0 || (comparison == 0 && (sourceChanged || packageChanged))
                ? PluginMarketUpdateKind.UpdateAvailable
                : comparison < 0 ? PluginMarketUpdateKind.DowngradeAvailable : PluginMarketUpdateKind.Current;
            return new PluginMarketUpdateCandidate(entry, current, kind);
        }).ToArray();
    }

    public async Task<PluginMarketIndex> GetMarketAsync(Uri indexUri, CancellationToken cancellationToken = default)
    {
        if (indexUri is null || !indexUri.IsAbsoluteUri || indexUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Plugin market index must use an absolute HTTPS URL.");
        using var response = await _httpClient.GetAsync(indexUri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? indexUri;
        if (finalUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Plugin market index redirected away from HTTPS.");
        var bytes = await _ReadBoundedContentAsync(response.Content, MaximumMarketIndexBytes, cancellationToken)
            .ConfigureAwait(false);
        var index = JsonSerializer.Deserialize<PluginMarketIndex>(bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 32 })
                    ?? throw new InvalidDataException("Plugin market index is empty.");
        if (index.SchemaVersion != 1) throw new InvalidDataException("Plugin market schema is not supported.");
        if (index.Plugins is null || index.Plugins.Count > MaximumMarketEntries)
            throw new InvalidDataException($"Plugin market index may contain at most {MaximumMarketEntries} entries.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<PluginMarketEntry>(index.Plugins.Count);
        foreach (var entry in index.Plugins)
        {
            if (entry is null) throw new InvalidDataException("Plugin market contains an empty entry.");
            try { _ValidateId(entry.Id); }
            catch (ArgumentException ex) { throw new InvalidDataException("Plugin market contains an invalid plugin id.", ex); }
            if (!ids.Add(entry.Id)) throw new InvalidDataException($"Duplicate plugin market id: {entry.Id}");
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 128 ||
                string.IsNullOrWhiteSpace(entry.Author) || entry.Author.Length > 128 ||
                string.IsNullOrWhiteSpace(entry.Version) || entry.Version.Length > 64 ||
                entry.Description is null || entry.Description.Length > 4096 ||
                string.IsNullOrWhiteSpace(entry.PackageUrl) || entry.PackageUrl.Length > 2048 ||
                (entry.Website?.Length ?? 0) > 2048)
                throw new InvalidDataException($"Plugin market entry has invalid or oversized metadata: {entry.Id}");
            if (!Uri.TryCreate(entry.PackageUrl, UriKind.Absolute, out var packageUri) || packageUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Plugin package URL must use HTTPS: {entry.Id}");
            if (entry.Website is not null &&
                (!Uri.TryCreate(entry.Website, UriKind.Absolute, out var websiteUri) || websiteUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException($"Plugin website URL must use HTTPS: {entry.Id}");
            if (string.IsNullOrWhiteSpace(entry.Sha256) || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException($"Plugin market entry must include a SHA-256 digest: {entry.Id}");
            if (!PluginVersionRange.IsValidVersion(entry.Version))
                throw new InvalidDataException($"Plugin market entry has an invalid SemVer version: {entry.Id}");
            if (!_TryParseGitHubReleaseAsset(entry.PackageUrl, out _))
                throw new InvalidDataException($"Plugin market package must be a GitHub release asset: {entry.Id}");
            var sourceUri = new UriBuilder(finalUri) { Query = string.Empty, Fragment = string.Empty }.Uri;
            entries.Add(entry with { SourceUrl = sourceUri.AbsoluteUri });
        }
        return index with { Plugins = entries };
    }

    public async Task<PluginPackageCheck> InstallFromMarketAsync(PluginMarketEntry entry,
        Func<PluginPackageCheck, CancellationToken, Task<bool>> confirmInstallation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Sha256) || entry.Sha256.Length != 64 ||
            entry.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Plugin market entry must include a 64-character SHA-256 digest.");
        if (!Uri.TryCreate(entry.PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Plugin package URL must use HTTPS.");
        var downloadStagingDirectory = Path.Combine(_pluginsDirectory, ".market-download");
        Directory.CreateDirectory(downloadStagingDirectory);
        var path = Path.Combine(downloadStagingDirectory, "plugin-market-" + Guid.NewGuid().ToString("N") + PluginPackage.Extension);
        try
        {
            using (var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var finalUri = response.RequestMessage?.RequestUri ?? uri;
                if (finalUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("Plugin package download redirected away from HTTPS.");
                if (response.Content.Headers.ContentLength > PluginPackage.MaximumPackageBytes)
                    throw new InvalidDataException("Plugin package exceeds the download size limit.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > PluginPackage.MaximumPackageBytes)
                        throw new InvalidDataException("Plugin package exceeds the download size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            if (!string.IsNullOrWhiteSpace(entry.Sha256))
            {
                var expected = entry.Sha256.Trim();
                await using var packageStream = File.OpenRead(path);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken)
                    .ConfigureAwait(false));
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Plugin package hash does not match the market index.");
            }
            var check = await PluginPackage.InspectAsync(path, cancellationToken).ConfigureAwait(false);
            if (check.Error is not null || !check.IsValid)
                throw new InvalidDataException(check.Error ?? "Plugin package is invalid.");
            if (!string.Equals(check.Manifest.Id, entry.Id, StringComparison.Ordinal) ||
                !string.Equals(check.Manifest.Version, entry.Version, StringComparison.Ordinal))
                throw new InvalidDataException("Downloaded plugin does not match the selected market entry.");
            var provenanceUrl = entry.PackageUrl;
            if (_TryParseGitHubReleaseAsset(provenanceUrl, out _))
                check = check with { GitHubProvenance = await _VerifyGitHubReleaseAsync(path, provenanceUrl, check, cancellationToken).ConfigureAwait(false) };
            var installedCheck = await _InstallAsync(path, (current, token) => confirmInstallation(current with
            {
                GitHubProvenance = check.GitHubProvenance
            }, token), "market", entry.SourceUrl, entry.PackageUrl, entry.Sha256, cancellationToken)
                .ConfigureAwait(false);
            return installedCheck with { GitHubProvenance = check.GitHubProvenance };
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    public async Task<PluginPackageCheck> InstallAsync(string packagePath,
        Func<PluginPackageCheck, CancellationToken, Task<bool>> confirmInstallation,
        CancellationToken cancellationToken = default)
    {
        return await _InstallAsync(packagePath, confirmInstallation, "local", null, null, null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PluginPackageCheck> _InstallAsync(string packagePath,
        Func<PluginPackageCheck, CancellationToken, Task<bool>> confirmInstallation,
        string sourceType, string? sourceUrl, string? packageUrl, string? expectedPackageSha256,
        CancellationToken cancellationToken)
    {
        var snapshotDirectory = Path.Combine(_pluginsDirectory, ".package-snapshot");
        Directory.CreateDirectory(snapshotDirectory);
        var snapshotPath = Path.Combine(snapshotDirectory, Guid.NewGuid().ToString("N") + PluginPackage.Extension);
        try
        {
            var sourceInfo = new FileInfo(packagePath);
            if (!sourceInfo.Exists) throw new FileNotFoundException("Plugin package does not exist.", packagePath);
            if (sourceInfo.Length > PluginPackage.MaximumPackageBytes)
                throw new InvalidDataException("Package exceeds the 100 MiB limit.");
            await using (var source = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var snapshot = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    copied += read;
                    if (copied > PluginPackage.MaximumPackageBytes)
                        throw new InvalidDataException("Package exceeds the 100 MiB limit.");
                    await snapshot.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            return await _InstallSnapshotAsync(snapshotPath, confirmInstallation, sourceType, sourceUrl,
                packageUrl, expectedPackageSha256,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(snapshotPath)) File.Delete(snapshotPath); } catch { }
        }
    }

    private async Task<PluginPackageCheck> _InstallSnapshotAsync(string packagePath,
        Func<PluginPackageCheck, CancellationToken, Task<bool>> confirmInstallation,
        string sourceType, string? sourceUrl, string? packageUrl, string? expectedPackageSha256,
        CancellationToken cancellationToken)
    {
        var check = await PluginPackage.InspectAsync(packagePath, cancellationToken).ConfigureAwait(false);
        if (check.Error is not null || !check.IsValid) throw new InvalidDataException(check.Error ?? "Plugin package is invalid.");
        var provenanceUrl = packageUrl ?? check.Manifest.ReleaseAssetUrl;
        if (!string.IsNullOrWhiteSpace(provenanceUrl))
        {
            if (!_TryParseGitHubReleaseAsset(provenanceUrl, out _))
                throw new InvalidDataException("The package GitHub release asset URL is invalid.");
            check = check with
            {
                GitHubProvenance = await _VerifyGitHubReleaseAsync(packagePath, provenanceUrl,
                    check, cancellationToken).ConfigureAwait(false)
            };
        }
        if (check.Manifest.PclCoreVersion is { } coreVersionRange &&
            !PluginVersionRange.Satisfies(Basics.VersionName, coreVersionRange))
            check = check with
            {
                CompatibilityWarning = $"Plugin supports PCL Core {coreVersionRange}; current version is {Basics.VersionName}."
            };
        var previousState = await _ReadStateAsync(check.Manifest.Id, cancellationToken).ConfigureAwait(false);
        await using (var packageStream = File.OpenRead(packagePath))
        {
            check = check with
            {
                PackageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken)
                    .ConfigureAwait(false)),
                PreviousVersion = await _ReadInstalledVersionAsync(check.Manifest.Id, cancellationToken)
                    .ConfigureAwait(false) ?? previousState.InstalledVersion,
                PreviousInstallSourceType = previousState.InstallSourceType,
                PreviousInstallSourceUrl = previousState.InstallSourceUrl,
                PreviousPackageSha256 = previousState.InstalledPackageSha256,
                CurrentInstallSourceType = sourceType,
                CurrentInstallSourceUrl = sourceUrl,
                CurrentInstallPackageUrl = packageUrl
            };
        }
        if (expectedPackageSha256 is not null && !string.Equals(expectedPackageSha256,
                check.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin package hash does not match its source metadata.");
        if (previousState.ExpectedSigner is not null)
            check = check with { PreviousSignerThumbprint = previousState.ExpectedSigner };
        if (!await confirmInstallation(check, cancellationToken).ConfigureAwait(false))
            throw new OperationCanceledException("Plugin installation was declined.", cancellationToken);

        var files = (await PluginPackage.ReadPackageFilesAsync(packagePath, cancellationToken).ConfigureAwait(false))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        Directory.CreateDirectory(Path.Combine(_pluginsDirectory, ".pending-install"));
        var pendingRoot = Path.Combine(_pluginsDirectory, ".pending-install");
        var staging = Path.Combine(pendingRoot, ".stage-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(pendingRoot, check.Manifest.Id);
        var backup = Path.Combine(pendingRoot, ".backup-" + Guid.NewGuid().ToString("N"));
        var movedPreviousTarget = false;
        var movedNewTarget = false;
        var transactionCompleted = false;
        var pendingStatePath = Path.Combine(pendingRoot, ".state-" + check.Manifest.Id + ".json");
        var pendingStateBackupPath = pendingStatePath + ".rollback";
        var hadPendingState = File.Exists(pendingStatePath);
        var featureStates = _GetDeclaredFeatureStates(check.Manifest, previousState);
        var featureErrors = _GetDeclaredFeatureErrors(check.Manifest, previousState);
        var nextState = new PluginState(check.PreviousVersion is null || previousState.Enabled, !check.IsSigned,
            ExpectedHash: check.ContentHash, ExpectedSigner: check.SignerThumbprint,
            InstalledVersion: check.Manifest.Version, InstallSourceType: sourceType,
            InstallSourceUrl: sourceUrl, InstallPackageUrl: packageUrl,
            InstalledPackageSha256: check.PackageSha256, InstalledAtUtc: DateTimeOffset.UtcNow,
            Signer: check.Signer, SignedAtUtc: check.SignedAtUtc)
        {
            ExperimentalFeatures = featureStates,
            ExperimentalFeatureErrors = featureErrors
        };
        try
        {
            if (hadPendingState) File.Copy(pendingStatePath, pendingStateBackupPath, overwrite: true);
            Directory.CreateDirectory(staging);
            foreach (var (relative, contents) in files)
            {
                PluginPackage.ValidateRelativePath(relative);
                var destination = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe package path: {relative}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, contents, cancellationToken).ConfigureAwait(false);
            }
            await _WritePendingStateAsync(pendingStatePath, nextState, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                movedPreviousTarget = true;
            }
            Directory.Move(staging, target);
            movedNewTarget = true;
            if (movedPreviousTarget)
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", "无法清理旧的待安装插件目录"); }
            }
            lock (_sync)
            {
                _installed.RemoveAll(item => item.Manifest.Id == check.Manifest.Id);
                _installed.Add(new(check.Manifest, Path.Combine(_pluginsDirectory, check.Manifest.Id), nextState.Enabled, false,
                    null, true)
                {
                    InstallSourceType = sourceType,
                    InstallSourceUrl = sourceUrl,
                    InstallPackageUrl = packageUrl,
                    InstalledPackageSha256 = check.PackageSha256,
                    InstalledAtUtc = nextState.InstalledAtUtc,
                    Signer = nextState.Signer,
                    SignerThumbprint = nextState.ExpectedSigner,
                    SignedAtUtc = nextState.SignedAtUtc,
                    ExperimentalFeatures = _BuildInstalledFeatures(check.Manifest, nextState)
                });
            }
            transactionCompleted = true;
            return check;
        }
        catch
        {
            if (movedNewTarget && Directory.Exists(target))
            {
                try { Directory.Delete(target, recursive: true); }
                catch (Exception restoreError) { LogWrapper.Error(restoreError, "Plugin", "无法清理失败的插件安装目录"); }
            }
            if (movedPreviousTarget && Directory.Exists(backup))
            {
                try { Directory.Move(backup, target); }
                catch (Exception restoreError) { LogWrapper.Error(restoreError, "Plugin", "无法恢复旧的待安装插件目录"); }
            }
            if (hadPendingState && File.Exists(pendingStateBackupPath))
            {
                try { File.Copy(pendingStateBackupPath, pendingStatePath, overwrite: true); }
                catch (Exception restoreError) { LogWrapper.Error(restoreError, "Plugin", "无法恢复先前插件状态暂存文件"); }
            }
            else if (File.Exists(pendingStatePath))
            {
                try { File.Delete(pendingStatePath); }
                catch (Exception restoreError) { LogWrapper.Error(restoreError, "Plugin", "无法移除失败安装的插件状态暂存文件"); }
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", "无法清理插件暂存目录"); }
            }
            if (transactionCompleted && Directory.Exists(backup))
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", "无法清理插件安装备份目录"); }
            }
            if (transactionCompleted || !movedNewTarget)
            {
                try { if (File.Exists(pendingStateBackupPath)) File.Delete(pendingStateBackupPath); }
                catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", "无法清理插件状态备份文件"); }
            }
        }
    }

    /// <summary>Verifies enabled packages and applies their compiled patches before the main window is created.</summary>
    public async Task PrepareEnabledAsync(CancellationToken cancellationToken = default)
    {
        _current = this;
        Directory.CreateDirectory(_pluginsDirectory);
        lock (_sync) _installed.Clear();
        var candidates = new List<PluginDependencyCandidate>();
        var statesById = new Dictionary<string, PluginState>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(_pluginsDirectory)
                     .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginManifest? manifest = null;
            PluginState? pluginState = null;
            try
            {
                var files = await ReadInstalledFilesAsync(directory, cancellationToken).ConfigureAwait(false);
                if (!files.TryGetValue(PluginPackage.ManifestFileName, out var manifestBytes))
                    throw new InvalidDataException("Plugin manifest is missing.");
                manifest = PluginPackage.DeserializeManifest(manifestBytes);
                if (!string.Equals(manifest.Id, Path.GetFileName(directory), StringComparison.Ordinal))
                    throw new InvalidDataException("Plugin id does not match its installation directory.");
                var error = PluginPackage.ValidateManifest(manifest, files.Keys);
                if (error is not null) throw new InvalidDataException(error);
                if (manifest.PclCoreVersion is { } coreVersionRange &&
                    !PluginVersionRange.Satisfies(Basics.VersionName, coreVersionRange))
                    throw new InvalidDataException(
                        $"Plugin supports PCL Core {coreVersionRange}; current version is {Basics.VersionName}.");
                error = PluginPackage.ValidateMixinConfigurations(manifest, files);
                if (error is not null) throw new InvalidDataException(error);
                var state = await _ReadStateAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
                pluginState = state;
                var signaturePath = Path.Combine(directory, PluginPackage.SignatureFileName);
                if (File.Exists(signaturePath))
                {
                    var signatureBytes = await _ReadBoundedFileAsync(signaturePath,
                        PluginPackage.MaximumSignatureBytes, cancellationToken).ConfigureAwait(false);
                    var signature = JsonSerializer.Deserialize<PluginPackageSignature>(
                        signatureBytes,
                        PluginPackage.SerializerOptions) ?? throw new InvalidDataException("Signature is invalid.");
                    var result = PluginPackage.VerifyFiles(files, signature);
                    if (!result.Valid) throw new InvalidDataException(result.Error ?? "Signature verification failed.");
                    if (state.ExpectedSigner is null || !string.Equals(state.ExpectedSigner, result.Thumbprint,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Plugin signing certificate changed after installation.");
                    if (state.Signer is not null && !string.Equals(state.Signer, result.Signer, StringComparison.Ordinal))
                        throw new InvalidDataException("Plugin signer metadata changed after installation.");
                    if (state.SignedAtUtc is { } expectedSignedAt &&
                        signature.SignedAtUtc.ToUniversalTime() != expectedSignedAt.ToUniversalTime())
                        throw new InvalidDataException("Plugin signing time changed after installation.");
                    if (!string.Equals(state.ExpectedHash, PluginPackage.CreateFileContentHash(files),
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Plugin contents changed after installation.");
                    if (state.Signer is null || state.SignedAtUtc is null)
                    {
                        state = state with
                        {
                            Signer = state.Signer ?? result.Signer,
                            SignedAtUtc = state.SignedAtUtc ?? signature.SignedAtUtc
                        };
                        try { await _WriteStateAsync(manifest.Id, state, cancellationToken).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            LogWrapper.Warn(ex, "Plugin", $"无法保存插件 {manifest.Id} 的签名展示信息");
                        }
                    }
                }
                else
                {
                    if (!state.UnsignedApproved)
                        throw new InvalidDataException("Unsigned plugin has not been approved.");
                    if (!string.Equals(state.ExpectedHash, PluginPackage.CreateFileContentHash(files), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsigned plugin contents changed after installation.");
                }
                var enabled = await _IsEnabledAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
                statesById[manifest.Id] = state;
                candidates.Add(new(manifest, directory, enabled));
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "Plugin", $"跳过无效插件目录 {Path.GetFileName(directory)}");
                if (manifest is not null) _AddInstalled(_WithInstallMetadata(
                    new(manifest, directory, false, false, ex.Message), pluginState));
            }
        }

        foreach (var candidate in candidates.Where(candidate => !candidate.Enabled))
            _AddInstalled(_WithInstallMetadata(new(candidate.Manifest, candidate.Directory, false, false),
                statesById.GetValueOrDefault(candidate.Manifest.Id)));

        var dependencyResolution = PluginDependencyResolver.Resolve(candidates);
        foreach (var (pluginId, error) in dependencyResolution.Blocked)
        {
            var candidate = candidates.Single(item => item.Manifest.Id == pluginId);
            _AddInstalled(_WithInstallMetadata(new(candidate.Manifest, candidate.Directory, true, false, error),
                statesById.GetValueOrDefault(candidate.Manifest.Id)));
            LogWrapper.Warn("Plugin", $"插件 {pluginId} 依赖检查失败：{error}");
        }

        foreach (var candidate in dependencyResolution.LoadOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSafeMode)
            {
                _AddInstalled(_WithInstallMetadata(new(candidate.Manifest, candidate.Directory, true, false,
                    "Plugin loading skipped by safe mode."), statesById.GetValueOrDefault(candidate.Manifest.Id)));
                continue;
            }

            var failedDependency = candidate.Manifest.Dependencies.Keys.FirstOrDefault(id => !_prepared.ContainsKey(id));
            if (failedDependency is not null)
            {
                var error = $"Plugin dependency failed during Mixin preparation: {failedDependency}.";
                _AddInstalled(_WithInstallMetadata(new(candidate.Manifest, candidate.Directory, true, false, error),
                    statesById.GetValueOrDefault(candidate.Manifest.Id)));
                continue;
            }
            try
            {
                var pluginState = await _PreparePluginAsync(candidate.Directory, candidate.Manifest,
                    statesById[candidate.Manifest.Id], cancellationToken).ConfigureAwait(false);
                statesById[candidate.Manifest.Id] = pluginState;
                _AddInstalled(_WithInstallMetadata(new(candidate.Manifest, candidate.Directory, true, false, null),
                    pluginState));
            }
            catch (Exception ex)
            {
                LogWrapper.Error(ex, "Plugin", $"插件 {candidate.Manifest.Id} 加载或应用 Mixin 失败");
                _AddInstalled(_WithInstallMetadata(new(candidate.Manifest, candidate.Directory, true, false, ex.Message),
                    statesById.GetValueOrDefault(candidate.Manifest.Id)));
            }
        }
    }

    /// <summary>Starts plugin entrypoints after the launcher window is available.</summary>
    public async Task StartPreparedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var prepared in _prepared.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failedDependency = prepared.Manifest.Dependencies.Keys.FirstOrDefault(id =>
                !_running.Any(plugin => string.Equals(plugin.Manifest.Id, id, StringComparison.Ordinal)));
            if (failedDependency is not null)
            {
                var error = $"Plugin dependency failed to start: {failedDependency}.";
                LogWrapper.Warn("Plugin", $"插件 {prepared.Manifest.Id} 未启动：{error}");
                lock (_sync)
                {
                    var index = _installed.FindIndex(item => item.Manifest.Id == prepared.Manifest.Id);
                    if (index >= 0) _installed[index] = _installed[index] with { Running = false, Error = error };
                }
                _RollbackPrepared(prepared);
                continue;
            }
            try
            {
                await _StartPluginAsync(prepared, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    var index = _installed.FindIndex(item => item.Manifest.Id == prepared.Manifest.Id);
                    if (index >= 0) _installed[index] = _installed[index] with { Running = true, Error = null };
                }
            }
            catch (Exception ex)
            {
                LogWrapper.Error(ex, "Plugin", $"插件 {prepared.Manifest.Id} 启动失败");
                lock (_sync)
                {
                    var index = _installed.FindIndex(item => item.Manifest.Id == prepared.Manifest.Id);
                    if (index >= 0) _installed[index] = _installed[index] with { Running = false, Error = ex.Message };
                }
                _RollbackPrepared(prepared);
            }
        }
        _prepared.Clear();
    }

    public async Task LoadEnabledAsync(CancellationToken cancellationToken = default)
    {
        await PrepareEnabledAsync(cancellationToken).ConfigureAwait(false);
        await StartPreparedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        _ValidateId(pluginId);
        var oldState = await _ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false);
        await _WriteStateAsync(pluginId, oldState with { Enabled = enabled, UninstallPending = false }, cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            var index = _installed.FindIndex(item => item.Manifest.Id == pluginId);
            if (index >= 0) _installed[index] = _installed[index] with { Enabled = enabled, PendingRestart = true };
        }
    }

    public async Task SetExperimentalFeatureEnabledAsync(string pluginId, string featureId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        _ValidateId(pluginId);
        if (string.IsNullOrWhiteSpace(featureId) || featureId.Length > 64 ||
            featureId.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-')))
            throw new ArgumentException("Invalid experimental feature id.", nameof(featureId));
        InstalledPlugin installed;
        lock (_sync)
            installed = _installed.FirstOrDefault(item => item.Manifest.Id == pluginId)
                        ?? throw new InvalidOperationException("Plugin is not installed.");
        var feature = installed.Manifest.ExperimentalFeatures.FirstOrDefault(item => item.Id == featureId)
                      ?? throw new ArgumentException("Experimental feature is not declared by this plugin.", nameof(featureId));
        var state = await _ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false);
        var featureStates = new Dictionary<string, bool>(state.ExperimentalFeatures ?? new(), StringComparer.Ordinal);
        featureStates[feature.Id] = enabled;
        var errors = new Dictionary<string, string>(state.ExperimentalFeatureErrors ?? new(), StringComparer.Ordinal);
        errors.Remove(feature.Id);
        await _WriteStateAsync(pluginId, state with
        {
            ExperimentalFeatures = featureStates,
            ExperimentalFeatureErrors = errors
        }, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            var index = _installed.FindIndex(item => item.Manifest.Id == pluginId);
            if (index >= 0)
            {
                var current = _installed[index];
                _installed[index] = current with
                {
                    PendingRestart = true,
                    ExperimentalFeatures = _BuildInstalledFeatures(current.Manifest, state with
                    {
                        ExperimentalFeatures = featureStates,
                        ExperimentalFeatureErrors = errors
                    })
                };
            }
        }
    }

    public async Task UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        _ValidateId(pluginId);
        var state = await _ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false);
        await _WriteStateAsync(pluginId, state with { Enabled = false, UninstallPending = true }, cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            var index = _installed.FindIndex(item => item.Manifest.Id == pluginId);
            if (index >= 0) _installed[index] = _installed[index] with { Enabled = false, PendingRestart = true };
        }
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PluginGameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) =>
        PluginHostBridge.GetInstancesAsync is { } provider
            ? await provider(cancellationToken).ConfigureAwait(false)
            : Array.Empty<PluginGameInstance>();

    public Task<bool> LaunchAsync(string instanceId, CancellationToken cancellationToken = default) =>
        PluginHostBridge.LaunchInstanceAsync is { } launcher
            ? launcher(instanceId, cancellationToken)
            : Task.FromResult(false);

    public async Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default)
    {
        if (eventData is null || !_handlers.TryGetValue(typeof(TEvent), out var handlers)) return;
        foreach (var handler in handlers.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await handler(eventData, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", $"事件处理失败：{typeof(TEvent).Name}"); }
        }
    }

    public static void Notify<TEvent>(TEvent eventData)
    {
        if (_current is { } manager) _ = manager.PublishAsync(eventData);
    }

    private static PluginRuntimeManager? _current;

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

    private static async Task<Dictionary<string, byte[]>> ReadInstalledFilesAsync(string directory,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        var rootAttributes = File.GetAttributes(directory);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Plugin installation directory cannot be a symbolic link or junction.");
        var pending = new Stack<string>();
        pending.Push(directory);
        var entryCount = 0;
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                if (++entryCount > PluginPackage.MaximumPackageEntries)
                    throw new InvalidDataException("Plugin installation exceeds the package entry limit.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Plugin installation cannot contain symbolic links or junctions.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                if (relative.Equals(PluginPackage.SignatureFileName, StringComparison.Ordinal))
                {
                    if (new FileInfo(path).Length > PluginPackage.MaximumSignatureBytes)
                        throw new InvalidDataException("Plugin signature metadata is too large.");
                    continue;
                }
                PluginPackage.ValidateRelativePath(relative);
                total = checked(total + new FileInfo(path).Length);
                if (total > PluginPackage.MaximumExpandedBytes)
                    throw new InvalidDataException("Plugin exceeds the expanded size limit.");
                if (relative.Equals(PluginPackage.ManifestFileName, StringComparison.Ordinal) &&
                    new FileInfo(path).Length > PluginPackage.MaximumManifestBytes)
                    throw new InvalidDataException("Plugin manifest exceeds the 64 KiB limit.");
                files.Add(relative, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            }
        }
        return files;
    }

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
            var files = ReadInstalledFilesAsync(directory, CancellationToken.None).GetAwaiter().GetResult();
            if (!string.Equals(state.ExpectedHash, PluginPackage.CreateFileContentHash(files),
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (!files.TryGetValue(PluginPackage.ManifestFileName, out var manifestBytes)) return false;
            var manifest = PluginPackage.DeserializeManifest(manifestBytes);
            if (!string.Equals(manifest.Id, Path.GetFileName(directory), StringComparison.Ordinal) ||
                !string.Equals(manifest.Version, state.InstalledVersion, StringComparison.Ordinal)) return false;
            var signaturePath = Path.Combine(directory, PluginPackage.SignatureFileName);
            if (File.Exists(signaturePath))
            {
                if (new FileInfo(signaturePath).Length > PluginPackage.MaximumSignatureBytes || state.ExpectedSigner is null) return false;
                var signatureBytes = File.ReadAllBytes(signaturePath);
                var signature = JsonSerializer.Deserialize<PluginPackageSignature>(signatureBytes,
                    PluginPackage.SerializerOptions);
                if (signature is null) return false;
                var result = PluginPackage.VerifyFiles(files, signature);
                return result.Valid && string.Equals(result.Thumbprint, state.ExpectedSigner,
                    StringComparison.OrdinalIgnoreCase);
            }
            return state.UnsignedApproved && state.ExpectedSigner is null;
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
        public void Dispose() => _events.Dispose();
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
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) throw new ArgumentException("Invalid page id.", nameof(id));
            PluginPageRegistry.Register(new(pluginId, id, title, createView));
        }
        public void ShowNotification(string message, PluginNotificationKind kind = PluginNotificationKind.Info) =>
            PluginHostBridge.ShowNotification?.Invoke(message, kind);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
            PluginHostBridge.ConfirmAsync?.Invoke(title, message, cancellationToken) ??
            Task.FromResult(System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes);
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
            SendAsync(HttpMethod.Get, uri, cancellationToken: cancellationToken);
        public async Task<PluginHttpResponse> SendAsync(HttpMethod method, Uri uri, string? content = null,
            string contentType = "application/json", CancellationToken cancellationToken = default)
        {
            if (method is null) throw new ArgumentNullException(nameof(method));
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http"))
                throw new ArgumentException("Plugin HTTP requests require an absolute HTTP(S) URI.", nameof(uri));
            if (content is not null && System.Text.Encoding.UTF8.GetByteCount(content) > MaximumPluginHttpBodyBytes)
                throw new InvalidDataException("Plugin HTTP request content exceeds the 16 MiB limit.");
            using var request = new HttpRequestMessage(method, uri);
            if (content is not null) request.Content = new StringContent(content, System.Text.Encoding.UTF8, contentType);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is > MaximumPluginHttpBodyBytes)
                throw new InvalidDataException("Plugin HTTP response exceeds the 16 MiB limit.");
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var bodyStream = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bodyStream.Length + count > MaximumPluginHttpBodyBytes)
                    throw new InvalidDataException("Plugin HTTP response exceeds the 16 MiB limit.");
                await bodyStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            var body = System.Text.Encoding.UTF8.GetString(bodyStream.ToArray());
            return new((int)response.StatusCode, body, response.Headers.Concat(response.Content.Headers)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
        }
    }

    private sealed class PluginMinecraft(PluginRuntimeManager owner) : IPluginMinecraft
    {
        public Task<IReadOnlyList<PluginGameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default) => owner.GetInstancesAsync(cancellationToken);
        public Task<bool> LaunchAsync(string instanceId, CancellationToken cancellationToken = default) => owner.LaunchAsync(instanceId, cancellationToken);
    }

    private sealed class PluginLauncher(string pluginsDirectory, string dataDirectory, HttpClient httpClient) : IPluginLauncher
    {
        public string Name => "PCL Lite";
        public string Version => typeof(PluginRuntimeManager).Assembly.GetName().Version?.ToString() ?? "unknown";
        public string DataDirectory => dataDirectory;
        public string PluginsDirectory => pluginsDirectory;
        public async Task DownloadAsync(Uri uri, string relativePath, IProgress<PluginDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
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
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        progress?.Report(new(total, response.Content.Headers.ContentLength));
                    }
                    if (expectedLength is { } expected && total != expected)
                        throw new InvalidDataException("Plugin download ended before the declared content length.");
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
