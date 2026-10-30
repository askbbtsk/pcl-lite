using System;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;

namespace PCL;

/// <summary>
///     把 Lunar 游戏进程发来的 IPC 请求落到 PCL 的实际动作上。
///     <para>
///         协议细节见 <see cref="LunarIpcDispatcher" />。这里只做「能不能做、怎么做」，
///         凡是 PCL 没有对应能力的（启动器聊天面板、内嵌浏览器、电台、推广），
///         一律在分派层就降级掉，不会走到这里。
///     </para>
/// </summary>
internal sealed class LunarIpcHost : ILunarIpcHost
{
    /// <summary>
    ///     游戏内点链接（商店 / 公告 / 帮助 / 社交链接）。用系统默认浏览器打开。
    ///     <para>
    ///         这里刻意只放行 http / https：游戏进程能通过本地 IPC 让 PCL 执行
    ///         <c>Process.Start</c>，不限制协议就等于把 <c>file://</c>、<c>ms-*:</c>
    ///         这些交给游戏随便调。官方启动器的内嵌窗口 PCL 没有，所以
    ///         <c>FORCE_EMBEDDED</c> 也降级成外部浏览器。
    ///     </para>
    /// </summary>
    public bool OpenUrl(string url, LunarUrlInitiator initiator, LunarOpenType openType)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        if (openType == LunarOpenType.ForceEmbedded)
            ModBase.Log($"[Lunar IPC] 游戏要求内嵌打开（{initiator}），PCL 没有内嵌浏览器，改用系统浏览器");

        ModBase.RunInUi(() => ModBase.OpenWebsite(url));
        return true;
    }

    /// <summary>
    ///     游戏内点了「添加账号」。PCL 没法代替游戏登录，只能把用户带到能加账号的地方，
    ///     然后由分派层回 <c>success = false</c> —— 游戏侧会提示登录失败，这是实话。
    /// </summary>
    public void RequestAddAccount()
    {
        ModBase.RunInUi(() =>
        {
            ModMain.frmMain.PageChange(FormMain.PageType.Launch);
            HintService.Hint(Lang.Text("Lunar.Ipc.AddAccount"), HintType.Info);
        });
    }

    /// <summary>
    ///     游戏内请求「换版本重开」。
    ///     <para>
    ///         官方启动器是先回响应、再延迟 1 秒自己重启。PCL 这边**不自动杀进程**：
    ///         游戏正在跑的时候强杀会丢掉未保存的世界，代价太大。这里只把请求摊给用户看，
    ///         由用户决定什么时候切。想改成真重启的话，入口就是这里。
    ///     </para>
    /// </summary>
    public void RequestLaunchGame(string version, string module, string serverIp, int launchSource)
    {
        var target = string.IsNullOrWhiteSpace(version) ? Lang.Text("Lunar.Ipc.LaunchUnknown") : version;
        if (!string.IsNullOrWhiteSpace(module)) target += " / " + module;
        if (!string.IsNullOrWhiteSpace(serverIp)) target += " @ " + serverIp;

        ModBase.Log($"[Lunar IPC] 游戏请求重开：{target}（launchSource={launchSource}）");

        ModBase.RunInUi(() =>
        {
            ModMain.frmMain.PageChange(FormMain.PageType.Launch);
            HintService.Hint(Lang.Text("Lunar.Ipc.LaunchGame", target), HintType.Info);
        });
    }
}
