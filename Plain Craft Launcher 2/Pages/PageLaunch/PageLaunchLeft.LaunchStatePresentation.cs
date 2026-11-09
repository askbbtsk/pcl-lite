using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.Utils;
using PCL.Network;

namespace PCL;

public partial class PageLaunchLeft
{


    /// <summary>
    ///     切换至启动中页面。
    /// </summary>
    public void PageChangeToLaunching()
    {
        // 修改验证方式
        switch (ProfileService.Current?.ProfileType)
        {
            case ProfileType.Offline:
            {
                LabLaunchingMethod.Text = Lang.Text("Launch.Account.Type.Offline");
                break;
            }
            case ProfileType.Microsoft:
            {
                LabLaunchingMethod.Text = Lang.Text("Launch.Account.Type.Microsoft");
                break;
            }
            case ProfileType.Authlib:
            case ProfileType.YggdrasilConnect:
            {
                LabLaunchingMethod.Text = Lang.Text("Launch.Account.Type.ThirdParty") + (!string.IsNullOrEmpty(ProfileService.Current?.ServerName)
                    ? " / " + ProfileService.Current.ServerName
                    : "");
                break;
            }
        }

        // 初始化页面
        LabLaunchingName.Text = ModInstanceList.McMcInstanceSelected.Name;
        LabLaunchingStage.Text = Lang.Text("Common.Action.Initialize");
        LabLaunchingTitle.Text = ModLaunch.currentLaunchOptions?.SaveBatch is null
            ? Lang.Text("Launch.Status.Title.Launching")
            : Lang.Text("Launch.Status.Title.ExportingScript");
        LabLaunchingProgress.Text = Lang.Number(0d, "P2");
        LabLaunchingProgress.Opacity = 1d;
        LabLaunchingDownload.Visibility = Visibility.Visible;
        LabLaunchingProgressLeft.Opacity = 0.6d;
        LabLaunchingDownload.Visibility = Visibility.Visible;
        LabLaunchingDownload.Text = ModBase.GetString(0) + "/s";
        LabLaunchingDownload.Opacity = 0d;
        LabLaunchingDownload.Visibility = Visibility.Collapsed;
        LabLaunchingDownloadLeft.Opacity = 0d;
        LabLaunchingDownloadLeft.Visibility = Visibility.Collapsed;
        ProgressLaunchingFinished.Width = new GridLength(0d, GridUnitType.Star);
        ProgressLaunchingUnfinished.Width = new GridLength(1d, GridUnitType.Star);
        PanLaunchingHint.Opacity = 0d;
        PanLaunchingHint.Visibility = Visibility.Collapsed;
        PanLaunchingInfo.Width = double.NaN; // 重置宽度改变动画
        ModLaunch.mcLaunchProcess = null;
        ModLaunch.mcLaunchWatcher = null;

        var shouldShowHint = Config.Preference.ShowLaunchingHint;
        if (shouldShowHint)
            LabLaunchingHint.Text = PageLaunchRight.GetRandomHint(true, true);
        else
            LabLaunchingHint.Text = "";

        // 初始化其他页面
        PanInput.IsHitTestVisible = false;
        PanLaunching.IsHitTestVisible = false;
        LoadLaunching.State.LoadingState = MyLoading.MyLoadingState.Run;
        PanLaunching.Visibility = Visibility.Visible;
        ModAnimation.AniStart(
            new[]
            {
                ModAnimation.AaOpacity(PanInput, 0d, 50),
                ModAnimation.AaOpacity(PanInput, -PanInput.Opacity, 110, ease: new ModAnimation.AniEaseInFluent(),
                    after: true),
                ModAnimation.AaScaleTransform(PanInput, 1.2d - ((ScaleTransform)PanInput.RenderTransform).ScaleX, 160),
                ModAnimation.AaOpacity(PanLaunching, 1d - PanLaunching.Opacity, 150, 100),
                ModAnimation.AaScaleTransform(PanLaunching, 1d - ((ScaleTransform)PanLaunching.RenderTransform).ScaleX,
                    500, 100, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaCode(() => PanLaunching.IsHitTestVisible = true, 150)
            }, "Launch State Page"); // 略作延迟，这样如果预检测失败，不会出现奇怪的弹一下的动画
    }


    /// <summary>
    ///     切换至登录页面。
    /// </summary>
    public void PageChangeToLogin()
    {
        if (PageGet(pageCurrent) is ILoginPage loginPage) loginPage.Reload();
        PanInput.IsHitTestVisible = false;
        PanLaunching.IsHitTestVisible = false;
        LoadLaunching.State.LoadingState = MyLoading.MyLoadingState.Stop;
        PanInput.Visibility = Visibility.Visible;
        ModAnimation.AniStart(
            new[]
            {
                ModAnimation.AaOpacity(PanLaunching, -PanLaunching.Opacity, 150),
                ModAnimation.AaScaleTransform(PanLaunching,
                    0.8d - ((ScaleTransform)PanLaunching.RenderTransform).ScaleX, 150,
                    ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaOpacity(PanInput, 1d - PanInput.Opacity, 250, 50),
                ModAnimation.AaScaleTransform(PanInput, 1d - ((ScaleTransform)PanInput.RenderTransform).ScaleX, 300, 50,
                    new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
                ModAnimation.AaCode(() => PanInput.IsHitTestVisible = true, 200)
            }, "Launch State Page", true);
    }




    private enum PageType
    {
        None,
        Auth,
        Ms,
        Profile,
        ProfileSkin,
        Offline
    }


    private object PageGet(PageType type)
    {
        switch (type)
        {
            case PageType.Auth:
            {
                if (ModMain.frmLoginAuth is null)
                    ModMain.frmLoginAuth = new PageLoginAuth();
                return ModMain.frmLoginAuth;
            }
            case PageType.Ms:
            {
                if (ModMain.frmLoginMs is null)
                    ModMain.frmLoginMs = new PageLoginMs();
                return ModMain.frmLoginMs;
            }
            case PageType.Profile:
            {
                if (ModMain.frmLoginProfile is null)
                    ModMain.frmLoginProfile = new PageLoginProfile();
                return ModMain.frmLoginProfile;
            }
            case PageType.ProfileSkin:
            {
                if (ModMain.frmLoginProfileSkin is null)
                    ModMain.frmLoginProfileSkin = new PageLoginProfileSkin();
                return ModMain.frmLoginProfileSkin;
            }
            case PageType.Offline:
            {
                if (ModMain.frmLoginOffline is null)
                    ModMain.frmLoginOffline = new PageLoginOffline();
                return ModMain.frmLoginOffline;
            }

            default:
            {
                throw new ArgumentOutOfRangeException("Type", "即将切换的登录分页编号越界");
            }
        }
    }


    /// <summary>
    ///     切换现有登录页面种类，返回新页面的实例。
    /// </summary>
    /// <param name="type">新页面的种类。</param>
    /// <param name="anim">是否显示动画。</param>
    private object PageChange(PageType type, bool anim)
    {
        object pageNew = ModMain.frmLoginMs; // 初始化一个东西，避免在执行时出现异常导致雪崩
        try
        {

            if (pageCurrent == type)
                return pageNew;
            pageNew = PageGet(type);



            ModAnimation.AniStop("FrmLogin PageChange");
            // 清除页面关联性
            if (pageNew is FrameworkElement element && element.Parent is not null)
            {
                element.SetValue(ContentPresenter.ContentProperty, null);
            }
            if (anim)
            {
                // 动画
                // 执行动画
                Dispatcher.Invoke(() => ModAnimation.AniStart(new[]
                {
                    ModAnimation.AaOpacity(PanLogin, -PanLogin.Opacity, 100, ease: new ModAnimation.AniEaseOutFluent()),
                    ModAnimation.AaCode(() =>
                    {
                        ModAnimation.AniControlEnabled += 1;
                        PanLogin.Children.Clear();
                        PanLogin.Children.Add((UIElement)pageNew);
                        ModAnimation.AniControlEnabled -= 1;
                    }, 100),
                    ModAnimation.AaOpacity(PanLogin, 1d, 100, 120, new ModAnimation.AniEaseInFluent())
                }, "FrmLogin PageChange"), DispatcherPriority.Render);
            }
            else
            {
                // 无动画
                ModAnimation.AniControlEnabled += 1;
                PanLogin.Children.Clear();
                PanLogin.Children.Add((UIElement)pageNew);
                ModAnimation.AniControlEnabled -= 1;
            }


            pageCurrent = type;
            return pageNew;
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Launch.Account.Error.SwitchPage", ModBase.GetStringFromEnum(type)),
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Launch.Account.Error.SwitchPage", ModBase.GetStringFromEnum(type)));
            return pageNew;
        }
    }


    /// <summary>
    ///     确认当前显示的子页面正确，并刷新该页面。
    /// </summary>
    /// <param name="anim">是否显示动画</param>
    /// <param name="targetLoginType">目标验证方式，若正在创建档案需填</param>
    public void RefreshPage(bool anim, ModLaunch.McLoginType targetLoginType = default)
    {
        var type = default(PageType);
        if (targetLoginType != default)
        {
            if (targetLoginType == ModLaunch.McLoginType.Ms)
                type = PageType.Ms;
            if (targetLoginType == ModLaunch.McLoginType.Auth)
                type = PageType.Auth;
            if (targetLoginType == ModLaunch.McLoginType.Legacy)
                type = PageType.Offline;
        }
        else if (ProfileService.Current is not null)
        {
            type = PageType.ProfileSkin;
            BtnLaunch.IsEnabled = true;
        }
        else
        {
            type = PageType.Profile;
            if (_launchButtonAction != LaunchButtonAction.Download)
                BtnLaunch.IsEnabled = false;
        }

        // 刷新页面
        if (pageCurrent == type)
            return;
        PageChange(type, anim);
    }


    private static ModBase.EqualableList<string> SkinMsInput()
    {
        // 获取名称
        return new ModBase.EqualableList<string>
            { ProfileService.Current?.UserName ?? "", ProfileService.Current?.Uuid ?? "" };
    }


    private static void SkinMsLoad(ModLoader.LoaderTask<ModBase.EqualableList<string>, string> data)
    {
        // 清空已有皮肤
        // 如果在输入时清空皮肤，若输入内容一样则不会执行 Load 方法，导致皮肤不被加载
        ModBase.RunInUi(() =>
        {
            if (ModMain.frmLoginProfileSkin is not null && ModMain.frmLoginProfileSkin.Skin is not null)
                ModMain.frmLoginProfileSkin.Skin.Clear();
        });
        // 获取 Url
        var userName = data.input[0];
        var uuid = data.input[1];
        if (ProfileService.Current is not null)
        {
            userName = ProfileService.Current.UserName;
            uuid = ProfileService.Current.Uuid;
        }

        if (string.IsNullOrEmpty(userName))
        {
            data.output = ModBase.pathImage + "Skins/" + ModSkin.McSkinSex(ProfileUi.GetOfflineUuid(userName)) +
                          ".png";
            ModBase.Log("[Minecraft] 获取微软正版皮肤失败，ID 为空");
            goto Finish;
        }

        try
        {
            var result = ModSkin.McSkinGetAddress(uuid, "Ms");
            if (data.IsAborted)
                throw new ThreadInterruptedException("当前任务已取消：" + userName);
            result = ModSkin.McSkinDownload(result);
            if (data.IsAborted)
                throw new ThreadInterruptedException("当前任务已取消：" + userName);
            data.output = result;
        }
        catch (Exception ex)
        {
            if (ex is ThreadInterruptedException)
            {
                data.output = "";
                ModBase.Log("[Minecraft] 已取消皮肤获取：" + userName);
                return;
            }

            if (ex.ToString().Contains("429"))
            {
                data.output = ModBase.pathImage + "Skins/" +
                              ModSkin.McSkinSex(ProfileUi.GetOfflineUuid(userName)) + ".png";
                ModBase.Log(
                    Lang.Text("Launch.Skin.Error.MsRateLimited", userName),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Launch.Skin.Error.MsRateLimited", userName));
            }
            else if (ex.ToString().Contains("未设置自定义皮肤"))
            {
                data.output = ModBase.pathImage + "Skins/" +
                               ModSkin.McSkinSex(ProfileUi.GetOfflineUuid(userName)) + ".png";
                ModBase.Log("[Minecraft] 用户未设置自定义皮肤，跳过皮肤加载");
            }
            else
            {
                data.output = ModBase.pathImage + "Skins/" +
                               ModSkin.McSkinSex(ProfileUi.GetOfflineUuid(userName)) + ".png";
                ModBase.Log(
                    ex,
                    Lang.Text("Launch.Skin.Error.MsGet", userName),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Launch.Skin.Error.MsGet", userName));
            }
        }

        Finish: ;

        // 刷新显示
        if (ModMain.frmLoginProfileSkin is not null && ReferenceEquals(ModMain.frmLoginProfileSkin.Skin.loader, data))
            ModBase.RunInUi(ModMain.frmLoginProfileSkin.Skin.Load);
        else if (!data.IsAborted) // 如果已经中断，Input 也被清空，就不会再次刷新
            data.input = null; // 清空输入，因为皮肤实际上没有被渲染，如果不清空切换到页面的 Start 会由于输入相同而不渲染
    }


    private static ModBase.EqualableList<string> SkinLegacyInput()
    {
        return new ModBase.EqualableList<string>
            { ProfileService.Current?.UserName ?? "", ProfileService.Current?.Uuid ?? "" };
    }


    private static void SkinLegacyLoad(ModLoader.LoaderTask<ModBase.EqualableList<string>, string> data)
    {
        // 清空已有皮肤
        ModBase.RunInUi(() =>
        {
            if (ModMain.frmLoginProfileSkin is not null && ModMain.frmLoginProfileSkin.Skin is not null)
                ModMain.frmLoginProfileSkin.Skin.Clear();
        });
        data.output = ModBase.pathImage + "Skins/" + ModSkin.McSkinSex(data.input[1]) + ".png";
        // 刷新显示
        if (ModMain.frmLoginProfileSkin is not null && ReferenceEquals(ModMain.frmLoginProfileSkin.Skin.loader, data))
            ModBase.RunInUi(() => ModMain.frmLoginProfileSkin.Skin.Load());
        else if (!data.IsAborted) // 如果已经中断，Input 也被清空，就不会再次刷新
            data.input = null; // 清空输入，因为皮肤实际上没有被渲染，如果不清空切换到页面的 Start 会由于输入相同而不渲染
    }


    private static ModBase.EqualableList<string> SkinAuthInput()
    {
        // 获取名称
        return new ModBase.EqualableList<string>
            { ProfileService.Current?.UserName ?? "", ProfileService.Current?.Uuid ?? "" };
    }


    private static void SkinAuthLoad(ModLoader.LoaderTask<ModBase.EqualableList<string>, string> data)
    {
        // 清空已有皮肤
        // 如果在输入时清空皮肤，若输入内容一样则不会执行 Load 方法，导致皮肤不被加载
        ModBase.RunInUi(() =>
        {
            if (ModMain.frmLoginProfileSkin is not null && ModMain.frmLoginProfileSkin.Skin is not null)
                ModMain.frmLoginProfileSkin.Skin.Clear();
        });
        // 获取 Url
        var userName = data.input[0];
        var uuid = data.input[1];
        if (string.IsNullOrEmpty(userName))
        {
            data.output = ModBase.pathImage + "Skins/Steve.png";
            ModBase.Log("[Minecraft] 获取 Authlib-Injector 皮肤失败，ID 为空");
            goto Finish;
        }

        try
        {
            var result = ModSkin.McSkinGetAddress(uuid, "Auth");
            if (data.IsAborted)
                throw new ThreadInterruptedException("当前任务已取消：" + userName);
            result = ModSkin.McSkinDownload(result);
            if (data.IsAborted)
                throw new ThreadInterruptedException("当前任务已取消：" + userName);
            data.output = result;
        }
        catch (Exception ex)
        {
            if (ex is ThreadInterruptedException)
            {
                data.output = "";
                return;
            }

            if (ex.ToString().Contains("429"))
            {
                data.output = ModBase.pathImage + "Skins/Steve.png";
                ModBase.Log(
                    $"[Minecraft] 获取 Authlib-Injector 皮肤失败（{userName}）：获取皮肤太过频繁，请 5 分钟后再试！",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Launch.Skin.Error.AuthlibRateLimited"));
            }
            else if (ex.ToString().Contains("未设置自定义皮肤"))
            {
                data.output = ModBase.pathImage + "Skins/Steve.png";
                ModBase.Log("[Minecraft] 用户未设置自定义皮肤，跳过皮肤加载");
            }
            else
            {
                data.output = ModBase.pathImage + "Skins/Steve.png";
                ModBase.Log(
                    ex,
                    Lang.Text("Launch.Skin.Error.AuthGet", userName),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Launch.Skin.Error.AuthGet", userName));
            }
        }

        Finish: ;

        // 刷新显示
        if (ModMain.frmLoginProfileSkin is not null && ReferenceEquals(ModMain.frmLoginProfileSkin.Skin.loader, data))
            ModBase.RunInUi(ModMain.frmLoginProfileSkin.Skin.Load);
        else if (!data.IsAborted) // 如果已经中断，Input 也被清空，就不会再次刷新
            data.input = null; // 清空输入，因为皮肤实际上没有被渲染，如果不清空切换到页面的 Start 会由于输入相同而不渲染
    }

}
