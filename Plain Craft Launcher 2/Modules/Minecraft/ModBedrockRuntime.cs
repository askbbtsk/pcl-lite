using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.Bedrock;

namespace PCL;

/// <summary>
///     基岩版的运行时状态：检测结果、当前选中实例、启动与停止。
///     <para>
///         左栏底部的「启动基岩版」按钮和右栏的版本列表读写的是同一份状态，
///         所以抽成静态类，两边都订阅 <see cref="StateChanged" /> 刷新界面。
///         所有公开方法都应在 UI 线程调用（<see cref="Refresh" /> 内部会把结果切回 UI 线程）。
///     </para>
/// </summary>
public static class ModBedrockRuntime
{
    /// <summary>状态或列表发生变化，UI 线程触发。</summary>
    public static event Action? StateChanged;

    /// <summary>本机可启动的基岩版列表。</summary>
    public static IReadOnlyList<BedrockInstall> Installs { get; private set; } = [];

    /// <summary>
    ///     <see cref="Installs" /> 的版本号，只在列表内容真正变化时自增。
    ///     <para>
    ///         解包进度每秒会把状态刷几十次，每次都会触发 <see cref="StateChanged" />；
    ///         列表页据此判断「要不要重建列表」，避免为了刷新一个百分比把整个列表重画一遍。
    ///     </para>
    /// </summary>
    public static int Revision { get; private set; }

    /// <summary>当前选中的实例，左栏按钮启动的就是它。</summary>
    public static BedrockInstall? Current { get; private set; }

    /// <summary>状态标题，例如「已就绪」。</summary>
    public static string StatusText { get; private set; } = Lang.Text("Bedrock.Launch.Detecting");

    /// <summary>状态详情，通常是版本名或错误原因。</summary>
    public static string DetailText { get; private set; } = "";

    private static bool _detecting;

    /// <summary>是否正在检测，此时启动按钮应禁用。</summary>
    public static bool IsBusy => _detecting;

    /// <summary>重新检测本机所有基岩版。正在检测时重复调用会被忽略，<paramref name="force" /> 为 true 时除外。</summary>
    /// <param name="force">
    ///     忽略「正在检测」的标记强制重来。安装完成后必须用它：
    ///     否则上一轮还没跑完的检测会把这个请求吞掉，新解包的版本就一直不出现在列表里。
    /// </param>
    public static void Refresh(bool force = false)
    {
        if (_detecting && !force) return;
        _detecting = true;
        SetState(Lang.Text("Bedrock.Launch.Detecting"), "");

        var directories = GetConfiguredDirectories();
        Task.Run(() =>
        {
            IReadOnlyList<BedrockInstall> merged;
            string? error;
            try
            {
                var store = BedrockService.Detect(directories, out error);
                // 下载页解包出来的 GDK 版本不向系统注册，只能自己扫目录
                var downloaded = BedrockService.DetectDownloaded(ModBedrockDownload.VersionsDirectory);
                merged = store
                    .Concat(downloaded)
                    .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            }
            catch (Exception ex)
            {
                // 检测本身炸掉时不能把 _detecting 留在 true，否则后续刷新全部失效
                ModBase.Log(ex, "[Bedrock] 检测已安装版本失败", ModBase.LogLevel.Normal);
                merged = Installs;
                error = ex.Message;
            }

            ModBase.RunInUi(() =>
            {
                // 先放开标记，后面 SetState 触发的界面刷新才能看到「不在检测中」
                _detecting = false;
                try
                {
                    Installs = merged;
                    Revision++;

                    // 尽量保留原来选中的版本，失效则退回第一个
                    var keep = Current is null
                        ? null
                        : merged.FirstOrDefault(i =>
                            string.Equals(i.Id, Current.Id, StringComparison.OrdinalIgnoreCase));
                    Current = keep ?? merged.FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(error))
                        SetState(Lang.Text("Bedrock.Launch.DetectFailed", error!), Current?.DisplayName ?? "");
                    else if (merged.Count == 0)
                        SetState(Lang.Text("Bedrock.Status.NotFound"), Lang.Text("Bedrock.Launch.Empty"));
                    else
                        SetState(
                            BedrockService.IsRunning()
                                ? Lang.Text("Bedrock.Launch.Running")
                                : Lang.Text("Bedrock.Status.Ready"),
                            Current?.DisplayName ?? "");
                }
                finally
                {
                    // 无论如何都要放开，否则后续刷新会被永久拦住
                    _detecting = false;
                    RaiseChanged();
                }
            });
        });
    }

    /// <summary>切换当前实例。</summary>
    public static void Select(BedrockInstall? install)
    {
        if (install is not null &&
            string.Equals(install.Id, Current?.Id, StringComparison.OrdinalIgnoreCase))
            return;

        Current = install;
        if (install is null)
        {
            RaiseChanged();
            return;
        }

        SetState(
            BedrockService.IsRunning()
                ? Lang.Text("Bedrock.Launch.Running")
                : Lang.Text("Bedrock.Status.Ready"),
            install.DisplayName);
    }

    /// <summary>启动当前实例；没有选中时退而启动列表里第一个。</summary>
    public static void Launch()
    {
        var target = Current ?? Installs.FirstOrDefault();
        if (target is null)
        {
            HintService.Hint(Lang.Text("Bedrock.Status.NotFound"), HintType.Warning);
            Refresh();
            return;
        }

        Current = target;
        SetState(Lang.Text("Bedrock.Status.Launching"), target.DisplayName);

        if (BedrockService.Launch(target, out var error))
        {
            HintService.Hint(Lang.Text("Bedrock.Launch.Launched", target.DisplayName), HintType.Success);
            SetState(Lang.Text("Bedrock.Launch.Running"), target.DisplayName);
            return;
        }

        ModBase.Log($"[Bedrock] 启动失败（{target.DisplayName}）：{error}", ModBase.LogLevel.Msgbox,
            userSummary: Lang.Text("Bedrock.Launch.LaunchFailed", error ?? ""));
        SetState(Lang.Text("Bedrock.Launch.LaunchFailed", error ?? ""), target.DisplayName);
    }

    /// <summary>结束所有基岩版进程。</summary>
    public static void Stop()
    {
        var count = BedrockService.StopAll();
        HintService.Hint(count > 0
            ? Lang.Text("Bedrock.Launch.Stopped", count)
            : Lang.Text("Bedrock.Launch.NoneRunning"));

        SetState(
            BedrockService.IsRunning()
                ? Lang.Text("Bedrock.Launch.Running")
                : Lang.Text("Bedrock.Status.Ready"),
            Current?.DisplayName ?? "");
    }

    /// <summary>
    ///     直接改写状态卡片。下载页解包、安装 UWP 等流程也用它显示进度。
    ///     调用方负责线程（UI 线程）。
    /// </summary>
    public static void SetState(string status, string detail)
    {
        StatusText = status;
        DetailText = detail;
        RaiseChanged();
    }

    private static void RaiseChanged() => StateChanged?.Invoke();

    /// <summary>读取用户手动添加的 GDK 目录（换行或分号分隔）。</summary>
    public static List<string> GetConfiguredDirectories()
    {
        var raw = Config.Launch.BedrockGdkDirectories;
        if (string.IsNullOrWhiteSpace(raw)) return [];

        return raw
            .Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
