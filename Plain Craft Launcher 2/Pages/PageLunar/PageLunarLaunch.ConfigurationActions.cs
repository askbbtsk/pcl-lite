using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;
using PCL.Core.App.Lunar.Social;
using PCL.Core.UI;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageLunarLaunch
{

    /// <summary>换启动目录。选完立刻生效，下次启动就用它。</summary>
    private void BtnPickGameDir_Click(object sender, EventArgs e)
    {
        var folder = SystemDialogs.SelectFolder(Lang.Text("Lunar.Tip.PickGameDir"), PreviewGameDirectory());
        if (string.IsNullOrWhiteSpace(folder)) return;

        Config.Preference.Lunar.GameDir = folder;
        _gameDirPreview = folder;
        RefreshPage();
    }


    /// <summary>启动目录恢复默认：跟随当前选中的 MC 根目录。</summary>
    private void BtnResetGameDir_Click(object sender, EventArgs e)
    {
        Config.Preference.Lunar.GameDir = "";
        _gameDirPreview = ModLunar.DefaultGameDirectory;
        RefreshPage();
    }


    /// <summary>
    ///     换物料根目录（相当于官方启动器的 <c>~/.lunarclient</c>）。
    ///     物料、JRE、账户库都挂在这下面，所以换完要把版本列表和账号重新读一遍。
    /// </summary>
    private void BtnPickRoot_Click(object sender, EventArgs e)
    {
        var folder = SystemDialogs.SelectFolder(Lang.Text("Lunar.Tip.PickRoot"), ModLunar.LunarRoot);
        if (string.IsNullOrWhiteSpace(folder)) return;

        Config.Preference.Lunar.Root = folder;
        ResetAfterRootChanged();
    }


    /// <summary>物料目录恢复默认：<c>~/.lunarclient</c>。</summary>
    private void BtnResetRoot_Click(object sender, EventArgs e)
    {
        Config.Preference.Lunar.Root = "";
        ResetAfterRootChanged();
    }


    private void ResetAfterRootChanged()
    {
        // 缓存签名清空，让下拉框按新目录里的内容重建
        _versionSignature = "";
        _accountSignature = "";
        ModLunar.Refresh();
        RefreshPage();
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
