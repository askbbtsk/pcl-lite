using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.IO;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Java.UserPreference;
using PCL.Core.UI;
using PCL.Core.Utils.OS;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceSetup
{

    private void PageSetupSystem_Loaded(object sender, RoutedEventArgs e)
    {
        // 重复加载部分
        PanBack.ScrollToHome();
        RefreshRam(false);

        // 由于各个实例不同，每次都需要重新加载
        ModAnimation.AniControlEnabled += 1;
        Reload();
        ModAnimation.AniControlEnabled -= 1;

        // 非重复加载部分
        if (isLoaded)
            return;
        isLoaded = true;

        // 内存自动刷新
        var timer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 1) };
        timer.Tick += (_, _) => RefreshRam();
        timer.Start();
        RectRamGame.SizeChanged += (s, e) => RefreshRamText();
    }


    public void Reload()
    {
        try
        {
            // 启动参数
            TextArgumentTitle.Text = Config.Instance.Title[PageInstanceLeft.McInstance.PathInstance];
            CheckArgumentTitleEmpty.Checked = Config.Instance.UseGlobalTitle[PageInstanceLeft.McInstance.PathInstance];
            TextArgumentInfo.Text = Config.Instance.TypeInfo[PageInstanceLeft.McInstance.PathInstance];
            var _unused = PageInstanceLeft.McInstance.PathIndie; // 触发自动判定
            ComboArgumentIndieV2.SelectedIndex = Config.Instance.IndieV2[PageInstanceLeft.McInstance.PathInstance] ? 0 : 1;
            CheckArgumentTitleEmpty.Visibility = TextArgumentTitle.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            TextArgumentTitle.HintText = CheckArgumentTitleEmpty.Checked == true ? Lang.Text("Common.Option.Default") : Lang.Text("Instance.Setup.FollowGlobal");
            RefreshJavaComboBox();

            // 游戏内存
            var ramType = Config.Instance.MemorySolution[PageInstanceLeft.McInstance.PathInstance];
            ((MyRadioBox)FindName("RadioRamType" + ramType)).Checked = true;
            SliderRamCustom.Value = Config.Instance.CustomMemorySize[PageInstanceLeft.McInstance.PathInstance];
            SliderRamInitialCustom.Value = Config.Instance.CustomInitialMemorySize[PageInstanceLeft.McInstance.PathInstance];
            RamType(ramType);

            // 服务器
            TextServerEnter.Text = Config.Instance.ServerToEnter[PageInstanceLeft.McInstance.PathInstance];
            ComboServerLoginRequire.SelectedIndex = Config.InstanceAuth.LoginRequirementSolution[PageInstanceLeft.McInstance.PathInstance];
            comboServerLoginLast = ComboServerLoginRequire.SelectedIndex;
            TextServerAuthServer.Text = Config.InstanceAuth.AuthServerAddress[PageInstanceLeft.McInstance.PathInstance];
            TextServerAuthName.Text = Config.InstanceAuth.AuthServerDisplayName[PageInstanceLeft.McInstance.PathInstance];
            TextServerAuthRegister.Text = Config.InstanceAuth.AuthRegisterAddress[PageInstanceLeft.McInstance.PathInstance];
            ServerLogin(ComboServerLoginRequire.SelectedIndex);

            // 高级设置
            ComboAdvanceRenderer.SelectedIndex = Config.Instance.Renderer[PageInstanceLeft.McInstance.PathInstance];
            TextAdvanceClasspathHead.Text = Config.Instance.ClasspathHead[PageInstanceLeft.McInstance.PathInstance];
            TextAdvanceJvm.Text = Config.Instance.JvmArgs[PageInstanceLeft.McInstance.PathInstance];
            TextAdvanceGame.Text = Config.Instance.GameArgs[PageInstanceLeft.McInstance.PathInstance];
            TextAdvanceRun.Text = Config.Instance.PreLaunchCommand[PageInstanceLeft.McInstance.PathInstance];
            CheckAdvanceRunWait.Checked = Config.Instance.PreLaunchCommandWait[PageInstanceLeft.McInstance.PathInstance];
            CheckAdvanceDisableLwjglUnsafeAgent.Checked = Config.Instance.DisableLwjglUnsafeAgent[PageInstanceLeft.McInstance.PathInstance];
            if (Config.Instance.AssetVerifySolutionV1[PageInstanceLeft.McInstance.PathInstance] == 2)
            {
                ModBase.Log("[Setup] 已迁移老版本的关闭文件校验设置");
                Config.Instance.AssetVerifySolutionV1Config.Reset(PageInstanceLeft.McInstance.PathInstance);
                Config.Instance.DisableAssetVerifyV2[PageInstanceLeft.McInstance.PathInstance] = true;
            }

            CheckAdvanceAssetsV2.Checked = Config.Instance.DisableAssetVerifyV2[PageInstanceLeft.McInstance.PathInstance];
            CheckAdvanceUseProxyV2.Checked = Config.Instance.UseProxy[PageInstanceLeft.McInstance.PathInstance];
            CheckAdvanceJava.Checked = Config.Instance.IgnoreJavaCompatibility[PageInstanceLeft.McInstance.PathInstance];
            if (SystemInfo.IsArm64System)
            {
                CheckAdvanceDisableJLW.Checked = true;
                CheckAdvanceDisableJLW.IsEnabled = false;
                CheckAdvanceDisableJLW.ToolTip = Lang.Text("Setup.Launch.Advanced.DisableJlw.Arm64ToolTip");
            }
            else
            {
                CheckAdvanceDisableJLW.Checked = Config.Instance.DisableJlw[PageInstanceLeft.McInstance.PathInstance];
            }
            CheckUseDebugLog4j2Config.Checked = Config.Instance.UseDebugLof4j2Config[PageInstanceLeft.McInstance.PathInstance];
            CheckAdvanceDisableLF.Checked = Config.Instance.DisableLF[PageInstanceLeft.McInstance.PathInstance];
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "重载实例独立设置时出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Setup.Error.OperationFailed"));
        }
    }


    // 初始化
    public void Reset()
    {
        try
        {
            if (!Config.InstanceAuth.AuthLocked[PageInstanceLeft.McInstance.PathInstance])
                Config.InstanceAuth.Reset(PageInstanceLeft.McInstance.PathInstance);

            Config.Instance.Reset(PageInstanceLeft.McInstance.PathInstance);

            ModBase.Log("[Setup] 已初始化实例独立设置");
            HintService.Hint(Lang.Text("Instance.Setup.Initialize.Success"), HintType.Success, false);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "初始化实例独立设置失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Setup.Error.OperationFailed"));
        }

        Reload();
    }


    // 将控件改变路由到设置改变
    private static void SetByTag(string tag, object value)
        => ConfigService.TrySetValue(tag, value, PageInstanceLeft.McInstance.PathInstance);


    private void RadioBoxChange(object o, ModBase.RouteEventArgs routeEventArgs)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;
        if (o is not MyRadioBox { Tag: string tag }) return;

        var slash = tag.IndexOf('/');
        if (slash < 0) return;

        SetByTag(tag[..slash], int.Parse(tag[(slash + 1)..]));
    }


    private void TextBoxChange(object o, TextChangedEventArgs textChangedEventArgs)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;
        if (o is not MyTextBox textBox) return;

        SetByTag(textBox.Tag?.ToString(), textBox.Text);
    }


    private void SliderChange(object o, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;
        if (o is not MySlider slider) return;

        SetByTag(slider.Tag?.ToString(), slider.Value);
    }


    private void ComboChange(MyComboBox sender, object e)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;

        SetByTag(sender.Tag?.ToString(), sender.SelectedIndex);
    }


    private void CheckBoxChange(object sender, bool user)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;
        if (sender is not MyCheckBox checkBox) return;

        SetByTag(checkBox.Tag?.ToString(), checkBox.Checked.GetValueOrDefault());
    }


    // 切换到全局设置
    private void BtnSwitch_Click(object sender, MouseButtonEventArgs e)
    {
        ModMain.frmMain.PageChange(FormMain.PageType.Setup);
    }

}
