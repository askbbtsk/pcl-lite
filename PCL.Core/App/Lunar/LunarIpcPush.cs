using System;
using System.IO;

namespace PCL.Core.App.Lunar;

/// <summary>
///     启动器主动推给游戏的一条通知（<c>push_notification</c>）。
///     <para>
///         消息定义和 <c>type_url</c> 都来自官方启动器 <c>app.asar</c> 里的 protobuf 生成代码，
///         游戏端认不认是另一回事 —— 能不能用要看 <c>lunar.jar</c> 里有没有注册处理器。
///         目前的实测结论（<c>docs/LUNAR.md</c> 有完整记录）：
///     </para>
///     <list type="bullet">
///         <item><see cref="ExitGame" />、<see cref="FocusGame" />、<see cref="PlayServer" /> —— 游戏真的处理</item>
///         <item>聊天推送 <c>chat.v1.SendChatPush</c> —— 游戏端**没有注册处理器**，推了会被丢掉</item>
///         <item>推广 / 商店预览 / 电台 / 结账关闭 —— 游戏处理，但 PCL 没有对应界面，推了也没有意义</item>
///     </list>
/// </summary>
/// <param name="TypeUrl">形如 <c>type.googleapis.com/&lt;消息全名&gt;</c>。</param>
/// <param name="Payload">消息体的 protobuf 字节，空消息就是空数组。</param>
public sealed record LunarIpcPush(string TypeUrl, byte[] Payload)
{
    /// <summary>Any 里 type_url 的固定前缀，官方启动器也是这么拼的。</summary>
    public const string TypeUrlPrefix = "type.googleapis.com/";

    private const string LaunchPackage = "lunarclient.gameipc.launch.v1.";
    private const string LocationPackage = "lunarclient.gameipc.location.v1.";

    /// <summary>
    ///     请游戏自己退出。
    ///     <para>
    ///         游戏端的处理器是 <c>bridge$shutdownMinecraftApplet()</c>，走的是 Minecraft
    ///         正常的关闭流程：存档落盘、跑 shutdown hook、退出码 0。比起
    ///         <c>Process.Kill</c> 直接砍掉进程，这是唯一不丢存档的做法。
    ///     </para>
    ///     <para>
    ///         消息体是空的 —— <c>ExitGamePush</c> 一个字段都没有。
    ///     </para>
    /// </summary>
    public static LunarIpcPush ExitGame { get; } =
        new(TypeUrlPrefix + LaunchPackage + "ExitGamePush", []);

    /// <summary>
    ///     把游戏窗口切到前台。消息体同样是空的。
    ///     <para>
    ///         游戏端收到后会调 <c>bridge$displayGuiScreen(null)</c> 一类的逻辑把窗口拉起来。
    ///         PCL 这边目前没有入口调用它，留着给「回到游戏」按钮用。
    ///     </para>
    /// </summary>
    public static LunarIpcPush FocusGame { get; } =
        new(TypeUrlPrefix + LocationPackage + "FocusGamePush", []);

    /// <summary>
    ///     让已经在跑的游戏直接连一个服务器。
    ///     <para>
    ///         游戏端要求当前**已经进了世界**（有 session），否则只打一条日志就忽略。
    ///         PCL 没有服务器列表页，所以现在也没有入口调用它。
    ///     </para>
    /// </summary>
    /// <param name="serverIp">形如 <c>mc.example.com</c> 或 <c>mc.example.com:25565</c>。</param>
    public static LunarIpcPush PlayServer(string serverIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverIp);

        // PlayServerPush { string autoJoinServerIp = 1; }
        using var payload = new MemoryStream();
        LunarIpcProtocol.WriteStringField(payload, 1, serverIp);
        return new LunarIpcPush(TypeUrlPrefix + LaunchPackage + "PlayServerPush", payload.ToArray());
    }
}
