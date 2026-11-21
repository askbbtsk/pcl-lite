using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Plugin.ImcProxy;

public sealed class ImcProxyApi
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string BaseUrl { get; set; } = "http://127.0.0.1:8790/api";
    public string Token { get; set; } = "";

    private HttpRequestMessage Build(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/'));
        if (!string.IsNullOrWhiteSpace(Token))
            request.Headers.TryAddWithoutValidation("X-Api-Token", Token);
        if (content is not null)
            request.Content = content;
        return request;
    }

    private async Task<JsonNode> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonNode? node = null;
        try
        {
            node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch
        {
        }
        if (!response.IsSuccessStatusCode)
        {
            var message = node?["error"]?.GetValue<string>() ?? text;
            throw new ImcProxyApiException((int)response.StatusCode, string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? "request failed" : message);
        }
        return node ?? new JsonObject();
    }

    public async Task<JsonNode> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await _http.SendAsync(Build(HttpMethod.Get, path), cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public async Task<JsonNode> PostAsync(string path, object? body = null, CancellationToken cancellationToken = default)
    {
        var payload = body is null ? "{}" : JsonSerializer.Serialize(body);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(Build(HttpMethod.Post, path, content), cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    public Task<JsonNode> StatusAsync(CancellationToken cancellationToken = default) => GetAsync("status", cancellationToken);

    public Task<JsonNode> ConfigAsync(CancellationToken cancellationToken = default) => GetAsync("config", cancellationToken);

    public Task<JsonNode> SettingAsync(string key, CancellationToken cancellationToken = default) =>
        GetAsync("config/" + Uri.EscapeDataString(key), cancellationToken);

    public Task<JsonNode> SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
        PostAsync("config/" + Uri.EscapeDataString(key), new { value }, cancellationToken);

    public Task<JsonNode> StartAsync(CancellationToken cancellationToken = default) => PostAsync("start", null, cancellationToken);

    public Task<JsonNode> StopAsync(CancellationToken cancellationToken = default) => PostAsync("stop", null, cancellationToken);

    public Task<JsonNode> CommandAsync(string line, CancellationToken cancellationToken = default) =>
        PostAsync("command", new { line }, cancellationToken);

    public Task<JsonNode> VersionAsync(CancellationToken cancellationToken = default) => GetAsync("version", cancellationToken);

    public Task<JsonNode> ChangelogAsync(CancellationToken cancellationToken = default) => GetAsync("changelog", cancellationToken);
}

public sealed class ImcProxyApiException : Exception
{
    public int StatusCode { get; }

    public ImcProxyApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
