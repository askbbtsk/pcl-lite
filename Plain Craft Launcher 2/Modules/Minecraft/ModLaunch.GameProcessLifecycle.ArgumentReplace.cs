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

    /// <summary>
    ///     对替换标记进行处理。会对替换内容使用 EscapeHandler 进行转义。
    /// </summary>
    private static string ArgumentReplace(string text, bool replaceTime, Func<string, string> escapeHandler = null)
    {
        // 预处理
        if (text is null)
            return null;

        string replacer(string s)
        {
            if (s is null)
                return "";
            if (escapeHandler is null)
                return s;
            if (s.Contains(@":\"))
                s = ModBase.ShortenPath(s);
            return escapeHandler(s);
        }

        ;
        // 基础
        text = text.Replace("{pcl_version}", replacer(ModBase.versionBaseName));
        text = text.Replace("{pcl_version_code}", replacer(ModBase.versionCode.ToString()));
        text = text.Replace("{pcl_version_branch}", replacer(ModBase.versionBranchName));
        text = text.Replace("{identify}", replacer(Identify.LauncherId));
        text = text.Replace("{path}", replacer(Basics.CurrentDirectory));
        text = text.Replace("{path_with_name}", replacer(Basics.ExecutablePath));
        text = text.Replace("{path_temp}", replacer(ModBase.pathTemp));
        // 时间
        if (replaceTime) // 在窗口标题中，时间会被后续动态替换，所以此时不应该替换
        {
            text = text.Replace("{date}", replacer(Lang.Date(DateTime.Now, "d")));
            text = text.Replace("{time}", replacer(Lang.Date(DateTime.Now, "T")));
        }

        // Minecraft
        text = text.Replace("{java}", replacer(mcLaunchJavaSelected?.Installation.JavaFolder));
        text = text.Replace("{minecraft}", replacer(ModFolder.mcFolderSelected));
        if (ModInstanceList.McMcInstanceSelected?.IsLoaded == true)
        {
            text = text.Replace("{version_path}", replacer(ModInstanceList.McMcInstanceSelected.PathInstance));
            text = text.Replace("{verpath}", replacer(ModInstanceList.McMcInstanceSelected.PathInstance));
            text = text.Replace("{version_indie}", replacer(ModInstanceList.McMcInstanceSelected.PathIndie));
            text = text.Replace("{verindie}", replacer(ModInstanceList.McMcInstanceSelected.PathIndie));
            text = text.Replace("{name}", replacer(ModInstanceList.McMcInstanceSelected.Name));
            if (new[] { "unknown", "old", "pending" }.Contains(
                    ModInstanceList.McMcInstanceSelected.Info.VanillaName.ToLower()))
                text = text.Replace("{version}", replacer(ModInstanceList.McMcInstanceSelected.Name));
            else
                text = text.Replace("{version}", replacer(ModInstanceList.McMcInstanceSelected.Info.VanillaName));
        }
        else
        {
            text = text.Replace("{version_path}", replacer(null));
            text = text.Replace("{verpath}", replacer(null));
            text = text.Replace("{version_indie}", replacer(null));
            text = text.Replace("{verindie}", replacer(null));
            text = text.Replace("{name}", replacer(null));
            text = text.Replace("{version}", replacer(null));
        }

        // 登录信息
        if (mcLoginLoader.State == ModBase.LoadState.Finished)
        {
            text = text.Replace("{user}", replacer(mcLoginLoader.output.Name));
            text = text.Replace("{uuid}", replacer(mcLoginLoader.output.Uuid?.ToLower()));
            switch (mcLoginLoader.input.LoginType)
            {
                case McLoginType.Legacy:
                {
                    text = text.Replace("{login}", replacer("离线"));
                    break;
                }
                case McLoginType.Ms:
                {
                    text = text.Replace("{login}", replacer("正版"));
                    break;
                }
                case McLoginType.Auth:
                {
                    text = text.Replace("{login}", replacer("Authlib-Injector"));
                    break;
                }
            }
        }
        else
        {
            text = text.Replace("{user}", replacer(null));
            text = text.Replace("{uuid}", replacer(null));
            text = text.Replace("{login}", replacer(null));
        }

        return text;
    }

}
