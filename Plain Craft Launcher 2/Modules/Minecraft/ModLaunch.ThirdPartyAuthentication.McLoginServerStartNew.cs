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




    private static void McLoginServerStartNew(ModLoader.LoaderTask<McLoginServer, McLoginResult> data)
    {
        var input = data.input;
        LogWrapper.Info("Profile","验证方式：" + input.Description);
        data.Progress = 0.1d;
        var existing = input.IsExist && !ProfileService.IsCreatingProfile ? ProfileService.Current : null;
        var stored = ProfileService.AuthenticateAsync(input.ProviderType, new AuthenticationRequest
        {
            Server = input.BaseUrl,
            DiscoveryAddress = input.DiscoveryAddress,
            Username = input.UserName,
            Password = input.Password,
            ForceRefresh = data.isForceRestarting,
            ForceReselectProfile = input.ForceReselectProfile,
            DeviceCodeHandler = input.ProviderType == ProfileType.YggdrasilConnect
                ? ProfileUi.ShowDeviceCodeLoginAsync
                : null,
            ProfileSelector = (candidates, _) => Task.FromResult(_SelectAuthProfile(candidates))
        }, existing, select: true, data.AbortedToken).GetAwaiter().GetResult();
        if (data.IsAborted) throw new ThreadInterruptedException();

        data.output = new McLoginResult
        {
            AccessToken = stored.AccessToken,
            ClientToken = stored.ClientToken,
            Uuid = stored.Uuid,
            Name = stored.UserName,
            Type = "Auth"
        };
        ProfileService.IsCreatingProfile = false;
        data.Progress = 0.98d;
        LogWrapper.Info("Profile","第三方验证完成");
    }


    private static AuthenticationCandidate? _SelectAuthProfile(IReadOnlyList<AuthenticationCandidate> candidates)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        AuthenticationCandidate? selected = null;
        ModBase.RunInUiWait(() =>
        {
            var controls = candidates.Select(item => (IMyRadio)new MyRadioBox { Text = item.Name }).ToList();
            var index = ModMain.MyMsgBoxSelect(controls, Lang.Text("Minecraft.Launch.Login.Auth.SelectProfile"));
            if (index is >= 0 and < 100000) selected = candidates[index.Value];
        });
        return selected;
    }

}
