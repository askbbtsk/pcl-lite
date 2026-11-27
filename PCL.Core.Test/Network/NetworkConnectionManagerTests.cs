using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net.Consent;

namespace PCL.Core.Test.Network;

[TestClass]
public sealed class NetworkConnectionManagerTests
{
    [TestMethod]
    public async Task DeniedRequestDoesNotCreateTransport()
    {
        using var consent = NetworkConsentService.SetConsentForCurrentTest(false);
        var created = false;
        using var client = NetworkConnectionManager.CreateHttpClient("Denied module",
            transportFactory: () => { created = true; return new ResponseHandler(); });
        await Assert.ThrowsExactlyAsync<NetworkAccessDeniedException>(() => client.GetAsync("https://example.invalid/"));
        Assert.IsFalse(created);
    }

    [TestMethod]
    public async Task AllowedRequestReturnsResponseWithoutSendingModuleAsHeader()
    {
        using var consent = NetworkConsentService.AllowAllForCurrentTest();
        const string module = "Module-identity-not-a-wire-header";
        var transport = new ResponseHandler(request =>
        {
            Assert.AreEqual(module, NetworkConnectionManager.GetSource(request, new("fallback")).Name);
            Assert.IsFalse(request.Headers.Any(header =>
                header.Key.Contains("PCL.Network", StringComparison.OrdinalIgnoreCase) ||
                header.Value.Any(value => value.Contains(module, StringComparison.Ordinal))));
        });
        using var client = NetworkConnectionManager.CreateHttpClient(module, transportFactory: () => transport);
        using var response = await client.GetAsync("https://example.invalid/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, transport.Calls);
    }

    [TestMethod]
    public async Task ExplicitRequestOwnerSurvivesSharedClient()
    {
        using var consent = NetworkConsentService.AllowAllForCurrentTest();
        var transport = new ResponseHandler(request =>
            Assert.AreEqual("Plugin: test-plugin", NetworkConnectionManager.GetSource(request, new("fallback")).Name));
        using var client = NetworkConnectionManager.CreateHttpClient("Shared client", transportFactory: () => transport);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        NetworkConnectionManager.SetSource(request, new("Plugin: test-plugin"));
        using var response = await client.SendAsync(request);
        Assert.AreEqual(1, transport.Calls);
    }

    [TestMethod]
    public async Task CancelledRequestDoesNotCreateTransport()
    {
        using var consent = NetworkConsentService.AllowAllForCurrentTest();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var created = false;
        using var client = NetworkConnectionManager.CreateHttpClient("Cancelled module",
            transportFactory: () => { created = true; return new ResponseHandler(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.GetAsync("https://example.invalid/", cts.Token));
        Assert.IsFalse(created);
    }

    [TestMethod]
    public async Task DeniedWebSocketRemainsUnconnected()
    {
        using var consent = NetworkConsentService.SetConsentForCurrentTest(false);
        using var socket = new ClientWebSocket();
        await Assert.ThrowsExactlyAsync<NetworkAccessDeniedException>(() =>
            NetworkConnectionManager.ConnectWebSocketAsync(socket, new("WebSocket module", NetworkPurpose.Lunar),
                new Uri("wss://example.invalid/")));
        Assert.AreEqual(WebSocketState.None, socket.State);
    }

    [TestMethod]
    public async Task DeniedSocketRemainsUnconnected()
    {
        using var consent = NetworkConsentService.SetConsentForCurrentTest(false);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsExactlyAsync<NetworkAccessDeniedException>(() =>
            NetworkConnectionManager.ConnectSocketAsync(socket, new("TCP module", NetworkPurpose.ServerConnection),
                new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9)));
        Assert.IsFalse(socket.Connected);
        Assert.IsNull(socket.LocalEndPoint);
    }

    [TestMethod]
    public async Task LocalIpcRemainsAvailableWhenExternalConnectionsAreDenied()
    {
        using var consent = NetworkConsentService.SetConsentForCurrentTest(false);
        await NetworkConnectionManager.AuthorizeAsync(new("Local IPC"), new Uri("http://127.0.0.1:28190/"));
    }

    [TestMethod]
    public async Task AnonymousModuleIsRejected()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            NetworkConnectionManager.AuthorizeAsync(new(" "), new Uri("https://example.invalid/")));
    }

    [TestMethod]
    public void DifferentModulesOnSameHostHaveIndependentManagementEntries()
    {
        var first = NetworkPurposeCatalog.RegisterRequest("Independent module A", "example.invalid");
        var second = NetworkPurposeCatalog.RegisterRequest("Independent module B", "example.invalid");
        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(first, NetworkPurposeCatalog.ById(first.Id));
        Assert.AreEqual(second, NetworkPurposeCatalog.ById(second.Id));
        Assert.AreEqual(first.Id, NetworkPurposeCatalog.RegisterRequest(first.TitleZh, "second.invalid").Id);
    }

    [TestMethod]
    public void NamedRequestTargetsStayBounded()
    {
        NetworkPurposeInfo? result = null;
        for (var i = 0; i < 40; i++)
            result = NetworkPurposeCatalog.RegisterRequest("Bounded targets module", $"target-{i}.invalid");
        Assert.AreEqual(32, result!.Hosts.Length);
        Assert.AreEqual("target-39.invalid", result.Hosts[^1]);
    }

    [TestMethod]
    public void CatalogIncludesPreviouslyUnmanagedFeatures()
    {
        foreach (var purpose in new[] { NetworkPurpose.Authentication, NetworkPurpose.Lunar, NetworkPurpose.Bedrock,
                     NetworkPurpose.Plugins, NetworkPurpose.ServerConnection, NetworkPurpose.DirectLink })
            Assert.IsNotNull(NetworkPurposeCatalog.ByPurpose(purpose));
    }

    private sealed class ResponseHandler(Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
