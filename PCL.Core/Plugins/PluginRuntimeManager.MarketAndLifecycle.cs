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
                (entry.Website?.Length ?? 0) > 2048 || (entry.Icon?.Length ?? 0) > 1024)
                throw new InvalidDataException($"Plugin market entry has invalid or oversized metadata: {entry.Id}");
            if (!Uri.TryCreate(entry.PackageUrl, UriKind.Absolute, out var packageUri) || packageUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Plugin package URL must use HTTPS: {entry.Id}");
            if (entry.Website is not null &&
                (!Uri.TryCreate(entry.Website, UriKind.Absolute, out var websiteUri) || websiteUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException($"Plugin website URL must use HTTPS: {entry.Id}");
            if (entry.Icon is not null && (!Uri.TryCreate(entry.Icon, UriKind.Absolute, out var iconUri) ||
                iconUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException($"Plugin market icon URL must use HTTPS: {entry.Id}");
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
            var extracted = await PluginPackage.ExtractVerifiedAsync(packagePath, staging,
                check.ContentHash, check.PackageSha256!, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(extracted.Manifest.Id, check.Manifest.Id, StringComparison.Ordinal) ||
                !string.Equals(extracted.Manifest.Version, check.Manifest.Version, StringComparison.Ordinal))
                throw new InvalidDataException("Plugin package changed after confirmation.");
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
            _ = PublishAsync(new PluginStateChangedEvent(check.Manifest.Id, nextState.Enabled, "install"));
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
                var check = await PluginPackage.InspectDirectoryAsync(directory, cancellationToken)
                    .ConfigureAwait(false);
                if (check.Error is not null || !check.IsValid)
                    throw new InvalidDataException(check.Error ?? "Installed plugin package is invalid.");
                manifest = check.Manifest;
                if (!string.Equals(manifest.Id, Path.GetFileName(directory), StringComparison.Ordinal))
                    throw new InvalidDataException("Plugin id does not match its installation directory.");
                if (manifest.PclCoreVersion is { } coreVersionRange &&
                    !PluginVersionRange.Satisfies(Basics.VersionName, coreVersionRange))
                    throw new InvalidDataException(
                        $"Plugin supports PCL Core {coreVersionRange}; current version is {Basics.VersionName}.");
                var state = await _ReadStateAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
                pluginState = state;
                if (check.IsSigned)
                {
                    if (state.ExpectedSigner is null || !string.Equals(state.ExpectedSigner, check.SignerThumbprint,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Plugin signing certificate changed after installation.");
                    if (state.Signer is not null && !string.Equals(state.Signer, check.Signer, StringComparison.Ordinal))
                        throw new InvalidDataException("Plugin signer metadata changed after installation.");
                    if (state.SignedAtUtc is { } expectedSignedAt &&
                        check.SignedAtUtc?.ToUniversalTime() != expectedSignedAt.ToUniversalTime())
                        throw new InvalidDataException("Plugin signing time changed after installation.");
                    if (!string.Equals(state.ExpectedHash, check.ContentHash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Plugin contents changed after installation.");
                    if (state.Signer is null || state.SignedAtUtc is null)
                    {
                        state = state with
                        {
                            Signer = state.Signer ?? check.Signer,
                            SignedAtUtc = state.SignedAtUtc ?? check.SignedAtUtc
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
                    if (!string.Equals(state.ExpectedHash, check.ContentHash, StringComparison.OrdinalIgnoreCase))
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
        _ = PublishAsync(new PluginStateChangedEvent(pluginId, enabled, enabled ? "enable" : "disable"));
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
        _ = PublishAsync(new PluginStateChangedEvent(pluginId, enabled, "experimental-feature"));
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
        _ = PublishAsync(new PluginStateChangedEvent(pluginId, false, "uninstall"));
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


    public async Task<PluginGameInstance?> GetSelectedInstanceAsync(CancellationToken cancellationToken = default)
    {
        if (PluginHostBridge.GetSelectedInstanceAsync is { } provider)
            return await provider(cancellationToken).ConfigureAwait(false);
        return (await GetInstancesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.IsSelected);
    }


    public Task<IReadOnlyDictionary<string, string>> GetInstanceConfigurationAsync(string instanceId,
        CancellationToken cancellationToken = default) =>
        PluginHostBridge.GetInstanceConfigurationAsync is { } provider
            ? provider(instanceId, cancellationToken)
            : Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());


    public Task<bool> LaunchAsync(string instanceId, PluginLaunchOptions options,
        CancellationToken cancellationToken = default) =>
        PluginHostBridge.LaunchInstanceWithOptionsAsync is { } launcher
            ? launcher(instanceId, options, cancellationToken)
            : LaunchAsync(instanceId, cancellationToken);


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


    private bool _RegisterService<TService>(string pluginId, TService service) where TService : class =>
        _services.TryAdd(typeof(TService), (pluginId, service));


    private TService? _GetService<TService>() where TService : class =>
        _services.TryGetValue(typeof(TService), out var registration) ? registration.Service as TService : null;


    private bool _UnregisterService<TService>(string pluginId) where TService : class =>
        _services.TryGetValue(typeof(TService), out var registration) && registration.PluginId == pluginId &&
        _services.TryRemove(new KeyValuePair<Type, (string PluginId, object Service)>(typeof(TService), registration));


    private void _RemovePluginServices(string pluginId)
    {
        foreach (var pair in _services)
            if (string.Equals(pair.Value.PluginId, pluginId, StringComparison.Ordinal))
                _services.TryRemove(new KeyValuePair<Type, (string PluginId, object Service)>(pair.Key, pair.Value));
    }


    public static void Notify<TEvent>(TEvent eventData)
    {
        if (_current is { } manager) _ = manager.PublishAsync(eventData);
    }

}
