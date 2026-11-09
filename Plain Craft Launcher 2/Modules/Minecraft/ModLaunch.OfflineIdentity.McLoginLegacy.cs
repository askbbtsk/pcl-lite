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



    public class McLoginLegacy : McLoginData
    {
        /// <summary>
        ///     若采用正版皮肤，则为该皮肤名。
        /// </summary>
        public string SkinName;

        /// <summary>
        ///     皮肤种类。
        /// </summary>
        public int SkinType;

        /// <summary>
        ///     登录用户名。
        /// </summary>
        public string UserName;

        /// <summary>
        ///     UUID。
        /// </summary>
        public string Uuid;

        public McLoginLegacy()
        {
            LoginType = McLoginType.Legacy;
        }

        public override int GetHashCode()
        {
            return (int)Math.Round(
                ModBase.GetHash(UserName + SkinType + SkinName + (int)LoginType) % (decimal)int.MaxValue);
        }
    }



    // 登录返回结果
    public struct McLoginResult
    {
        public string Name;
        public string Uuid;
        public string AccessToken;
        public string Type;
        public string ClientToken;

        /// <summary>
        ///     进行微软登录时返回的 profile 信息。
        /// </summary>
        public string ProfileJson;
    }


    public static McLoginData McLoginInput()
    {
        McLoginData loginData = null;
        try
        {
            loginData = ProfileUi.GetLoginData();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Minecraft.Launch.Login.Error.Input"),
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Launch.Login.Error.Input"));
        }

        return loginData;
    }


    private static void McLoginStart(ModLoader.LoaderTask<McLoginData, McLoginResult> data)
    {
        ModBase.Log("[Profile] 开始加载选定档案");
        // 校验登录信息
        var checkResult = ProfileUi.IsProfileValid();
        if (!string.IsNullOrEmpty(checkResult))
            throw new ArgumentException(checkResult);
        // 获取对应加载器
        ModLoader.LoaderBase loader = null;
        switch (data.input.LoginType)
        {
            case McLoginType.Ms:
            {
                loader = mcLoginMsLoader;
                break;
            }
            case McLoginType.Legacy:
            {
                loader = mcLoginLegacyLoader;
                break;
            }
            case McLoginType.Auth:
            {
                loader = mcLoginAuthLoader;
                break;
            }
        }

        // 尝试加载
        loader.WaitForExit(data.input, mcLoginLoader, data.isForceRestarting);
        data.output = (McLoginResult)((dynamic)loader).output;
        ModBase.RunInUi(() => ModMain.frmLaunchLeft.RefreshPage(false)); // 刷新自动填充列表
        ModBase.Log("[Profile] 选定档案加载完成");
    }

}
