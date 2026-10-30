using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.App.Lunar.Social;

/// <summary>
///     Lunar 后端的连接（<c>wss://websocket.lunarclientprod.com/launcher-v3</c>），
///     负责握手、RPC 与推送分发。好友与私聊都挂在它上面。
///     <para>
///         一开连接先发一条<b>裸的</b> <c>Handshake</c>（没有信封），之后才是
///         <c>ServerboundWebSocketMessage</c> / <c>ClientboundWebSocketMessage</c>。
///         官方启动器握手成功后会把所有服务并行 <c>Login</c> 一遍，这里只做好友和会话两个。
///     </para>
///     <para>
///         线程模型：一条收包循环 + 一把写锁。RPC 用「自增 id → TaskCompletionSource」
///         对上号，推送直接在收包线程上抛事件（订阅方自己切 UI 线程）。
///     </para>
/// </summary>
public sealed class LunarSocialClient : IAsyncDisposable
{
    private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(20);

    private readonly string _installationId;
    private readonly string _launcherVersion;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<byte[]>> _pending = new(StringComparer.Ordinal);
    private readonly object _pendingLock = new();
    private readonly List<LunarFriend> _friends = [];
    private readonly CancellationTokenSource _lifetime = new();

    private ClientWebSocket? _socket;
    private Task? _receiveLoop;
    private int _requestCounter;
    private LunarUuid _selfUuid;
    private string _selfName = string.Empty;
    private volatile bool _connected;

    public LunarSocialClient(string installationId, string? launcherVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        _installationId = installationId;
        _launcherVersion = string.IsNullOrWhiteSpace(launcherVersion)
            ? LunarSocialProtocol.ResolveLauncherVersion()
            : launcherVersion;
    }

    /// <summary>连接状态变了（连上 / 断开）。</summary>
    public event Action? StateChanged;

    /// <summary>好友列表刷新了。</summary>
    public event Action? FriendsChanged;

    /// <summary>收到一条私聊消息。<c>参数：会话 id、消息</c>。</summary>
    public event Action<string, LunarChatMessage>? MessageReceived;

    public bool IsConnected => _connected;

    /// <summary>上一次断开的原因（WebSocket 关闭码 + 说明），诊断用。</summary>
    public string? LastCloseReason { get; private set; }

    public IReadOnlyList<LunarFriend> Friends
    {
        get
        {
            lock (_friends) return _friends.ToArray();
        }
    }

    /// <summary>连上并完成握手。重复调用会先断开旧连接。</summary>
    public async Task ConnectAsync(string accessToken, LunarUuid uuid, string username,
        CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _selfUuid = uuid;
        _selfName = username;

        var jwt = await LunarAuthenticator.GetLauncherJwtAsync(accessToken, uuid, username, _launcherVersion,
                cancellationToken)
            .ConfigureAwait(false);

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Accept", "application/x-protobuf");

        try
        {
            await socket.ConnectAsync(new Uri(LunarSocialProtocol.AssetServerUrl), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            socket.Dispose();
            throw new LunarSocialException("连不上 Lunar 后端：" + ex.Message, ex);
        }

        _socket = socket;
        var handshake = LunarSocialProtocol.EncodeHandshake(jwt, uuid, username, _installationId,
            _launcherVersion);
        await SendRawAsync(socket, handshake, cancellationToken).ConfigureAwait(false);

        _connected = true;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _lifetime.Token), CancellationToken.None);
        StateChanged?.Invoke();

        // 官方启动器是「所有服务并行 Login」，这里两个服务互相独立，顺序来也不会更慢
        await LoadFriendsAsync(cancellationToken).ConfigureAwait(false);
        await CallAsync(LunarSocialProtocol.ConversationService, "Login",
            LunarSocialProtocol.EncodeConversationLogin(), cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        var socket = _socket;
        _socket = null;
        _connected = false;

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // 已经断了就算了
            }

            socket.Dispose();
        }

        FailAllPending("连接已断开");
        StateChanged?.Invoke();
    }

    /// <summary>重新拉一遍好友列表。</summary>
    public async Task LoadFriendsAsync(CancellationToken cancellationToken = default)
    {
        var output = await CallAsync(LunarSocialProtocol.FriendService, "Login",
            LunarSocialProtocol.EncodeEmptyMessage(), cancellationToken).ConfigureAwait(false);

        var friends = LunarSocialProtocol.DecodeFriendLoginResponse(output);
        lock (_friends)
        {
            _friends.Clear();
            _friends.AddRange(friends);
        }

        FriendsChanged?.Invoke();
    }

    /// <summary>拉会话列表（按最近活跃排序，由服务端决定）。</summary>
    public async Task<IReadOnlyList<LunarConversation>> GetConversationsAsync(int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var output = await CallAsync(LunarSocialProtocol.ConversationService, "GetConversations",
                LunarSocialProtocol.EncodeGetConversations(limit), cancellationToken)
            .ConfigureAwait(false);
        return LunarSocialProtocol.DecodeConversations(output, _selfUuid.ToString()).Conversations;
    }

    /// <summary>
    ///     拿到「和这个好友的会话」。已经有会话就直接返回原来那个 id，没有就新建一个。
    /// </summary>
    public async Task<string> OpenConversationAsync(string friendUuid,
        CancellationToken cancellationToken = default)
    {
        var output = await CallAsync(LunarSocialProtocol.ConversationService, "CreateConversation",
                LunarSocialProtocol.EncodeCreateConversation(LunarSocialProtocol.ConversationTypeFriend,
                    LunarUuid.Parse(friendUuid)), cancellationToken)
            .ConfigureAwait(false);

        var (status, conversation) = LunarSocialProtocol.DecodeCreateConversationResponse(output,
            _selfUuid.ToString());
        if (conversation is null || conversation.Id.Length == 0)
            throw new LunarSocialException($"建会话失败（状态 {status}）");

        return conversation.Id;
    }

    /// <summary>拉某段会话的历史消息。</summary>
    public async Task<IReadOnlyList<LunarChatMessage>> LoadMessagesAsync(string friendUuid, string conversationId,
        CancellationToken cancellationToken = default)
    {
        var reference = LunarSocialProtocol.EncodeConversationReference(
            LunarUuid.Parse(friendUuid), LunarUuid.Parse(conversationId));
        var output = await CallAsync(LunarSocialProtocol.ConversationService, "LoadConversation",
                LunarSocialProtocol.EncodeLoadConversation(reference), cancellationToken)
            .ConfigureAwait(false);
        return LunarSocialProtocol.DecodeConversationMessages(output, _selfUuid.ToString());
    }

    /// <summary>发一条纯文本消息。</summary>
    public async Task SendMessageAsync(string friendUuid, string conversationId, string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var reference = LunarSocialProtocol.EncodeConversationReference(
            LunarUuid.Parse(friendUuid), LunarUuid.Parse(conversationId));
        var output = await CallAsync(LunarSocialProtocol.ConversationService, "SendConversationMessage",
                LunarSocialProtocol.EncodeSendMessage(reference, text), cancellationToken)
            .ConfigureAwait(false);

        var status = LunarSocialProtocol.DecodeSendMessageStatus(output);
        if (status is 0 or 1) return;

        throw new LunarSocialException(status switch
        {
            2 => "会话不存在，先打开会话再发",
            3 => "这条消息被服务端判定为不合适",
            4 => "消息太长了",
            _ => $"发送失败（状态 {status}）"
        });
    }

    #region 收包

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var (frame, closeReason) = await ReceiveFrameAsync(socket, buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (frame is null)
                {
                    if (closeReason is not null) LastCloseReason = closeReason;
                    break;
                }

                HandleFrame(frame);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 断线 / 主动关闭，交给外层状态机处理
            if (LastCloseReason is null) LastCloseReason = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_socket, socket))
            {
                _connected = false;
                FailAllPending("连接已断开" + (LastCloseReason is null ? "" : "（" + LastCloseReason + "）"));
                StateChanged?.Invoke();
            }
        }
    }

    private void HandleFrame(byte[] frame)
    {
        var message = LunarSocialProtocol.DecodeClientbound(frame);

        if (message.IsRpc)
        {
            TaskCompletionSource<byte[]>? waiter;
            lock (_pendingLock)
            {
                if (!_pending.Remove(message.RequestId!, out waiter)) waiter = null;
            }

            waiter?.TrySetResult(message.Output ?? []);
            return;
        }

        if (message.PushTypeUrl is null || message.PushPayload is null) return;

        switch (message.PushTypeUrl)
        {
            case "lunarclient.websocket.conversation.v1.ConversationMessagePush":
            {
                var (conversationId, _, chatMessage) =
                    LunarSocialProtocol.DecodeMessagePush(message.PushPayload, _selfUuid.ToString());
                if (chatMessage is not null && conversationId.Length > 0)
                    MessageReceived?.Invoke(conversationId, chatMessage);
                break;
            }
            case "lunarclient.websocket.friend.v1.FriendStatusPush":
            case "lunarclient.websocket.friend.v1.FriendRequestReceivedPush":
            case "lunarclient.websocket.friend.v1.FriendRequestAcceptedPush":
            case "lunarclient.websocket.friend.v1.FriendRemovedYouPush":
                _ = RefreshFriendsAsync();
                break;
        }
    }

    private async Task RefreshFriendsAsync()
    {
        try
        {
            await LoadFriendsAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException or ObjectDisposedException)
        {
            // 好友列表刷不出来不值得断连接，等下一次推送
        }
    }

    #endregion

    #region 发包

    private async Task<byte[]> CallAsync(string service, string method, byte[] input,
        CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            throw new LunarSocialException("还没连上 Lunar 后端");

        var requestId = Interlocked.Increment(ref _requestCounter).ToString();
        var waiter = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[requestId] = waiter;

        try
        {
            var frame = LunarSocialProtocol.EncodeRpcRequest(requestId, service, method, input);
            await SendRawAsync(socket, frame, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(RpcTimeout);
            await using var registration = timeout.Token.Register(() => waiter.TrySetCanceled()).ConfigureAwait(false);
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingLock) _pending.Remove(requestId);
        }
    }

    private async Task SendRawAsync(ClientWebSocket socket, byte[] payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _writeGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 连接已经释放，无所谓了
            }
        }
    }

    private static async Task<(byte[]? Frame, string? CloseReason)> ReceiveFrameAsync(ClientWebSocket socket,
        byte[] buffer, CancellationToken cancellationToken)
    {
        using var stream = new System.IO.MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return (null, $"{(int?)result.CloseStatus} {result.CloseStatusDescription}".Trim());

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return (stream.ToArray(), null);
        }
    }

    private void FailAllPending(string reason)
    {
        TaskCompletionSource<byte[]>[] waiters;
        lock (_pendingLock)
        {
            waiters = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var waiter in waiters) waiter.TrySetException(new LunarSocialException(reason));
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _writeGate.Dispose();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // 收包循环本来就要退出
            }
        }
    }
}
