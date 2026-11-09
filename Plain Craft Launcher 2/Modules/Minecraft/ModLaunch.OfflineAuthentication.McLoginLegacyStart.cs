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



    private static void McLoginLegacyStart(ModLoader.LoaderTask<McLoginLegacy, McLoginResult> data)
    {
        var input = data.input;
        LogWrapper.Info("Profile",$"验证方式：离线（{input.UserName}, {input.Uuid}）");
        data.Progress = 0.1d;
        {
            ref var withBlock = ref data.output;
            withBlock.Name = input.UserName;
            withBlock.Uuid = input.Uuid;
            withBlock.Type = "Legacy";
        }
        // 将结果扩展到所有项目中
        data.output.AccessToken = data.output.Uuid;
        data.output.ClientToken = data.output.Uuid;
    }

}
