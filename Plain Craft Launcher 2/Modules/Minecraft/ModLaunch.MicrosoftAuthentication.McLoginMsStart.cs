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


    private static void McLoginMsStart(ModLoader.LoaderTask<McLoginMs, McLoginResult> data)
    {
        // 没有 client_id 时，后续任何一步都会失败，且报错信息对普通用户毫无意义。
        // 在这里提前拦下，给一条能照着做的提示。
        if (string.IsNullOrWhiteSpace(Secrets.MSOAuthClientId))
            throw new IdentityModelAuthenticationException("microsoft_client_id_missing",
                "当前构建未配置微软登录的 Client ID。");

        var existing = ProfileService.Current?.ProfileType == ProfileType.Microsoft ? ProfileService.Current : null;
        LogWrapper.Info("Profile",$"验证方式：正版（{(existing is null ? "尚未登录" : existing.UserName)}）");
        data.Progress = 0.05d;
        McProfile stored;
        try
        {
            stored = ProfileService.AuthenticateAsync(ProfileType.Microsoft, new AuthenticationRequest
            {
                ForceRefresh = data.isForceRestarting,
                DeviceCodeHandler = ProfileUi.ShowDeviceCodeLoginAsync,
                RefreshFailureHandler = _ConfirmMicrosoftRefreshFailureAsync
            }, existing, select: true, data.AbortedToken).GetAwaiter().GetResult();
        }
        catch (IdentityModelAuthenticationException ex) when (
            ProfileUi.HandleMicrosoftNotConfiguredError(ex) ||
            ProfileUi.HandleMicrosoftXstsError(ex) ||
            ProfileUi.HandleMicrosoftNotOwnedError(ex) ||
            ProfileUi.HandleMicrosoftCreateProfileError(ex))
        {
            throw new IdentityModelException("$$", ex);
        }
        ThrowIfAborted(data);
        ProfileService.IsCreatingProfile = false;

        data.output = new McLoginResult
        {
            AccessToken = stored.AccessToken,
            Name = stored.UserName,
            Uuid = stored.Uuid,
            Type = "Microsoft",
            ClientToken = stored.ClientToken,
            ProfileJson = stored.RawJson
        };

        data.Progress = 0.98d;
        LogWrapper.Info("Profile","正版验证完成");
    }


    /// <summary>
    ///     检查是否被中断
    /// </summary>
    private static void ThrowIfAborted(ModLoader.LoaderTask<McLoginMs, McLoginResult> data)
    {
        if (data.IsAborted)
            throw new ThreadInterruptedException();
    }


    private static Task<bool> _ConfirmMicrosoftRefreshFailureAsync(Exception exception, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LogWrapper.Info("Profile","获取正版 OAuth Token 失败：" + exception);
        var reuseCachedProfile = false;
        ModBase.RunInUiWait(() =>
        {
            if (!isLaunching) return;
            reuseCachedProfile = ModMain.MyMsgBox(
                Lang.Text("Minecraft.Launch.Login.RefreshAccountFailed.Message"),
                Lang.Text("Minecraft.Launch.Login.RefreshAccountFailed.Title"), Lang.Text("Minecraft.Launch.Login.Continue"),
                Lang.Text("Common.Action.Cancel")) == 1;
        });
        return Task.FromResult(reuseCachedProfile);
    }

}
