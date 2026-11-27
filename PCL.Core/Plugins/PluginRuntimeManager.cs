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
    public string? Icon { get; init; }
    public Dictionary<string, string> LocalizedNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> LocalizedDescriptions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PluginMarketIndex(int SchemaVersion, IReadOnlyList<PluginMarketEntry> Plugins);

public enum PluginMarketUpdateKind { NotInstalled, Current, UpdateAvailable, DowngradeAvailable }

public sealed record PluginMarketUpdateCandidate(PluginMarketEntry Entry, InstalledPlugin? Installed,
    PluginMarketUpdateKind Kind);

public sealed class PluginPage
{
    private readonly Func<System.Windows.FrameworkElement> _createView;
    private readonly PluginPageLifecycle? _lifecycle;
    private System.Windows.FrameworkElement? _view;
    private int _isShown;

    public PluginPage(string pluginId, string pluginName, string pageId, string title,
        Func<System.Windows.FrameworkElement> createView, PluginPageLifecycle? lifecycle = null,
        bool isMainPage = false)
    {
        PluginId = pluginId;
        PluginName = pluginName;
        PageId = pageId;
        Title = title;
        _createView = createView;
        _lifecycle = lifecycle;
        IsMainPage = isMainPage;
    }

    /// <summary>Backward-compatible constructor for callers without a separate plugin display name.</summary>
    public PluginPage(string pluginId, string pageId, string title,
        Func<System.Windows.FrameworkElement> createView, PluginPageLifecycle? lifecycle = null,
        bool isMainPage = false)
        : this(pluginId, pluginId, pageId, title, createView, lifecycle, isMainPage)
    {
    }

    public string PluginId { get; }
    public string PluginName { get; }
    public string PageId { get; }
    public string Title { get; }
    public bool IsMainPage { get; }
    public string MainPageKey => $"{PluginId}/{PageId}";

    public System.Windows.FrameworkElement GetOrCreateView() => _view ??= _createView();

    public async Task NotifyShownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isShown, 1) != 0) return;
        try { if (_lifecycle?.OnShown is { } callback) await callback(cancellationToken); }
        catch (Exception ex) { LogWrapper.Error(ex, "Plugin", $"插件页面显示回调失败：{PluginId}/{PageId}"); }
    }

    public async Task NotifyHiddenAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isShown, 0) == 0) return;
        try { if (_lifecycle?.OnHidden is { } callback) await callback(cancellationToken); }
        catch (Exception ex) { LogWrapper.Error(ex, "Plugin", $"插件页面隐藏回调失败：{PluginId}/{PageId}"); }
    }
}

/// <summary>Plugin-owned pages exposed to the host UI.</summary>
public static class PluginPageRegistry
{
    private static readonly ConcurrentDictionary<string, PluginPage> Pages = new(StringComparer.Ordinal);
    public static event Action? Changed;
    public static IReadOnlyCollection<PluginPage> GetAll() => Pages.Values.ToArray();
    public static IReadOnlyCollection<PluginPage> GetMainPages() =>
        Pages.Values.Where(page => page.IsMainPage).OrderBy(page => page.PluginName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(page => page.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    public static void Register(PluginPage page)
    {
        if (!Pages.TryAdd($"{page.PluginId}/{page.PageId}", page))
            throw new InvalidOperationException($"Plugin page is already registered: {page.PluginId}/{page.PageId}");
        Changed?.Invoke();
    }
    public static bool Unregister(string pluginId, string pageId)
        => Unregister(pluginId, pageId, isMainPage: null);

    public static bool Unregister(string pluginId, string pageId, bool isMainPage)
        => Unregister(pluginId, pageId, (bool?)isMainPage);

    private static bool Unregister(string pluginId, string pageId, bool? isMainPage)
    {
        var key = $"{pluginId}/{pageId}";
        if (!Pages.TryGetValue(key, out var page) ||
            isMainPage.HasValue && page.IsMainPage != isMainPage.Value ||
            !Pages.TryRemove(key, out _)) return false;
        if (page.IsMainPage) PluginMainPagePreferences.Remove(page.MainPageKey);
        Changed?.Invoke();
        return true;
    }
    public static void RemovePlugin(string pluginId)
    {
        var removed = new List<string>();
        foreach (var pair in Pages.Where(pair => pair.Key.StartsWith(pluginId + "/", StringComparison.Ordinal)).ToArray())
            if (Pages.TryRemove(pair.Key, out var page) && page.IsMainPage)
                removed.Add(page.MainPageKey);
        if (removed.Count > 0)
            PluginMainPagePreferences.RemovePlugin(pluginId, removed);
        Changed?.Invoke();
    }
}

/// <summary>Persists page visibility and first-use authorization per registered plugin main page.</summary>
public static class PluginMainPagePreferences
{
    private static readonly object Sync = new();
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new();

    public static IReadOnlySet<string> GetHiddenKeys()
    {
        lock (Sync) return Read(Config.Preference.PluginMainPagesHidden);
    }

    public static bool IsHidden(string key) => GetHiddenKeys().Contains(key);

    public static void SetHidden(string key, bool hidden)
    {
        lock (Sync)
        {
            var keys = Read(Config.Preference.PluginMainPagesHidden);
            if (hidden) keys.Add(key);
            else keys.Remove(key);
            Config.Preference.PluginMainPagesHidden = Write(keys);
        }
    }

    public static bool IsAuthorized(string key)
    {
        lock (Sync) return Read(Config.Preference.PluginMainPagesAuthorized).Contains(key);
    }

    public static void Authorize(IEnumerable<string> keys)
    {
        lock (Sync)
        {
            var authorized = Read(Config.Preference.PluginMainPagesAuthorized);
            authorized.UnionWith(keys);
            Config.Preference.PluginMainPagesAuthorized = Write(authorized);
        }
    }

    public static void Remove(string key)
    {
        lock (Sync)
        {
            var hidden = Read(Config.Preference.PluginMainPagesHidden);
            var authorized = Read(Config.Preference.PluginMainPagesAuthorized);
            if (hidden.Remove(key)) Config.Preference.PluginMainPagesHidden = Write(hidden);
            if (authorized.Remove(key)) Config.Preference.PluginMainPagesAuthorized = Write(authorized);
        }
    }

    public static void RemovePlugin(string pluginId, IEnumerable<string>? removedKeys = null)
    {
        lock (Sync)
        {
            var prefix = pluginId + "/";
            var removed = (removedKeys ?? []).ToHashSet(StringComparer.Ordinal);
            var hidden = Read(Config.Preference.PluginMainPagesHidden);
            var authorized = Read(Config.Preference.PluginMainPagesAuthorized);
            var hiddenChanged = hidden.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal)) > 0;
            var authorizedChanged = authorized.RemoveWhere(key =>
                key.StartsWith(prefix, StringComparison.Ordinal) || removed.Contains(key)) > 0;
            if (hiddenChanged) Config.Preference.PluginMainPagesHidden = Write(hidden);
            if (authorizedChanged) Config.Preference.PluginMainPagesAuthorized = Write(authorized);
        }
    }

    private static HashSet<string> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (System.Text.Json.JsonException) { return new(StringComparer.Ordinal); }
    }

    private static string Write(IEnumerable<string> keys) =>
        System.Text.Json.JsonSerializer.Serialize(keys.Order(StringComparer.Ordinal), JsonOptions);
}

/// <summary>Callbacks supplied by the WPF host for launcher-specific APIs.</summary>
public static class PluginHostBridge
{
    public static Func<CancellationToken, Task<IReadOnlyList<PluginGameInstance>>>? GetInstancesAsync { get; set; }
    public static Func<string, CancellationToken, Task<bool>>? LaunchInstanceAsync { get; set; }
    public static Func<CancellationToken, Task<PluginGameInstance?>>? GetSelectedInstanceAsync { get; set; }
    public static Func<string, CancellationToken, Task<IReadOnlyDictionary<string, string>>>? GetInstanceConfigurationAsync { get; set; }
    public static Func<string, PluginLaunchOptions, CancellationToken, Task<bool>>? LaunchInstanceWithOptionsAsync { get; set; }
    public static Func<string, string, Func<Task>, PluginButtonStyle, System.Windows.FrameworkElement>? CreateButton { get; set; }
    public static Action<string, PluginNotificationKind>? ShowNotification { get; set; }
    public static Action<string, PluginNotificationKind, Func<Task>?>? ShowNotificationWithAction { get; set; }
    public static Func<string, string, CancellationToken, Task<bool>>? ConfirmAsync { get; set; }
    public static Func<string, string, PluginDialogOptions, CancellationToken, Task<int>>? ChooseAsync { get; set; }
    public static Action<Uri>? OpenUrl { get; set; }
    public static Action<string>? OpenPath { get; set; }
}

public sealed partial class PluginRuntimeManager : IAsyncDisposable
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
    private readonly ConcurrentDictionary<Type, (string PluginId, object Service)> _services = new();
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
        _httpClient = httpClient ?? IO.Net.Consent.NetworkConnectionManager.CreateHttpClient(
            "Plugin runtime", IO.Net.Consent.NetworkPurpose.Plugins, TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(_pluginsDirectory);
        Directory.CreateDirectory(_stateDirectory);
        _ApplyPendingChanges();
        _CleanupPendingDeletes();
    }

    private static PluginRuntimeManager? _current;
}
