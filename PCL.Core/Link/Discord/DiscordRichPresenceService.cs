using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.IoC;

namespace PCL.Core.Link.Discord;

/// <summary>
/// Discord Rich Presence 服务。
/// <para>
/// 每 5 秒取一次状态，内容没变就不发（Discord 对 SET_ACTIVITY 有 20 秒 5 次的限流）。
/// 时长靠 <c>timestamps.start</c> 让 Discord 自己跳动，所以不需要高频刷新。
/// </para>
/// </summary>
[LifecycleService(LifecycleState.Loaded)]
public sealed class DiscordRichPresenceService() : GeneralService("discord-rpc", "DiscordRichPresence", false)
{
    /// <summary>
    ///     本项目的 Discord 应用 ID，写死在这里。
    ///     <para>
    ///     以前它是个可配置项，但那个输入框除了让人填错之外没有任何用处：填成别人的应用，
    ///     状态卡片上显示的就是别人的应用名和图标；填错一个字符就整条链路连不上，
    ///     而用户根本无从判断是自己填错了还是 Discord 没开。现在固定成本项目的应用，
    ///     不给改，也就没有「填错了」这种状态。
    ///     </para>
    /// </summary>
    public const string AppId = "1563608700969689108";

    private const int TickIntervalMs = 5000;

    /// <summary>
    ///     连接失败后的重试间隔。
    ///     <para>
    ///     Discord 没开的时候别每 5 秒折腾一次；但也不能太长——Discord 后启动、或者管道
    ///     被对端悄悄关掉之后，用户要等这么久状态才回来。15 秒是「不折腾」和「别让人干等」的折中。
    ///     </para>
    /// </summary>
    private const int ConnectRetryMs = 15000;

    /// <summary>
    ///     连续多少次读不到游戏才算真的退出了。
    ///     <para>
    ///     单次读不到不等于退出：UI 线程忙着加载、进程对象瞬时拿不到、<c>Refresh()</c> 抛个异常，
    ///     都会让快照变成「没在运行」。以前一读到就立刻清空开始时间，于是 <c>timestamps.start</c>
    ///     归零重来，用户看到的就是「游玩时长莫名其妙从头开始」。连续三次（15 秒）才算数。
    ///     </para>
    /// </summary>
    private const int GameLossGraceTicks = 3;

    /// <summary>
    ///     这么久没有成功上报过任何东西，就认定连接已经僵死，强制重连。
    ///     <para>
    ///     <c>NamedPipeClientStream.IsConnected</c> 在对端静默消失后仍可能是 <c>true</c>，
    ///     于是 SET_ACTIVITY 一直写进黑洞、状态却停在最后一帧不动。只靠 <c>IsConnected</c>
    ///     判断「还连着」是不够的。
    ///     </para>
    /// </summary>
    private const int StaleConnectionMs = 90000;

    /// <summary>
    ///     内容没变时也定期重发一次的间隔。
    ///     <para>
    ///     Discord 一次只显示一个「正在玩」，别的 RPC 应用（例如各种 Electron 小工具）
    ///     会把它抢走。原来我们内容不变就不再发，一旦被抢走就再也回不来，用户看到的就是
    ///     「Discord 里没有 PCL Lite」。定期重发一次就能抢回来。
    ///     </para>
    ///     <para>30 秒一次，仍低于 SET_ACTIVITY 的 20 秒 5 次限流。</para>
    /// </summary>
    private const int ReassertIntervalMs = 30000;

    private static readonly Process _LauncherProcess = Process.GetCurrentProcess();
    private static readonly DateTime _LauncherStart = _GetLauncherStart();

    private static Timer? _timer;
    private static DiscordIpcClient? _client;
    private static LifecycleContext? _context;
    private static DateTime? _gameStart;

    /// <summary>连续多少次没读到「游戏在跑」。用于给退出判定加宽限。</summary>
    private static int _missingGameTicks;

    private static string? _lastActivity;

    /// <summary>上次真正发出 SET_ACTIVITY 的时刻，用于定期重发。</summary>
    private static long _lastSentTicks;
    private static long _nextConnectAttemptTicks;
    private static string _status = "未启用";

    /// <summary>用户手动"在 Discord 中清除"后置位，此时保持连接但不再上报。</summary>
    private static bool _paused;

    /// <summary>最近一次断开原因，用来给用户一个说得清的错误提示。</summary>
    private static string? _lastDropReason;

    /// <summary>当前连接状态，供设置页显示。</summary>
    public static string StatusText => _status;

    /// <summary>状态变化通知（可能来自线程池线程）。</summary>
    public static event Action? StatusChanged;

    private static DateTime _GetLauncherStart()
    {
        try
        {
            return _LauncherProcess.StartTime;
        }
        catch (Exception)
        {
            return DateTime.Now;
        }
    }

    public override void Start()
    {
        _context = ServiceContext;
        _timer = new Timer(_Tick, null, 1500, TickIntervalMs);
    }

    public override void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _ClearAndDisconnect();
        _client?.Dispose();
        _client = null;
    }

    /// <summary>
    /// 配置变化后立刻刷新一次，不必等下一个 tick。
    /// </summary>
    public static void RequestRefresh()
    {
        _paused = false;
        _lastActivity = null;
        _nextConnectAttemptTicks = 0;
        // 刚恢复就判「长时间没发过」会把一条好端端的连接断掉，重新计时。
        // 连接要是真死了，下面这次 SetActivity 会失败，照样会走重连。
        _lastSentTicks = Environment.TickCount64;
        _Tick(null);
    }

    /// <summary>
    /// 在 Discord 里把状态清掉，但保持连接（之后改任何配置会自动恢复）。
    /// </summary>
    public static void ClearActivity()
    {
        _paused = true;
        _lastActivity = null;
        try
        {
            _client?.SetActivity(null, out _);
        }
        catch (Exception)
        {
            // 清不掉也没关系，Discord 会在进程退出后移除
        }

        _SetStatus(_client is { IsConnected: true } ? "已连接 · 状态已隐藏" : "未连接");
    }

    /// <summary>
    /// 游戏启动/退出时调用，立刻切到对应状态。
    /// </summary>
    public static void NotifyGameStateChanged()
    {
        _lastActivity = null;
        _Tick(null);
    }

    private static void _Tick(object? _)
    {
        try
        {
            _TickCore();
        }
        catch (Exception ex)
        {
            _SetStatus($"异常：{ex.Message}");
        }
    }

    private static void _TickCore()
    {
        if (!Config.Discord.Enabled)
        {
            if (_client is not null)
            {
                _ClearAndDisconnect();
                _client.Dispose();
                _client = null;
            }

            _SetStatus("已关闭");
            return;
        }

        // 连接（失败后按退避重试）
        // 暂停（用户手动清除状态）时不查僵死：那本来就不发东西，按「没发过」判失效会
        // 让连接每 90 秒被断开重连一次，纯属自找麻烦。
        if (!_paused && _client is { IsConnected: true } &&
            Environment.TickCount64 - _lastSentTicks > StaleConnectionMs)
        {
            // 管道还报「已连接」，但已经很久没成功发出去过东西了。
            // 这种情况 IsConnected 是假象，不主动断开就会一直停在最后一帧。
            _DropClient("连接已失效（长时间没有成功上报）");
            return;
        }

        if (_client is not { IsConnected: true })
        {
            if (_client is not null)
            {
                _client.Dispose();
                _client = null;
            }

            if (Environment.TickCount64 < _nextConnectAttemptTicks) return;

            var client = new DiscordIpcClient();
            if (!client.Connect(AppId, out var error))
            {
                client.Dispose();
                _nextConnectAttemptTicks = Environment.TickCount64 + ConnectRetryMs;
                _SetStatus(error ?? "连接失败");
                return;
            }

            client.Disconnected += reason =>
            {
                _lastDropReason = reason;
                _SetStatus($"连接断开：{reason}");
                _lastActivity = null;
            };
            _client = client;
        }

        var game = _ReadGameSnapshot();
        _TrackGameStart(game);

        if (_paused)
        {
            _SetStatus("已连接 · 状态已隐藏");
            return;
        }

        var activity = DiscordActivityBuilder.Build(
            game,
            _ParseFields(Config.Discord.Fields),
            _LauncherStart,
            _gameStart,
            DateTime.Now,
            _ReadLauncherMemory(),
            Basics.Metadata.Version.BaseName);

        // 内容没变就不发，省限流额度；但每隔一段时间必须重发一次，否则被别的
        // RPC 应用抢走的「正在玩」位置就再也拿不回来了。
        var unchanged = activity == _lastActivity;
        if (unchanged && Environment.TickCount64 - _lastSentTicks < ReassertIntervalMs) return;

        // 握手成功不代表管道还活着：Discord 对无效的应用 ID 会先回 READY，随后立刻断开。
        // 这里再确认一次，避免把「未连接」当成发送失败报给用户。
        if (_client is not { IsConnected: true })
        {
            _DropClient(_lastDropReason ?? "Discord 已断开连接（应用 ID 可能无效）");
            return;
        }

        if (_client.SetActivity(activity, out var setError))
        {
            _lastActivity = activity;
            _lastSentTicks = Environment.TickCount64;
            _SetStatus(game is { Running: true } ? "已连接 · 正在游玩" : "已连接 · 启动器中");
            return;
        }

        _DropClient(_lastDropReason ?? setError ?? "发送失败");
    }

    /// <summary>
    /// 丢掉当前连接，等下一个重试周期再连。避免每 5 秒对着一个死管道重试。
    /// </summary>
    private static void _DropClient(string reason)
    {
        _SetStatus($"连接断开：{reason}");
        _client?.Dispose();
        _client = null;
        _lastActivity = null;
        _lastDropReason = null;
        _nextConnectAttemptTicks = Environment.TickCount64 + ConnectRetryMs;
    }

    private static DiscordGameSnapshot? _ReadGameSnapshot()
    {
        try
        {
            return DiscordHostBridge.GameSnapshotProvider?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void _TrackGameStart(DiscordGameSnapshot? game)
    {
        if (game is { Running: true })
        {
            _missingGameTicks = 0;
            // 优先用游戏进程自己的启动时间：万一中间被误判成「没在运行」，
            // 重新拿到时起点还是同一个，游玩时长不会归零重算。
            _gameStart ??= game.StartedAt ?? DateTime.Now;
            return;
        }

        if (_gameStart is null)
        {
            _missingGameTicks = 0;
            return;
        }

        if (++_missingGameTicks < GameLossGraceTicks) return;

        _gameStart = null;
        _missingGameTicks = 0;
    }

    private static HashSet<string> _ParseFields(string? raw)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(part);
        return result;
    }

    private static long _ReadLauncherMemory()
    {
        try
        {
            _LauncherProcess.Refresh();
            return _LauncherProcess.WorkingSet64;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void _ClearAndDisconnect()
    {
        try
        {
            _client?.SetActivity(null, out _);
        }
        catch (Exception)
        {
            // 断开时清不掉也无所谓，Discord 会在进程消失后自动移除
        }

        _client?.Disconnect();
        _lastActivity = null;
        _gameStart = null;
    }

    private static void _SetStatus(string text)
    {
        if (_status == text) return;
        _status = text;
        _context?.Info($"[Discord] {text}");
        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception)
        {
            // 状态订阅者自己炸了不该影响服务
        }
    }
}
