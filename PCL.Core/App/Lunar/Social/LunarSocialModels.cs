using System;
using System.Collections.Generic;
using System.Globalization;

namespace PCL.Core.App.Lunar.Social;

/// <summary>
///     Lunar 用的 128 位 UUID。
///     <para>
///         协议里是 <c>{ fixed64 high64 = 1; fixed64 low64 = 2; }</c>，也就是把标准
///         UUID 字符串的前三段拼成高 64 位、后两段拼成低 64 位。官方启动器用 BigInt
///         算，这里用 <see cref="ulong" />。
///     </para>
/// </summary>
public readonly record struct LunarUuid(ulong High, ulong Low)
{
    public bool IsEmpty => High == 0 && Low == 0;

    /// <summary>解析带连字符或 32 位纯十六进制的 UUID；不合法时返回全零。</summary>
    public static LunarUuid Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;
        var text = value.Replace("-", "");
        if (text.Length != 32) return default;

        var high = ulong.Parse(text[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var low = ulong.Parse(text[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new LunarUuid(high, low);
    }

    /// <summary>带连字符的标准形式（<c>8-4-4-4-12</c>），也是 Mojang 接口要的形状。</summary>
    public override string ToString()
    {
        var text = High.ToString("x16", CultureInfo.InvariantCulture) +
                   Low.ToString("x16", CultureInfo.InvariantCulture);
        return $"{text[..8]}-{text[8..12]}-{text[12..16]}-{text[16..20]}-{text[20..]}";
    }

    /// <summary>去掉连字符的形式，<c>sessionserver</c> 的 <c>selectedProfile</c> 要这个。</summary>
    public string ToCompactString() =>
        High.ToString("x16", CultureInfo.InvariantCulture) + Low.ToString("x16", CultureInfo.InvariantCulture);
}

/// <summary>好友的在线状态，取值来自 <c>friend.v1.OnlineFriendStatus</c>。</summary>
public enum LunarFriendStatus
{
    Unknown = 0,
    Online = 1,
    Away = 2,
    Busy = 3,
    Invisible = 4
}

/// <summary>
///     好友列表里的一条。
///     <para>
///         <see cref="Where" /> 是从 <c>Location</c> 那个 oneof 里挑出来的人话描述
///         （服务器名 / 单人游戏 / 菜单…）。Lunar 的 <c>Location</c> 有 11 个分支，
///         大部分分支里没有可展示的内容，所以只取有意义的几个。
///     </para>
/// </summary>
public sealed record LunarFriend(
    string Uuid,
    string Name,
    LunarFriendStatus Status,
    string Where,
    string RankName)
{
    public bool IsOnline => Status is LunarFriendStatus.Online or LunarFriendStatus.Away or LunarFriendStatus.Busy;
}

/// <summary>一条私聊消息。</summary>
public sealed record LunarChatMessage(
    string Id,
    string SenderUuid,
    string SenderName,
    string Text,
    DateTimeOffset SentAt,
    bool Mine);

/// <summary>一个会话（现在只做一对一的私聊）。</summary>
public sealed record LunarConversation(
    string Id,
    string PeerUuid,
    string PeerName,
    int Unread,
    IReadOnlyList<LunarChatMessage> Messages);
