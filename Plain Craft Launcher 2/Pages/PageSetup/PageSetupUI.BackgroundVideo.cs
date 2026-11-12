using System.Windows;
using System.Windows.Controls;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageSetupUI
{
    // Static method identity lets each refresh detach the previous subscription.
    // A local lambda would only remove the newly created delegate.
    private static void BackgroundVideoFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not MediaElement { Source: { } source }) return;
        var videoEx = e.ErrorException;
        ModVideoBack.VideoStop();
        if (videoEx.Message.Contains("0xC00D109B"))
            ModBase.Log(
                $"""
                 刷新背景内容失败，该视频文件可能并非 H.264（AVC）格式。
                 你可以尝试使用视频转码工具打开视频文件并设定目标格式为 H.264（AVC），然后转码该视频。
                 文件：{source}
                 """,
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Setup.Ui.Error.BackgroundVideoUnsupported"));
        else
            ModBase.Log(videoEx, $"刷新背景内容失败（{source}）", ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
    }
}
