using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Backups;
using PCL.Core.Minecraft.Worlds;
using PCL.Core.Minecraft.PersonalOptions;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.Launch.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils;
using PCL.Core.Utils.Secret;
using PCL.Network;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using System.Globalization;
using PCL.Core.Logging;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;

namespace PCL;

public static partial class ModLaunch
{

    private static void McLaunchJava(ModLoader.LoaderTask<int, int> task)
    {
        var constraints = ResolveJavaConstraints(ModInstanceList.McMcInstanceSelected);
        var minVer = constraints.Minimum;
        var maxVer = constraints.Maximum;
        var recommendedComponent = constraints.RecommendedComponent;
        var compatibleJavaMajorsSet = constraints.CompatibleMajors;

        lock (ModJava.javaLock)
        {
            // 选择 Java
            McLaunchLog("Java 版本需求：最低 " + minVer + "，最高 " + maxVer +
                        (compatibleJavaMajorsSet is null
                            ? ""
                            : "，兼容主版本：" + string.Join(", ", compatibleJavaMajorsSet.OrderBy(major => major))));
            mcLaunchJavaSelected = ModJava.JavaSelect("$$", minVer, maxVer, ModInstanceList.McMcInstanceSelected,
                false, compatibleJavaMajorsSet);
            if (task.IsAborted)
                return;
            if (mcLaunchJavaSelected is not null)
            {
                McLaunchLog("选择的 Java：" + mcLaunchJavaSelected);
                return;
            }

            // 无合适的 Java
            if (task.IsAborted)
                return; // 中断加载会导致 JavaSelect 异常地返回空值，误判找不到 Java
            McLaunchLog("无合适的 Java，需要确认是否自动下载");
            string javaCode;
            var compatibleDownloadMajor = compatibleJavaMajorsSet?
                .Where(major =>
                {
                    var majorMinVer = major <= 8
                        ? new Version(1, major, 0, 0)
                        : new Version(major, 0, 0, 0);
                    var majorMaxVer = major <= 8
                        ? new Version(1, major, 999, 999)
                        : new Version(major, 999, 999, 999);
                    return majorMaxVer >= minVer && majorMinVer <= maxVer;
                })
                .OrderBy(major => major)
                .FirstOrDefault();
            if (compatibleDownloadMajor > 0)
            {
                javaCode = compatibleDownloadMajor.Value.ToString();
            }
            else if (minVer >= new Version(1, 9))
            {
                javaCode = minVer.Major.ToString();
            }
            else if (maxVer < new Version(1, 8))
            {
                if (ModInstanceList.McMcInstanceSelected.Info.HasForge)
                    ModMain.MyMsgBox(
                        Lang.Text("Minecraft.Launch.Java.NeedLegacyJavaFixerOrJava7"),
                        Lang.Text("Minecraft.Launch.Java.NotFound.Title"));
                else
                    ModMain.MyMsgBox(
                        Lang.Text("Minecraft.Launch.Java.NeedJava7"),
                        Lang.Text("Minecraft.Launch.Java.NotFound.Title"));
                throw new Exception("$$");
            }
            else if (minVer > new Version(1, 8, 0, 140) && maxVer < new Version(1, 8, 0, 321))
            {
                ModMain.MyMsgBox(
                    Lang.Text("Minecraft.Launch.Java.NeedJava8U141ToU320"),
                    Lang.Text("Minecraft.Launch.Java.NotFound.Title"));
                throw new Exception("$$");
            }
            else if (minVer > new Version(1, 8, 0, 140))
            {
                ModMain.MyMsgBox(
                    Lang.Text("Minecraft.Launch.Java.NeedJava8U141OrLater"),
                    Lang.Text("Minecraft.Launch.Java.NotFound.Title"));
                throw new Exception("$$");
            }
            else
            {
                javaCode = 8.ToString();
            }

            if (!ModJava.JavaDownloadConfirm($"Java {javaCode}"))
                throw new Exception("$$");
            // 开始自动下载
            var javaLoader = ModJava.GetJavaDownloadLoader();
            try
            {
                javaLoader.Start(recommendedComponent ?? javaCode, true); // 在 Java 22+ 时优先使用 Mojang 提供的 Component 字段
                while (javaLoader.State == ModBase.LoadState.Loading && !task.IsAborted)
                {
                    task.Progress = javaLoader.Progress;
                    Thread.Sleep(10);
                }
            }
            finally
            {
                javaLoader.Abort(); // 确保取消时中止 Java 下载
            }

            // 检查下载结果
            mcLaunchJavaSelected = ModJava.JavaSelect("$$", minVer, maxVer, ModInstanceList.McMcInstanceSelected,
                false, compatibleJavaMajorsSet);
            if (task.IsAborted)
                return;
            if (mcLaunchJavaSelected is not null)
            {
                McLaunchLog("选择的 Java：" + mcLaunchJavaSelected);
            }
            else
            {
                HintService.Hint(Lang.Text("Minecraft.Launch.Error.NoJava"), HintType.Error);
                throw new Exception("$$");
            }
        }
    }

}
