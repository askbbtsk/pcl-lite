using System;
using System.Collections.Generic;
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
            var message = Lang.Text("Network.Manager.Privacy.Blocked", info.TitleZh);
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
        var title = Lang.Text("Network.Manager.Request.Title");
        var caption = _BuildCaption(info, zh);
        var allow = Lang.Text("Network.Manager.Allow");
        var deny = Lang.Text("Network.Manager.Deny");
        var allowAll = Lang.Text("Network.Manager.Request.AllowAll");

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
        return Lang.Text("Network.Manager.Request.Caption", zh ? info.TitleZh : info.TitleEn,
            string.Join(", ", info.Hosts));
    }

    private static bool _IsChinese()
    {
        var code = LocalizationService.CurrentLanguage.Code;
        return code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }
}
