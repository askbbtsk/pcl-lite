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


    // 更新日志
    private void ShowUpdateLog()
    {
        ModBase.RunInNewThread(() =>
        {
            var changelogFile = $"{ModBase.pathTemp}CEUpdateLog.md";
            string changelog;
            if (File.Exists(changelogFile))
                changelog = ModBase.ReadFile(changelogFile);
            else
                changelog = Lang.Text("Main.UpdateLog.Empty");
            if (ModMain.MyMsgBoxMarkdown(changelog,
                    Lang.Text("Main.UpdateLog.Title", ModBase.versionBranchName, ModBase.versionBaseName), Lang.Text("Common.Action.Confirm"), Lang.Text("Main.UpdateLog.FullChangelog")) ==
                2) ModBase.OpenWebsite(ProjectLinks.Releases);
        }, "UpdateLog Output");
    }


    // 根据打开次数触发的事件
    private void RunCountSub()
    {
        States.System.StartupCount += 1;
    }


    private void DowngradeSub(int lastVersionCode)
    {
        ModBase.Log("[Start] 版本号从 " + lastVersionCode + " 降低到 " + ModBase.versionCode);
        States.System.LastVersion = ModBase.versionCode;
    }

}
