using System.Windows;
using System.Net.Http;

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
    void ShowNotification(string message, PluginNotificationKind kind = PluginNotificationKind.Info);
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
}

public interface IPluginEvents
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler);
    Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default);
}

public interface IPluginNetwork
{
    Task<PluginHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken = default);
    Task<PluginHttpResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        string? content = null,
        string contentType = "application/json",
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
    Task<bool> LaunchAsync(string instanceId, CancellationToken cancellationToken = default);
}

public sealed record PluginGameInstance(string Id, string Name, string Version, string GameDirectory, bool IsSelected);

public interface IPluginLauncher
{
    string Name { get; }
    string Version { get; }
    string DataDirectory { get; }
    string PluginsDirectory { get; }
    Task DownloadAsync(Uri uri, string relativePath, IProgress<PluginDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
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
