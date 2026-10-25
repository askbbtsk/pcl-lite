using System;
using System.Diagnostics;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     PCL Lite 官方 QQ 交流群。
///     <para>
///         群号写在这一个地方，欢迎页与贡献者页共用，改群号只改这里。
///     </para>
///     <para>
///         为什么不用 https 加群链接：那种链接需要 QQ 后台生成的 <c>k</c> 参数（jq.qq.com / qm.qq.com），
///         没有 key 就打不开对应群。这里改用 QQ 的群名片协议 <c>mqqapi://</c>，
///         装了 QQ 就能直接弹出群资料卡；没装则退化为复制群号，点一下总归有结果。
///     </para>
/// </summary>
public static class QqGroup
{
    /// <summary>群号。</summary>
    public const string Number = "1085542362";

    /// <summary>QQ 群名片协议链接（需要本机安装 QQ）。</summary>
    public static string Url =>
        $"mqqapi://card/show_pslcard?src_type=internal&version=1&uin={Number}&card_type=group&source=qrcode";

    /// <summary>
    ///     打开群资料卡；系统没有注册该协议（没装 QQ）时改为复制群号并提示。
    /// </summary>
    public static void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
            HintService.Hint(Lang.Text("Community.Qq.Opening"));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "打开 QQ 群失败，已改为复制群号", ModBase.LogLevel.Debug);
            ModBase.ClipboardSet(Number, false);
            HintService.Hint(Lang.Text("Community.Qq.Copied", Number));
        }
    }
}
