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
        var minVer = new Version(0, 0, 0, 0);
        var maxVer = new Version(999, 999, 999, 999);

        // MC 大版本检测
        if ((!ModInstanceList.McMcInstanceSelected.Info.Valid &&
             ModInstanceList.McMcInstanceSelected.releaseTime >= new DateTime(2024, 4, 2)) ||
            (ModInstanceList.McMcInstanceSelected.Info.Valid &&
             ModInstanceList.McMcInstanceSelected.Info.vanilla >= new Version(20, 0, 5)))
        {
            // 1.20.5+ (24w14a+)：至少 Java 21
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.20.5+ (24w14a+) 要求至少 Java 21");
            minVer = new Version(21, 0, 0, 0);
        }
        else if ((!ModInstanceList.McMcInstanceSelected.Info.Valid &&
                  ModInstanceList.McMcInstanceSelected.releaseTime >= new DateTime(2021, 11, 16)) ||
                 (ModInstanceList.McMcInstanceSelected.Info.Valid &&
                  ModInstanceList.McMcInstanceSelected.Info.vanilla.Major >= 18))
        {
            // 1.18 pre2+：至少 Java 17
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.18 pre2+ 要求至少 Java 17");
            minVer = new Version(17, 0, 0, 0);
        }
        else if ((!ModInstanceList.McMcInstanceSelected.Info.Valid &&
                  ModInstanceList.McMcInstanceSelected.releaseTime >= new DateTime(2021, 5, 11)) ||
                 (ModInstanceList.McMcInstanceSelected.Info.Valid &&
                  ModInstanceList.McMcInstanceSelected.Info.vanilla.Major >= 17))
        {
            // 1.17+ (21w19a+)：至少 Java 16
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.17+ (21w19a+) 要求至少 Java 16");
            minVer = new Version(16, 0, 0, 0);
        }
        else if (ModInstanceList.McMcInstanceSelected.releaseTime.Year >= 2017) // Minecraft 1.12 与 1.11 的分界线正好是 2017 年，太棒了
        {
            // 1.12+：至少 Java 8
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.12+ 要求至少 Java 8");
            minVer = new Version(1, 8, 0, 0);
        }
        else if (ModInstanceList.McMcInstanceSelected.releaseTime <= new DateTime(2013, 5, 1) &&
                 ModInstanceList.McMcInstanceSelected.releaseTime.Year >= 2001) // 避免某些版本写个 1960 年
        {
            // 1.5.2-：最高 Java 8
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] MC 1.5.2- 要求最高 Java 12");
            maxVer = new Version(1, 8, 999, 999);
        }

        // 原版 26+：获取 Mojang 要求的 Java 版本
        string recommendedComponent = null;
        var recommendedCode =
            ModInstanceList.McMcInstanceSelected.JsonObject?["javaVersion"]?["majorVersion"]?.ToObject<int>() ??
            ModInstanceList.McMcInstanceSelected.JsonVersion?["java_version"]?.ToObject<int>() ?? 0;
        if (recommendedCode >= 22)
        {
            McLaunchLog("Mojang 要求至少使用 Java " + recommendedCode);
            minVer = new Version(recommendedCode, 0, 0, 0);
            recommendedComponent =
                ModInstanceList.McMcInstanceSelected.JsonObject?["javaVersion"]?["component"]?.ToString() ??
                ModInstanceList.McMcInstanceSelected.JsonVersion?["java_component"]?.ToString();
            if (string.IsNullOrEmpty(recommendedComponent))
                recommendedComponent = null;
        }

        // OptiFine 检测
        if (ModInstanceList.McMcInstanceSelected.Info.HasOptiFine && ModInstanceList.McMcInstanceSelected.Info.Valid) // 不管非标准版本
        {
            if (ModInstanceList.McMcInstanceSelected.Info.vanilla.Major < 7)
            {
                // <1.7：至多 Java 8
                maxVer = new Version(1, 8, 999, 999);
            }
            else if (ModInstanceList.McMcInstanceSelected.Info.vanilla.Major >= 8 &&
                     ModInstanceList.McMcInstanceSelected.Info.vanilla.Major < 12)
            {
                // 1.8 - 1.11：必须恰好 Java 8
                minVer = new Version(1, 8, 0, 0);
                maxVer = new Version(1, 8, 999, 999);
            }
            else if (ModInstanceList.McMcInstanceSelected.Info.vanilla.Major == 12)
            {
                // 1.12：最高 Java 8
                maxVer = new Version(1, 8, 999, 999);
            }
        }

        // Forge 检测
        if (ModInstanceList.McMcInstanceSelected.Info.HasForge)
        {
            if (ModInstanceList.McMcInstanceSelected.Info.vanilla >= new Version(6, 0, 1) &&
                ModInstanceList.McMcInstanceSelected.Info.vanilla <= new Version(7, 0, 2))
            {
                // 1.6.1 - 1.7.2：必须 Java 7
                minVer = new Version(1, 7, 0, 0) > minVer ? new Version(1, 7, 0, 0) : minVer;
                maxVer = new Version(1, 7, 999, 999) < maxVer ? new Version(1, 7, 999, 999) : maxVer;
            }
            else if (ModInstanceList.McMcInstanceSelected.Info.vanilla.Major <= 12 ||
                     !ModInstanceList.McMcInstanceSelected.Info.Valid) // 非标准版本
            {
                // <=1.12：Java 8
                maxVer = new Version(1, 8, 999, 999);
            }
            else if (ModInstanceList.McMcInstanceSelected.Info.vanilla.Major <= 14)
            {
                // 1.13 - 1.14：Java 8 - 10
                minVer = new Version(1, 8, 0, 0) > minVer ? new Version(1, 8, 0, 0) : minVer;
                maxVer = new Version(1, 10, 999, 999) < maxVer ? new Version(1, 10, 999, 999) : maxVer;
            }
            else if (ModInstanceList.McMcInstanceSelected.Info.vanilla.Major == 15)
            {
                // 1.15：Java 8 - 15
                minVer = new Version(1, 8, 0, 0) > minVer ? new Version(1, 8, 0, 0) : minVer;
                maxVer = new Version(1, 15, 999, 999) < maxVer ? new Version(1, 15, 999, 999) : maxVer;
            }
            else if (McVersionComparer.CompareVersionGe(ModInstanceList.McMcInstanceSelected.Info.Forge, "34.0.0") &&
                     McVersionComparer.CompareVersionGe("36.2.25", ModInstanceList.McMcInstanceSelected.Info.Forge))
            {
                // 1.16，Forge 34.X ~ 36.2.25：最高 Java 8u321
                maxVer = new Version(1, 8, 0, 320) < maxVer ? new Version(1, 8, 0, 321) : maxVer;
            }
            else if (ModInstanceList.McMcInstanceSelected.Info.vanilla.Major >= 18 &&
                     ModInstanceList.McMcInstanceSelected.Info.vanilla.Major < 19 &&
                     ModInstanceList.McMcInstanceSelected.Info.HasOptiFine) // #305
            {
                // 1.18：若安装了 OptiFine，最高 Java 18
                maxVer = new Version(1, 18, 999, 999) < maxVer ? new Version(1, 18, 999, 999) : maxVer;
            }
        }

        // Cleanroom 检测
        if (ModInstanceList.McMcInstanceSelected.Info.HasCleanroom)
        {
            if (!Version.TryParse(ModInstanceList.McMcInstanceSelected.Info.Cleanroom.Split('-')[0], out Version cleanroomVersion))
                throw new FormatException("无法解析 Cleanroom 版本号：" + ModInstanceList.McMcInstanceSelected.Info.Cleanroom);
            if (cleanroomVersion < new Version(0, 5, 0, 0))
            {
                if (ModBase.modeDebug) ModBase.Log("[Launch] [Debug] Cleanroom 版本低于 0.5，要求至少 Java 21");
                minVer = new Version(21, 0, 0, 0) > minVer ? new Version(21, 0, 0, 0) : minVer;
            }
            else
            {
                if (ModBase.modeDebug) ModBase.Log("[Launch] [Debug] Cleanroom 版本高于 0.5，要求至少 Java 25");
                minVer = new Version(25, 0, 0, 0) > minVer ? new Version(25, 0, 0, 0) : minVer;
            }
        }

        // Fabric 检测
        if (ModInstanceList.McMcInstanceSelected.Info.HasFabric && ModInstanceList.McMcInstanceSelected.Info.Valid) // 不管非标准版本
        {
            if (ModInstanceList.McMcInstanceSelected.Info.vanilla.Major >= 15 &&
                ModInstanceList.McMcInstanceSelected.Info.vanilla.Major <= 16)
                // 1.15 - 1.16：Java 8+
                minVer = new Version(1, 8, 0, 0) > minVer ? new Version(1, 8, 0, 0) : minVer;
            else if (ModInstanceList.McMcInstanceSelected.Info.vanilla.Major >= 18)
                // 1.18+：Java 17+
                minVer = new Version(1, 17, 0, 0) > minVer ? new Version(1, 17, 0, 0) : minVer;
        }

        // LiteLoader 检测
        if (ModInstanceList.McMcInstanceSelected.Info.HasLiteLoader && ModInstanceList.McMcInstanceSelected.Info.Valid)
        {
            // 最高 Java 8
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] LiteLoader 要求最高 Java 8");
            maxVer = new Version(8, 999, 999, 999) < maxVer ? new Version(8, 999, 999, 999) : maxVer;
        }

        // LabyMod 检测
        if (ModInstanceList.McMcInstanceSelected.Info.HasLabyMod)
        {
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] LabyMod 要求至少 Java 21");
            minVer = new Version(21, 0, 0, 0) > minVer ? new Version(21, 0, 0, 0) : minVer;
            maxVer = new Version(999, 999, 999, 999);
        }

        // JSON 中要求的版本
        if (ModInstanceList.McMcInstanceSelected.JsonObject["javaVersion"] is not null)
        {
            // Val 无法解析 JsonNode（会返回 0），必须先转为字符串
            var majorVersion = ModBase.Val(ModInstanceList.McMcInstanceSelected.JsonObject["javaVersion"]["majorVersion"]
                ?.ToString());
            if (ModBase.modeDebug)
                ModBase.Log("[Launch] [Debug] JSON 中参数要求至少 Java " + majorVersion);
            if (majorVersion <= 8d)
                minVer = new Version(1, (int)Math.Round(majorVersion), 0, 0) > minVer
                    ? new Version(1, (int)Math.Round(majorVersion), 0, 0)
                    : minVer;
            else
                minVer = new Version((int)Math.Round(majorVersion), 0, 0, 0) > minVer
                    ? new Version((int)Math.Round(majorVersion), 0, 0, 0)
                    : minVer;

            if (maxVer < minVer)
                maxVer = new Version(999, 999, 999, 999);
        }

        // compatibleJavaMajors 声明的兼容主版本集合（选择 Java 时按成员资格判断，而非连续区间）
        IReadOnlySet<int> compatibleJavaMajorsSet = null;
        // JSON 中要求的兼容 Java 主版本（例如 MultiMC 整合包的 compatibleJavaMajors）
        if (ModInstanceList.McMcInstanceSelected.JsonObject["compatibleJavaMajors"] is JsonArray compatibleMajors &&
            compatibleMajors.Count > 0)
        {
            var majors = compatibleMajors
                // Val 无法解析 JsonNode（会返回 0），必须先转为字符串
                .Select(major => (int)Math.Round(ModBase.Val(major?.ToString())))
                .Where(major => major > 0)
                .Distinct()
                .OrderBy(major => major)
                .ToList();
            if (majors.Count > 0)
            {
                static Version ToCompatibleVersion(int major, bool isMax) => major <= 8
                    ? new Version(1, major, isMax ? 999 : 0, isMax ? 999 : 0)
                    : new Version(major, isMax ? 999 : 0, isMax ? 999 : 0, isMax ? 999 : 0);
                var compatibleMinVer = ToCompatibleVersion(majors.Min(), false);
                var compatibleMaxVer = ToCompatibleVersion(majors.Max(), true);
                if (ModBase.modeDebug)
                    ModBase.Log("[Launch] [Debug] JSON 中要求的兼容 Java 主版本：" + string.Join(", ", majors));
                // compatibleJavaMajors 具有最高优先级
                // 先与其他规则取交集；若无交集（例如旧版本的 Java 8 上限规则），则直接采用 compatibleJavaMajors 声明的范围
                var intersectMin = compatibleMinVer > minVer ? compatibleMinVer : minVer;
                var intersectMax = compatibleMaxVer < maxVer ? compatibleMaxVer : maxVer;
                if (intersectMax < intersectMin)
                {
                    intersectMin = compatibleMinVer;
                    intersectMax = compatibleMaxVer;
                }

                minVer = intersectMin;
                maxVer = intersectMax;
                // 记录原始集合：自动选择时按成员资格过滤，避免接受集合未声明的主版本（例如 [8, 17] 不接受 9 - 16）
                compatibleJavaMajorsSet = majors.ToHashSet();
            }
        }

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
