using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App.Lunar;

namespace PCL;

/// <summary>
///     Lunar Client 板块的右栏：状态、启动入口、说明与运行日志。
/// </summary>
public partial class PageLunarLaunch
{
    private bool _subscribed;
    private Brush? _brushReady;
    private Brush? _brushIdle;

    public PageLunarLaunch()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        ModLunar.StateChanged += RefreshPage;
        ModLunar.LogReceived += AppendLog;
        _subscribed = true;
        RefreshPage();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        ModLunar.StateChanged -= RefreshPage;
        ModLunar.LogReceived -= AppendLog;
        _subscribed = false;
    }

    /// <summary>
    ///     把 <see cref="ModLunar" /> 的当前状态铺到界面上。
    ///     刻意不叫 PageOnEnter —— 基类有同名方法，重名会把它藏掉。
    /// </summary>
    private void RefreshPage()
    {
        if (!IsLoaded) return;

        LabStatusLine.Text = ModLunar.StatusText;
        LabStatusDetail.Text = ModLunar.DetailText;

        _brushReady ??= (Brush)FindResource("ColorBrush3");
        _brushIdle ??= (Brush)FindResource("ColorBrushGray4");
        ShapeDot.Fill = ModLunar.IsInstalled ? _brushReady : _brushIdle;

        LabVersion.Text = $"Lunar Client {ModLunar.DefaultSubVersion}";
        LabCacheDir.Text = LunarClientService.MultiverDirectory(ModLunar.LunarRoot);
        LabJava.Text = string.IsNullOrWhiteSpace(ModLunar.JavaPath) ? "启动后自动定位" : ModLunar.JavaPath;

        RefreshButtons();
    }

    private void AppendLog(string line)
    {
        ModBase.RunInUi(() =>
        {
            TextLog.AppendText(line + Environment.NewLine);
            TextLog.ScrollToEnd();
        });
    }

    private void RefreshButtons()
    {
        var busy = ModLunar.IsBusy;
        BtnLaunch.IsEnabled = !busy && ModLunar.IsInstalled;
        BtnLaunch.Text = busy ? "运行中…" : "启动 Lunar";
    }

    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModLunar.IsBusy) return;
        _ = ModLunar.LaunchAsync(ModFolder.mcFolderSelected);
        RefreshButtons();
    }

    private void BtnOpenRoot_Click(object sender, MouseButtonEventArgs e)
    {
        var directory = LunarClientService.MultiverDirectory(ModLunar.LunarRoot);
        if (!Directory.Exists(directory))
        {
            ModMain.MyMsgBox("物料目录还不存在，先启动一次游戏再试。", "打开物料目录");
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private void BtnCopyCommand_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ModLunar.LastCommandLine))
        {
            ModMain.MyMsgBox("还没有启动过，没有命令行可复制。", "复制启动命令");
            return;
        }

        // 命令行里有 accessToken，复制出去前先抹掉，免得被顺手贴到群里
        var redacted = RedactToken(ModLunar.LastCommandLine);
        Clipboard.SetText(redacted);
        HintService.Hint("启动命令已复制（accessToken 已打码）");
    }

    private void BtnClearLog_Click(object sender, MouseButtonEventArgs e)
    {
        TextLog.Clear();
    }

    private static string RedactToken(string commandLine)
    {
        const string flag = "--accessToken";
        var index = commandLine.IndexOf(flag, StringComparison.Ordinal);
        if (index < 0) return commandLine;

        var start = index + flag.Length;
        while (start < commandLine.Length && commandLine[start] == ' ') start++;
        var end = commandLine.IndexOf(' ', start);
        if (end < 0) end = commandLine.Length;

        return commandLine[..start] + "[REDACTED]" + commandLine[end..];
    }
}
