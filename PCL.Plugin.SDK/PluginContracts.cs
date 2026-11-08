using System.Windows;
using System.Net.Http;
using System.IO;

namespace PCL.Plugin.SDK;

/// <summary>Entry point implemented by a PCL Lite plugin.</summary>
public interface IPclPlugin
{
    Task StartAsync(IPluginContext context, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Version of the host API this SDK exposes.</summary>
public static class PluginApiVersion
{
    public const int Current = 1;
}

/// <summary>Stable host API supplied to each plugin instance.</summary>
public interface IPluginContext
{
    IPluginMetadata Plugin { get; }
    string DataDirectory { get; }
    IPluginLogger Logger { get; }
    IPluginStorage Storage { get; }
    IPluginSettings Settings { get; }
    IPluginUi Ui { get; }
    IPluginEvents Events { get; }
    IPluginNetwork Network { get; }
    IPluginMinecraft Minecraft { get; }
    IPluginLauncher Launcher { get; }
    IPluginServices Services { get; }
}

public interface IPluginServices
{
    bool Register<TService>(TService service) where TService : class;
    TService? Get<TService>() where TService : class;
    bool Unregister<TService>() where TService : class;
}

public interface IPluginMetadata
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Author { get; }
    string Description { get; }
    int ApiVersion { get; }
}

public interface IPluginLogger
{
    void Trace(string message);
    void Debug(string message);
    void Info(string message);
    void Warn(string message, Exception? exception = null);
    void Error(string message, Exception? exception = null);
}

/// <summary>Per-plugin files and JSON settings, stored below the plugin's own data directory.</summary>
public interface IPluginStorage
{
    Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default);
    Task WriteTextAsync(string relativePath, string contents, CancellationToken cancellationToken = default);
    Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default);
    Task WriteBytesAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);
    Task WriteStreamAsync(string relativePath, Stream source, long? expectedLength = null,
        CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default);
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListAsync(string relativeDirectory = "",
        CancellationToken cancellationToken = default);
    Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken = default);
    Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken = default);
}

public interface IPluginSettings
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public interface IPluginUi
{
    void RegisterPage(string id, string title, Func<FrameworkElement> createView);
    void RegisterPage(string id, string title, Func<FrameworkElement> createView, PluginPageLifecycle lifecycle);
    bool UnregisterPage(string id);
    FrameworkElement CreateButton(string text, Func<Task> onClick,
        PluginButtonStyle style = PluginButtonStyle.Normal);
    void ShowNotification(string message, PluginNotificationKind kind = PluginNotificationKind.Info);
    void ShowNotification(string message, PluginNotificationKind kind, Func<Task>? onClick);
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
    Task<int> ChooseAsync(string title, string message, PluginDialogOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional callbacks invoked when the host displays or hides a registered page.</summary>
public sealed record PluginPageLifecycle(
    Func<CancellationToken, Task>? OnShown = null,
    Func<CancellationToken, Task>? OnHidden = null);

/// <summary>Visual treatment for buttons created by the PCL host.</summary>
public enum PluginButtonStyle
{
    Normal,
    Highlight,
    Danger
}

public sealed record PluginDialogOptions(string ConfirmText = "", string CancelText = "",
    string? AlternateText = null);

public interface IPluginEvents
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler);
    Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default);
}

public interface IPluginNetwork
{
    Task<PluginHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken = default);
    Task<PluginHttpResponse> GetAsync(Uri uri, IReadOnlyDictionary<string, string> headers,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    Task<PluginHttpResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        string? content = null,
        string contentType = "application/json",
        CancellationToken cancellationToken = default);
    Task<PluginHttpResponse> SendAsync(HttpMethod method, Uri uri, string? content, string contentType,
        IReadOnlyDictionary<string, string> headers, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed record PluginHttpResponse(int StatusCode, string Content, IReadOnlyDictionary<string, string[]> Headers)
{
    /// <summary>Response payload bytes. Host responses retain the original bytes; use this for binary content.</summary>
    public ReadOnlyMemory<byte> ContentBytes { get; init; } = System.Text.Encoding.UTF8.GetBytes(Content);

    /// <summary>Raw Content-Type header value, if the response supplied one.</summary>
    public string? ContentType { get; init; }
}

public interface IPluginMinecraft
{
    Task<IReadOnlyList<PluginGameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default);
    Task<PluginGameInstance?> GetSelectedInstanceAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> GetInstanceConfigurationAsync(string instanceId,
        CancellationToken cancellationToken = default);
    Task<bool> LaunchAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<bool> LaunchAsync(string instanceId, PluginLaunchOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record PluginGameInstance(string Id, string Name, string Version, string GameDirectory, bool IsSelected);
public sealed record PluginLaunchOptions(IReadOnlyList<string>? AdditionalJvmArguments = null,
    IReadOnlyDictionary<string, string>? Environment = null, string? WorldName = null);

public interface IPluginLauncher
{
    string Name { get; }
    string Version { get; }
    string CoreVersion { get; }
    string DataDirectory { get; }
    string PluginsDirectory { get; }
    Task DownloadAsync(Uri uri, string relativePath, IProgress<PluginDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task DownloadAsync(Uri uri, string relativePath, string expectedSha256,
        IProgress<PluginDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    void OpenUrl(Uri uri);
    void OpenPath(string path);
}

public sealed record PluginDownloadProgress(long BytesReceived, long? TotalBytes);

public enum PluginNotificationKind
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record PluginGameLaunchingEvent(string InstanceId, string InstanceName);
public sealed record PluginGameStartedEvent(string InstanceId, string InstanceName, int ProcessId);
public sealed record PluginGameExitedEvent(string InstanceId, string InstanceName, int ExitCode);
public sealed record PluginThemeChangedEvent(string ThemeId);
public sealed record PluginInstanceListChangedEvent(string GameDirectory);
public sealed record PluginPageNavigationEvent(string? PreviousPageId, string? CurrentPageId);
public sealed record PluginStateChangedEvent(string PluginId, bool Enabled, string Action);
