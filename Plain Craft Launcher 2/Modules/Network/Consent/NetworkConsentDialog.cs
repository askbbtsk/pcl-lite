using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Consent;
using PCL.Core.Logging;
using PCL.Core.UI;

namespace PCL.Network;

/// <summary>
/// 网络授权弹窗。沿用 PCL 的消息框组件，因此配色、按钮、动画与其余界面完全一致。
/// <para>
/// 弹窗按「用途」触发，不是按域名触发，所以一次安装流程不会出现成百上千个弹窗。
/// </para>
/// </summary>
public static class NetworkConsentDialog
{
    private static bool _registered;

    /// <summary>把弹窗挂到授权中心。必须在首次联网前调用。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        NetworkConsentService.ConsentRequested = AskAsync;
        NetworkConsentService.PrivacyBlocked += OnPrivacyBlocked;
        LogWrapper.Info("Network", "网络授权弹窗已注册");
    }

    private static readonly HashSet<string> _privacyHinted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>隐私模式拦下请求时给一次可见提示，避免用户只看到功能莫名失败。</summary>
    private static void OnPrivacyBlocked(NetworkPurposeInfo info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Show()
        {
            if (!_privacyHinted.Add(info.Id)) return;
            var zh = _IsChinese();
            var message = zh
                ? $"隐私模式已开启，「{(info.TitleZh)}」被拦截，功能可能不可用。\n可在「设置 → 杂项 → 网络授权」中关闭隐私模式。"
                : $"Privacy mode blocked \"{info.TitleEn}\". Disable it in Settings → Misc → Network consent.";
            HintService.Hint(message, HintType.Error);
        }

        if (dispatcher.CheckAccess()) Show();
        else dispatcher.InvokeAsync(Show);
    }

    private static Task<bool> AskAsync(NetworkPurposeInfo info)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            completion.TrySetResult(false);
            return completion.Task;
        }

        void Show()
        {
            try
            {
                completion.TrySetResult(_Ask(info));
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "Network", "网络授权弹窗执行失败，按拒绝处理");
                completion.TrySetResult(false);
            }
        }

        if (dispatcher.CheckAccess())
            Show();
        else
            dispatcher.InvokeAsync(Show);

        return completion.Task;
    }

    private static bool _Ask(NetworkPurposeInfo info)
    {
        var zh = _IsChinese();
        var title = zh ? "网络访问请求" : "Network access request";
        var caption = _BuildCaption(info, zh);
        var allow = zh ? "允许" : "Allow";
        var deny = zh ? "拒绝" : "Deny";
        var allowAll = zh ? "本次会话全部允许" : "Allow all this session";

        var result = ModMain.MyMsgBox(caption, title, allow, deny, allowAll, isWarn: true);
        switch (result)
        {
            case 1:
                return true;
            case 3:
                NetworkConsentService.SetAllowAllForSession();
                return true;
            default:
                return false;
        }
    }

    private static string _BuildCaption(NetworkPurposeInfo info, bool zh)
    {
        var name = zh ? info.TitleZh : info.TitleEn;
        var description = zh ? info.DescriptionZh : info.DescriptionEn;
        var feature = zh ? info.FeatureZh : info.FeatureEn;

        var sb = new StringBuilder();
        sb.AppendLine(zh ? $"「{name}」需要连接远程服务器。" : $"\"{name}\" needs to reach remote servers.");
        sb.AppendLine();

        if (info.Hosts.Length > 0)
        {
            sb.AppendLine(zh ? "目标：" : "Targets:");
            foreach (var host in info.Hosts)
                sb.AppendLine("    " + host);
        }
        else
        {
            sb.AppendLine(zh ? "目标：由运行时动态确定。" : "Targets: determined at runtime.");
        }

        sb.AppendLine();
        sb.AppendLine((zh ? "用途：" : "Purpose: ") + description);
        sb.AppendLine();
        sb.AppendLine(zh
            ? $"不同意将无法使用{feature}。"
            : $"Declining makes {feature} unavailable.");
        sb.AppendLine();
        sb.Append(zh
            ? "该选择仅在本次会话内有效；可在「设置 → 启动器 → 网络白名单」中永久管理。"
            : "This choice lasts for the current session. Manage it permanently in Settings.");

        return sb.ToString();
    }

    private static bool _IsChinese()
    {
        var code = LocalizationService.CurrentLanguage.Code;
        return code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }
}
