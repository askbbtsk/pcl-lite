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

    // Game 部分（第二段）
    private static string McLaunchArgumentsGameOld(McInstance version)
    {
        var dataList = new List<string>();

        // 本地化 Minecraft 启动信息
        var basicString = version.JsonObject["minecraftArguments"].ToString();
        if (!basicString.Contains("--height") && 
            version.releaseTime >= new DateTime(2013, 6, 17)) // 2013-06-17 => 13w25a
            basicString += " --height ${resolution_height} --width ${resolution_width}";
        dataList.Add(basicString);

        var result = dataList.Join(" ");

        // 特别改变 OptiFineTweaker
        if ((version.Info.HasForge || version.Info.HasLiteLoader) && version.Info.HasOptiFine)
        {
            // 把 OptiFineForgeTweaker 放在最后，不然会导致崩溃！
            if (result.Contains("--tweakClass optifine.OptiFineForgeTweaker"))
            {
                ModBase.Log("[Launch] 发现正确的 OptiFineForge TweakClass，目前参数：" + result);
                result = result.Replace(" --tweakClass optifine.OptiFineForgeTweaker", "")
                             .Replace("--tweakClass optifine.OptiFineForgeTweaker ", "") +
                         " --tweakClass optifine.OptiFineForgeTweaker";
            }

            if (result.Contains("--tweakClass optifine.OptiFineTweaker"))
            {
                ModBase.Log("[Launch] 发现错误的 OptiFineForge TweakClass，目前参数：" + result);
                result = result.Replace(" --tweakClass optifine.OptiFineTweaker", "")
                             .Replace("--tweakClass optifine.OptiFineTweaker ", "") +
                         " --tweakClass optifine.OptiFineForgeTweaker";
                try
                {
                    ModBase.WriteFile(Path.Combine(version.PathInstance, version.Name + ".json"),
                        ModBase.ReadFile(Path.Combine(version.PathInstance, version.Name + ".json"))
                            .Replace("optifine.OptiFineTweaker", "optifine.OptiFineForgeTweaker"));
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "替换 OptiFineForge TweakClass 失败");
                }
            }
        }

        return result;
    }


    private static string McLaunchArgumentsGameNew(McInstance instance)
    {
        string mcLaunchArgumentsGameNewRet = default;
        var dataList = new List<string>();

        // 获取 Json 中的 DataList
        var currentInstance = instance;
        while (true)
        {
            if (currentInstance.JsonObject["arguments"] is not null &&
                currentInstance.JsonObject["arguments"]["game"] is not null)
                foreach (var subJson in currentInstance.JsonObject["arguments"]["game"].AsArray())
                    if (subJson.GetValueKind() == JsonValueKind.String)
                    {
                        // 字符串类型
                        dataList.Add(subJson.ToString());
                    }
                    // 非字符串类型
                    else if (ModLibrary.McJsonRuleCheck(subJson["rules"]))
                    {
                        // 满足准则
                        if (subJson["value"].GetValueKind() == JsonValueKind.String)
                            dataList.Add(subJson["value"].ToString());
                        else
                            foreach (var value in subJson["value"].AsArray())
                                dataList.Add(value.ToString());
                    }

            if (string.IsNullOrEmpty(currentInstance.InheritInstanceName))
                break;

            currentInstance = new McInstance(currentInstance.InheritInstanceName);
        }

        // 将 "-XXX" 与后面 "XXX" 合并到一起
        // 如果不进行合并 Impact 会启动无效，它有两个 --tweakclass
        var deDuplicateDataList = new List<string>();
        for (int i = 0, loopTo = dataList.Count - 1; i <= loopTo; i++)
        {
            var currentEntry = dataList[i];
            if (dataList[i].StartsWithF("-"))
                while (i < dataList.Count - 1)
                {
                    if (dataList[i + 1].StartsWithF("-")) break;

                    i += 1;
                    currentEntry += " " + dataList[i];
                }

            deDuplicateDataList.Add(currentEntry);
        }

        // 去重
        mcLaunchArgumentsGameNewRet = deDuplicateDataList.Distinct().ToList().Join(" ");

        // 特别改变 OptiFineTweaker
        if ((instance.Info.HasForge || instance.Info.HasLiteLoader) && instance.Info.HasOptiFine)
        {
            // 把 OptiFineForgeTweaker 放在最后，不然会导致崩溃！
            if (mcLaunchArgumentsGameNewRet.Contains("--tweakClass optifine.OptiFineForgeTweaker"))
            {
                ModBase.Log("[Launch] 发现正确的 OptiFineForge TweakClass，目前参数：" + mcLaunchArgumentsGameNewRet);
                mcLaunchArgumentsGameNewRet =
                    mcLaunchArgumentsGameNewRet.Replace(" --tweakClass optifine.OptiFineForgeTweaker", "")
                        .Replace("--tweakClass optifine.OptiFineForgeTweaker ", "") +
                    " --tweakClass optifine.OptiFineForgeTweaker";
            }

            if (mcLaunchArgumentsGameNewRet.Contains("--tweakClass optifine.OptiFineTweaker"))
            {
                ModBase.Log("[Launch] 发现错误的 OptiFineForge TweakClass，目前参数：" + mcLaunchArgumentsGameNewRet);
                mcLaunchArgumentsGameNewRet =
                    mcLaunchArgumentsGameNewRet.Replace(" --tweakClass optifine.OptiFineTweaker", "")
                        .Replace("--tweakClass optifine.OptiFineTweaker ", "") +
                    " --tweakClass optifine.OptiFineForgeTweaker";
                try
                {
                    ModBase.WriteFile(Path.Combine(instance.PathInstance, instance.Name + ".json"),
                        ModBase.ReadFile(Path.Combine(instance.PathInstance, instance.Name + ".json"))
                            .Replace("optifine.OptiFineTweaker", "optifine.OptiFineForgeTweaker"));
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "替换 OptiFineForge TweakClass 失败");
                }
            }
        }

        return mcLaunchArgumentsGameNewRet;
    }


    // 替换 Arguments
    private static Dictionary<string, string> McLaunchArgumentsReplace(McInstance instance,
        ref ModLoader.LoaderTask<string, List<ModLibrary.McLibToken>> loader)
    {
        var gameArguments = new Dictionary<string, string>();

        // 基础参数
        gameArguments.Add("${classpath_separator}", ";");
        gameArguments.Add("${natives_directory}", ModBase.ShortenPath(GetNativesFolder()));
        gameArguments.Add("${library_directory}", ModBase.ShortenPath(ModFolder.mcFolderSelected + "libraries"));
        gameArguments.Add("${libraries_directory}", ModBase.ShortenPath(ModFolder.mcFolderSelected + "libraries"));
        gameArguments.Add("${launcher_name}", "PCLCE");
        gameArguments.Add("${launcher_version}", ModBase.versionCode.ToString());
        gameArguments.Add("${version_name}", instance.Name);
        var argumentInfo = Config.Instance.TypeInfo[ModInstanceList.McMcInstanceSelected?.PathInstance];
        gameArguments.Add("${version_type}",
            string.IsNullOrEmpty(argumentInfo)
                ? Config.Launch.TypeInfo
                : argumentInfo);
        gameArguments.Add("${game_directory}",
            ModBase.ShortenPath(ModInstanceList.McMcInstanceSelected.PathIndie[..^1]));
        gameArguments.Add("${assets_root}", ModBase.ShortenPath(ModFolder.mcFolderSelected + "assets"));
        gameArguments.Add("${user_properties}", "{}");
        gameArguments.Add("${auth_player_name}", mcLoginLoader.output.Name);
        gameArguments.Add("${auth_uuid}", mcLoginLoader.output.Uuid);
        gameArguments.Add("${auth_access_token}", mcLoginLoader.output.AccessToken);
        gameArguments.Add("${access_token}", mcLoginLoader.output.AccessToken);
        gameArguments.Add("${auth_session}", mcLoginLoader.output.AccessToken);
        gameArguments.Add("${user_type}", "msa"); // #1221

        // 窗口尺寸参数
        Size gameSize;
        switch (Config.Launch.GameWindowMode)
        {
            case GameWindowSizeMode.Launcher: // 与启动器尺寸一致
            {
                Size result;
                ModBase.RunInUiWait(() => result = new Size(ModBase.GetPixelSize(ModMain.frmMain.PanForm.ActualWidth),
                    ModBase.GetPixelSize(ModMain.frmMain.PanForm.ActualHeight)));
                gameSize = result;
                gameSize.Height -= 29.5d * ModBase.dpi / 96d; // 标题栏高度
                break;
            }
            case GameWindowSizeMode.Custom: // 自定义
            {
                gameSize = new Size(Math.Max(100, (double)Config.Launch.GameWindowWidth),
                    Math.Max(100, (double)Config.Launch.GameWindowHeight));
                break;
            }

            default:
            {
                gameSize = new Size(854d, 480d);
                break;
            }
        }

        if (ModInstanceList.McMcInstanceSelected.Info.Drop <= 120 && mcLaunchJavaSelected.Installation.MajorVersion <= 8 &&
            mcLaunchJavaSelected.Installation.Version.Revision >= 200 &&
            mcLaunchJavaSelected.Installation.Version.Revision <= 321 &&
            !ModInstanceList.McMcInstanceSelected.Info.HasOptiFine && !ModInstanceList.McMcInstanceSelected.Info.HasForge)
        {
            // 修复 #3463：1.12.2-，JRE 8u200~321 下窗口大小为设置大小的 DPI% 倍
            McLaunchLog($"已应用窗口大小过大修复（{mcLaunchJavaSelected.Installation.Version.Revision}）");
            gameSize.Width /= ModBase.dpi / 96d;
            gameSize.Height /= ModBase.dpi / 96d;
        }

        gameArguments.Add("${resolution_width}", Math.Round(gameSize.Width).ToString(CultureInfo.InvariantCulture));
        gameArguments.Add("${resolution_height}", Math.Round(gameSize.Height).ToString(CultureInfo.InvariantCulture));

        // Assets 相关参数
        gameArguments.Add("${game_assets}",
            ModBase.ShortenPath(ModFolder.mcFolderSelected +
                                @"assets\virtual\legacy")); // 1.5.2 的 pre-1.6 资源索引应与 legacy 合并
        gameArguments.Add("${assets_index_name}", ModAssets.McAssetsGetIndexName(instance));

        // 支持库参数
        var libList = ModLibrary.McLibListGet(instance, true);
        loader.output = libList;
        var cpStrings = new List<string>();
        string optiFineCp = null;

        // LegacyFix 释放
        if (McLaunchNeedsLegacyFix(instance))
        {
            var legacyFixPath = Path.Combine(ModBase.pathPure, "legacyfix.jar");
            try
            {
                ModBase.WriteFile(legacyFixPath, ModBase.GetResourceStream("Resources/legacyfix.jar"));
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "LegacyFix 释放失败");
            }
        }

        // LWJGL Unsafe Agent 释放
        if (McLaunchUsesLwjglUnsafeAgent(instance))
        {
            string agentPath = Path.Combine(ModBase.pathPure, "lwjgl-unsafe-agent.jar");
            try
            {
                ModBase.WriteFile(agentPath, ModBase.GetResourceStream("Resources/lwjgl-unsafe-agent.jar"));
                cpStrings.Add(agentPath);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "LWJGL Unsafe Agent 释放失败");
            }
        }

        foreach (var library in libList)
        {
            if (library.IsNatives)
                continue;
            if (ModInstanceList.McMcInstanceSelected.Info.HasCleanroom 
                && library.OriginalName is not null 
                && (library.OriginalName.Contains("org.lwjgl.lwjgl:lwjgl:2.9.4") 
                    || library.OriginalName.Contains("net.java.dev.jna:platform:3.4.0")
                    || library.OriginalName.Contains("com.ibm.icu:icu4j-core-mojang:51.2")))
                continue;
            if (library.Name is not null && library.Name == "optifine:OptiFine")
                optiFineCp = library.LocalPath;
            else
                cpStrings.Add(library.LocalPath);
        }

        foreach (var library in Config.Instance.ClasspathHead[instance.PathInstance].Split(";")) // 自定义 Classpath 头部
        {
            if (string.IsNullOrWhiteSpace(library))
                continue;
            cpStrings.Insert(0, library);
        }

        if (optiFineCp is not null)
            cpStrings.Insert(cpStrings.Count - 2, optiFineCp); // OptiFine 的总是需要放到倒数第二位
        gameArguments.Add("${classpath}", cpStrings.Select(c => ModBase.ShortenPath(c)).Join(";"));

        return gameArguments;
    }

}
