using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace PCL.Core.Link.Discord;

/// <summary>
/// 把状态快照渲染成 Discord activity JSON。
/// <para>
/// 刻意做成纯函数：不碰配置、不碰进程、不碰 UI，这样两种分支（游戏中 / 启动器中）都能单独验证，
/// 不需要真的开一个 Minecraft。
/// </para>
/// </summary>
public static class DiscordActivityBuilder
{
    /// <summary>Discord 对 details / state 的硬限制。</summary>
    private const int MaxTextLength = 128;

    /// <summary>项目主页，作为状态卡片上的按钮。</summary>
    private const string HomepageUrl = "https://github.com/askbbtsk/pcl-lite";

    /// <param name="game">游戏快照；<see langword="null"/> 或 <c>Running=false</c> 表示未开游戏。</param>
    /// <param name="fields">要展示的项，取值见 <see cref="DiscordRpcFields"/>。</param>
    /// <param name="launcherStart">启动器启动时间，未开游戏时用作时长起点。</param>
    /// <param name="gameStart">游戏启动时间，开游戏时用作时长起点。</param>
    /// <param name="now">当前时间（注入便于测试）。</param>
    /// <param name="launcherMemory">启动器工作集，未开游戏时展示。</param>
    /// <param name="launcherVersion">启动器版本名，如 <c>1.2.1-beta</c>。</param>
    public static string Build(
        DiscordGameSnapshot? game,
        IReadOnlySet<string> fields,
        DateTime launcherStart,
        DateTime? gameStart,
        DateTime now,
        long launcherMemory,
        string? launcherVersion)
    {
        var isGame = game is { Running: true };
        var startedAt = isGame ? gameStart ?? now : launcherStart;

        var activity = new JsonObject
        {
            // Discord 的 activity 类型：0 = Playing。不写时 Discord 会默认补 0，
            // 但显式写出来可以避免以后换客户端版本时行为漂移。
            ["type"] = 0,
            ["details"] = _Clamp(_BuildDetails(game, fields, isGame, launcherVersion), MaxTextLength),
            // 不让 Discord 显示成「正在玩 XXX」——那不是游戏本体
            ["instance"] = false,
            ["timestamps"] = new JsonObject
            {
                ["start"] = new DateTimeOffset(startedAt.ToUniversalTime()).ToUnixTimeMilliseconds()
            },
            ["buttons"] = new JsonArray
            {
                new JsonObject
                {
                    ["label"] = "获取 PCL Lite",
                    ["url"] = HomepageUrl
                }
            }
        };

        var state = _BuildState(game, fields, isGame, now, gameStart, launcherStart, launcherMemory);
        if (state.Length > 0) activity["state"] = _Clamp(state, MaxTextLength);

        return activity.ToJsonString();
    }

    private static string _BuildDetails(
        DiscordGameSnapshot? game, IReadOnlySet<string> fields, bool isGame, string? launcherVersion)
    {
        if (isGame)
        {
            if (fields.Contains(DiscordRpcFields.GameVersion)
                && !string.IsNullOrWhiteSpace(game!.InstanceName))
                return $"正在游玩 {game.InstanceName}";
            return "正在游玩 Minecraft";
        }

        if (fields.Contains(DiscordRpcFields.LauncherVersion) && !string.IsNullOrWhiteSpace(launcherVersion))
            return $"PCL Lite {launcherVersion}";

        return "PCL Lite";
    }

    private static string _BuildState(
        DiscordGameSnapshot? game, IReadOnlySet<string> fields, bool isGame, DateTime now,
        DateTime? gameStart, DateTime launcherStart, long launcherMemory)
    {
        var parts = new List<string>(4);

        if (fields.Contains(DiscordRpcFields.PlayTime))
        {
            var elapsed = now - (isGame ? gameStart ?? now : launcherStart);
            parts.Add(isGame ? $"已游玩 {FormatDuration(elapsed)}" : $"已运行 {FormatDuration(elapsed)}");
        }

        if (isGame)
        {
            if (fields.Contains(DiscordRpcFields.GameMemory) && game!.MemoryBytes > 0)
                parts.Add($"游戏 {FormatBytes(game.MemoryBytes)}");
        }
        else if (fields.Contains(DiscordRpcFields.LauncherMemory))
        {
            parts.Add($"启动器 {FormatBytes(launcherMemory)}");
        }

        if (isGame && fields.Contains(DiscordRpcFields.ServerAddress)
                     && !string.IsNullOrWhiteSpace(game!.ServerAddress))
            parts.Add(game.ServerAddress!);

        return string.Join(" · ", parts);
    }

    /// <summary>把时长写成人话：不到 1 分钟 / N 分钟 / N 小时 M 分。</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1) return $"{span.Minutes} 分钟";
        return "不到 1 分钟";
    }

    /// <summary>字节数按 1024 进制换算，超过 1 GB 用一位小数。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        const double mb = 1024d * 1024d;
        if (bytes >= 1024d * mb) return $"{bytes / (1024d * mb):0.0} GB";
        return $"{bytes / mb:0} MB";
    }

    private static string _Clamp(string text, int max)
    {
        if (text.Length <= max) return text;
        var sb = new StringBuilder(max);
        foreach (var ch in text)
        {
            if (sb.Length >= max - 1) break;
            sb.Append(ch);
        }

        return sb.Append('…').ToString();
    }
}
