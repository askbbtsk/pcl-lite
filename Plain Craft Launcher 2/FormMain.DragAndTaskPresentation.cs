using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Minecraft.Profile;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft;
using PCL.Core.Plugins;
using PCL.Core.UI;
using PCL.Core.UI.Navigation;
using PCL.Core.UI.Theme;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Plugin.SDK;

namespace PCL;

public partial class FormMain
{

    public void DragStop()
    {
        // 存在其他线程调用的可能性，因此需要确保在 UI 线程运行
        ModBase.RunInUi(() =>
        {
            if (ModMain.dragControl is null)
                return;
            var control = ModMain.dragControl;
            ModMain.dragControl = null;
            control.DragStop(); // 控件会在该事件中判断 DragControl，所以得放在后面
        });
    }




    // 更新重启
    private void BtnExtraUpdateRestart_Click(object sender, MouseButtonEventArgs e)
    {
        UpdateManager.UpdateRestart(true);
    }


    private bool BtnExtraUpdateRestart_ShowCheck()
    {
        return UpdateManager.isUpdateWaitingRestart;
    }


    // 音乐
    private void BtnExtraMusic_Click(object sender, MouseButtonEventArgs e)
    {
        ModMusic.MusicControlPause();
    }


    private void BtnExtraMusic_RightClick(object sender, MouseButtonEventArgs e)
    {
        ModMusic.MusicControlNext();
    }


    // 任务管理
    private void BtnExtraDownload_Click(object sender, MouseButtonEventArgs e)
    {
        PageChange(PageType.TaskManager);
    }


    private bool BtnExtraDownload_ShowCheck()
    {
        return ModNet.HasDownloadingTask() && !(pageCurrent == PageType.TaskManager);
    }


    // 投降
    public void AprilGiveup()
    {
        if (ModMain.isAprilEnabled && !ModMain.isAprilGiveup)
        {
            HintService.Hint("=D", HintType.Success);
            ModMain.isAprilGiveup = true;
            ModMain.frmLaunchLeft.AprilScaleTrans.ScaleX = 1d;
            ModMain.frmLaunchLeft.AprilScaleTrans.ScaleY = 1d;
            BtnExtraApril.ShowRefresh();
        }
    }


    private void BtnExtraApril_Click(object sender, MouseButtonEventArgs e)
    {
        AprilGiveup();
    }


    public bool BtnExtraApril_ShowCheck()
    {
        return ModMain.isAprilEnabled && !ModMain.isAprilGiveup && pageCurrent == PageType.Launch;
    }


    // 关闭 Minecraft
    private void BtnExtraShutdown_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (ModLaunch.mcLaunchLoaderReal is not null)
                ModLaunch.mcLaunchLoaderReal.Abort();
            foreach (var Watcher in ModWatcher.mcWatcherList)
                Watcher.Kill();
            HintService.Hint(Lang.Text("Main.ShutdownMinecraft.Success"), HintType.Success);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "强制关闭所有 Minecraft 失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }
    }


    public bool BtnExtraShutdown_ShowCheck()
    {
        return ModWatcher.hasRunningMinecraft;
    }


    // 游戏日志
    private void BtnExtraLog_Click(object sender, MouseButtonEventArgs e)
    {
        PageChange(PageType.GameLog);
    }


    public bool BtnExtraLog_ShowCheck()
    {
        if (ModMain.frmLogLeft is null || ModMain.frmLogRight is null || pageCurrent == PageType.GameLog)
            return false;
        return ModMain.frmLogLeft.shownLogs.Count > 0;
    }


    /// <summary>
    ///     返回顶部。
    /// </summary>
    public void BackToTop()
    {
        var realScroll = BtnExtraBack_GetRealChild();
        if (realScroll is not null)
            realScroll.PerformVerticalOffsetDelta(-realScroll.VerticalOffset);
        else
            ModBase.Log(
                "[UI] 无法返回顶部，未找到合适的 RealScroll",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Main.Error.ScrollToTopFailed"));
    }


    private void BtnExtraBack_Click(object sender, MouseButtonEventArgs e)
    {
        BackToTop();
    }


    private bool BtnExtraBack_ShowCheck()
    {
        var realScroll = BtnExtraBack_GetRealChild();
        return realScroll is not null && realScroll.Visibility == Visibility.Visible &&
               realScroll.VerticalOffset > Height + (BtnExtraBack.Show ? 0 : 700);
    }


    private MyScrollViewer? BtnExtraBack_GetRealChild()
    {
        if (PanMainRight.Child is null || !(PanMainRight.Child is MyPageRight))
            return null;
        return ((MyPageRight)PanMainRight.Child).PanScroll;
    }

}
