using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.Link.Discord;

namespace PCL;

/// <summary>
/// 把启动器的运行时状态接到 Discord Rich Presence 上。
/// <para>
/// PCL.Core 里的 RPC 服务不引用 WPF，所以「游戏进程活着没、哪个实例、占多少内存、连的哪个服」
/// 这些只有前端知道的信息由这里注入。服务本身因此可以脱离 UI 单独测试。
/// </para>
/// </summary>
public static class ModDiscord
{
    private static bool _initialized;

    // 日志解析缓存：文件没变就不重复读，避免每 5 秒扫一遍几百 KB
    private static string? _logPath;
    private static long _logLength = -1;
    private static string? _serverCache;

    /// <summary>Minecraft 客户端连接服务器时打的那行：<c>Connecting to mc.example.com, 25565</c>。</summary>
    private static readonly Regex ServerRegex = new(@"Connecting to ([^\s,]+), (\d+)", RegexOptions.Compiled);

    /// <summary>在启动器初始化阶段调用一次。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        DiscordHostBridge.GameSnapshotProvider = GetSnapshot;

        // 开关 / 应用 ID / 显示项 任何一项变化都立刻重发一次
        Config.Discord.EnabledConfig.Observe(
            new ConfigObserver(ConfigEvent.Changed, _ => DiscordRichPresenceService.RequestRefresh()));
        Config.Discord.AppIdConfig.Observe(
            new ConfigObserver(ConfigEvent.Changed, _ => DiscordRichPresenceService.RequestRefresh()));
        Config.Discord.FieldsConfig.Observe(
            new ConfigObserver(ConfigEvent.Changed, _ => DiscordRichPresenceService.RequestRefresh()));
    }

    /// <summary>
    /// 读取当前快照。可能被线程池线程调用，内部会切到 UI 线程。
    /// </summary>
    private static DiscordGameSnapshot? GetSnapshot()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(GetSnapshot);

        try
        {
            return BuildSnapshot();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Discord] 读取游戏状态失败", ModBase.LogLevel.Debug);
            return null;
        }
    }

    private static DiscordGameSnapshot BuildSnapshot()
    {
        var process = ModLaunch.mcLaunchProcess;
        var running = false;
        long memory = 0;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    running = true;
                    process.Refresh();
                    memory = process.WorkingSet64;
                }
            }
            catch (Exception)
            {
                running = false;
            }
        }

        var instance = ModLaunch.currentLaunchOptions?.instance ?? ModInstanceList.McMcInstanceSelected;
        string? instanceName = null;
        try
        {
            instanceName = instance?.Name;
        }
        catch (Exception)
        {
            // 实例没加载完时 Name 可能抛，忽略
        }

        if (!running)
        {
            // 游戏没开就别留着上一次的服务器地址
            _serverCache = null;
            _logPath = null;
            _logLength = -1;
            return new DiscordGameSnapshot(false, instanceName, 0, null);
        }

        return new DiscordGameSnapshot(true, instanceName, memory, ReadServerAddress(instance));
    }

    #region 服务器地址

    /// <summary>
    /// 从 <c>logs/latest.log</c> 里取最近一次连接的服务器地址。
    /// <para>
    /// 只读文件尾部 256 KB，并且用「路径 + 长度」做缓存键——游戏在跑的时候这个文件一直在长，
    /// 每 5 秒全量读一遍不划算。
    /// </para>
    /// </summary>
    private static string? ReadServerAddress(McInstance? instance)
    {
        var path = ResolveLogPath(instance);
        if (path is null) return null;

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception)
        {
            return null;
        }

        if (path == _logPath && info.Length == _logLength) return _serverCache;

        string? found = null;
        try
        {
            foreach (Match match in ServerRegex.Matches(ReadTail(path, 256 * 1024)))
            {
                var host = match.Groups[1].Value;
                var port = match.Groups[2].Value;
                found = port == "25565" ? host : $"{host}:{port}";
            }
        }
        catch (Exception)
        {
            return _serverCache;
        }

        _logPath = path;
        _logLength = info.Length;
        _serverCache = found;
        return found;
    }

    private static string? ResolveLogPath(McInstance? instance)
    {
        try
        {
            var root = instance?.PathIndie;
            if (!string.IsNullOrWhiteSpace(root))
            {
                var path = Path.Combine(root, "logs", "latest.log");
                if (File.Exists(path)) return path;
            }
        }
        catch (Exception)
        {
            // PathIndie 在实例未加载时可能触发 Load，失败就算了
        }

        var fallback = ModFolder.mcFolderSelected;
        if (string.IsNullOrWhiteSpace(fallback)) return null;

        var direct = Path.Combine(fallback, "logs", "latest.log");
        if (File.Exists(direct)) return direct;

        var nested = Path.Combine(fallback, ".minecraft", "logs", "latest.log");
        return File.Exists(nested) ? nested : null;
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var take = (int)Math.Min(length, maxBytes);
        stream.Seek(length - take, SeekOrigin.Begin);

        var buffer = new byte[take];
        var read = 0;
        while (read < take)
        {
            var n = stream.Read(buffer, read, take - read);
            if (n <= 0) break;
            read += n;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    #endregion
}
