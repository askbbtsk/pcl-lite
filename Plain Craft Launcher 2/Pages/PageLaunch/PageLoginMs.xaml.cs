using System.Security.Authentication;
using System.Threading;
using System.Windows;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;

namespace PCL;

public partial class PageLoginMs
{
    public PageLoginMs()
    {
        // Handles
        InitializeComponent();
        BtnBack.Click += BtnBack_Click;
        BtnLogin.Click += BtnLogin_Click;
        BtnToken.Click += BtnToken_Click;

        // 没有内置 OAuth 凭据时直接把入口标成不可用，避免用户点了才看到报错。
        if (string.IsNullOrWhiteSpace(PCL.Core.App.Secrets.MSOAuthClientId))
        {
            BtnLogin.IsEnabled = false;
            BtnLogin.Text = Lang.Text("Launch.Account.Microsoft.Unavailable");
        }
    }

    /// <summary>
    ///     用粘贴进来的凭据添加账号：Refresh Token、Minecraft 令牌或微软 Cookie，自动识别。
    ///     <para>
    ///         Refresh Token 和 Cookie 都会换出一整套新令牌，能一直续期，效果和正常登录一样；
    ///         Minecraft 令牌直接用，但 24 小时后失效，且没法续期。
    ///     </para>
    /// </summary>
    private void BtnToken_Click(object sender, EventArgs e)
    {
        // 这个弹窗是阻塞式的（内部推模态帧），必须在 UI 线程上开
        var credential = ModMain.MyMsgBoxInput(
            Lang.Text("Launch.Account.Token.Title"),
            Lang.Text("Launch.Account.Token.Desc"),
            hintText: Lang.Text("Launch.Account.Token.Hint"),
            multiline: true);
        if (string.IsNullOrWhiteSpace(credential)) return;

        BtnToken.IsEnabled = false;
        ProfileService.IsCreatingProfile = true;
        ModBase.RunInNewThread(() =>
        {
            try
            {
                var result = TokenImporter.ImportAsync(credential, CancellationToken.None)
                    .GetAwaiter().GetResult();
                var profile = ProfileService.ApplyAuthenticationResult(result, select: true);
                ModBase.RunInUi(() => ModMain.frmLaunchLeft.RefreshPage(true));
                HintService.Hint(Lang.Text("Launch.Account.Token.Success", profile.UserName), HintType.Success);
            }
            catch (Exception ex)
            {
                // 报错要指到具体哪一步：Cookie 过期和「令牌不归这个应用」是两回事，
                // 都丢给用户对着 invalid_grant 猜没有意义
                var error = (ex as IdentityModelAuthenticationException)?.Error ?? ex.Message;
                if (error.ContainsF("login_required") || error.ContainsF("interaction_required") ||
                    error.ContainsF("invalid_cookie") || error.ContainsF("cookie_flow_failed"))
                    HintService.Hint(Lang.Text("Launch.Account.Token.CookieStale"), HintType.Error);
                else if (error.ContainsF("invalid_grant") || error.ContainsF("invalid_client") ||
                         error.ContainsF("AADSTS7000"))
                    HintService.Hint(Lang.Text("Launch.Account.Token.NotOurs"), HintType.Error);
                ModBase.Log(ex, "[Login] 用令牌登录失败", ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Launch.Account.Token.Failed"));
            }
            finally
            {
                ProfileService.IsCreatingProfile = false;
                ModBase.RunInUi(() => BtnToken.IsEnabled = true);
            }
        }, "Token Login");
    }

    private void BtnBack_Click(object sender, EventArgs e)
    {
        ProfileService.IsCreatingProfile = false;
        ModBase.RunInUi(() => ModMain.frmLaunchLeft.RefreshPage(true));
    }

    private void BtnLogin_Click(object sender, EventArgs e)
    {
        // 微软登录需要 OAuth 客户端凭据（MS_CLIENT_ID），公开构建里不内置。
        // 这里给出明确说明，而不是把底层异常堆栈甩到用户脸上。
        if (string.IsNullOrWhiteSpace(PCL.Core.App.Secrets.MSOAuthClientId))
        {
            HintService.Hint(
                "PCL Lite 未内置微软登录凭据，无法使用正版登录。\n请改用「离线登录」或「第三方登录」，游戏内功能不受影响。",
                HintType.Error);
            return;
        }

        BtnLogin.IsEnabled = false;
        BtnBack.Visibility = Visibility.Collapsed;
        BtnLogin.Text = Lang.Number(0d, "P0");
        ModBase.RunInNewThread(() =>
        {
            var previousProfile = ProfileService.Current;
            var loginSucceeded = false;
            try
            {
                ProfileService.Select(null);
                ModLaunch.mcLoginMsLoader.Start(ProfileUi.GetLoginData(ModLaunch.McLoginType.Ms), true);
                while (ModLaunch.mcLoginMsLoader.State == ModBase.LoadState.Loading)
                {
                    ModBase.RunInUi(() => BtnLogin.Text = Lang.Number(ModLaunch.mcLoginMsLoader.Progress, "P0"));
                    Thread.Sleep(50);
                }

                if (ModLaunch.mcLoginMsLoader.State == ModBase.LoadState.Finished)
                {
                    loginSucceeded = true;
                    ModBase.RunInUi(() => ModMain.frmLaunchLeft.RefreshPage(true));
                }
                else if (ModLaunch.mcLoginMsLoader.State == ModBase.LoadState.Aborted)
                    throw new ThreadInterruptedException();
                else if (ModLaunch.mcLoginMsLoader.Error is null)
                    throw new InvalidOperationException(Lang.Text("Launch.Account.Microsoft.Error.Unknown"));
                else
                    throw ModLaunch.mcLoginMsLoader.Error;
            }
            catch (ThreadInterruptedException)
            {
                if (!loginSucceeded && previousProfile is not null) ProfileService.Select(previousProfile);
                HintService.Hint(Lang.Text("Launch.Account.LoginCancelled"));
            }
            catch (Exception ex)
            {
                if (!loginSucceeded && previousProfile is not null) ProfileService.Select(previousProfile);
                if (ex.Message == "$$")
                {
                }
                else if (ex.Message.StartsWith("$"))
                {
                    HintService.Hint(
                        Lang.Text("Launch.Account.Microsoft.LoginFailed.WithDetail", ex.Message.TrimStart('$')),
                        HintType.Error);
                }
                else if (ex is AuthenticationException && ex.Message.ContainsF("SSL/TLS"))
                {
                    ModBase.Log(
                        ex,
                        $"{Lang.Text("Launch.Account.Microsoft.LoginFailed.Message")}\r\n{ex.Message}",
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Launch.Account.Microsoft.Error.OperationFailed"));
                }
                else
                {
                    ModBase.Log(
                        ex,
                        Lang.Text("Launch.Account.Microsoft.LoginFailed.Title"),
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Launch.Account.Microsoft.LoginFailed.Title"));
                }
            }
            finally
            {
                ProfileService.IsCreatingProfile = false;
                ModBase.RunInUi(() =>
                {
                    BtnLogin.IsEnabled = true;
                    BtnBack.Visibility = Visibility.Visible;
                    BtnLogin.Text = Lang.Text("Launch.Account.Login");
                });
            }
        }, "Ms Login");
    }
}
