using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.UI;
using PCL.Core.UI.Theme;
using PCL.Core.Utils;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageSetupUI
{

    /// <summary>
    ///     把配置里的「启动默认尺寸」同步到下拉框：对得上某个预设就选它，对不上就是「自定义」。
    ///     <para>
    ///         读的是 <c>States.UI</c> 里存的值，不是主窗口当前的 Width / Height。
    ///         用户随手拖动窗口不会写回配置（见 <c>FormMain_SizeChanged</c>），所以这里
    ///         显示的一直是「下次启动会是多大」，不会因为拖了两下就跳成「自定义」。
    ///     </para>
    /// </summary>
    private void RefreshWindowSize()
    {
        ModAnimation.AniControlEnabled += 1;
        try
        {
            var width = States.UI.WindowWidth;
            var height = States.UI.WindowHeight;

            var match = -1;
            for (var i = 0; i < WindowSizePresets.Length; i++)
            {
                if (Math.Abs(WindowSizePresets[i].Width - width) > 0.5) continue;
                if (Math.Abs(WindowSizePresets[i].Height - height) > 0.5) continue;
                match = i;
                break;
            }

            ComboWindowSize.SelectedIndex = match >= 0 ? match : WindowSizePresets.Length;
            PanWindowSizeCustom.Visibility = match >= 0 ? Visibility.Collapsed : Visibility.Visible;
            TextWindowWidth.Text = Math.Round(width).ToString("F0");
            TextWindowHeight.Text = Math.Round(height).ToString("F0");
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }
    }


    private void ComboWindowSize_Change(object senderRaw, SelectionChangedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;

        var index = ComboWindowSize.SelectedIndex;
        if (index < 0) return;

        if (index >= WindowSizePresets.Length)
        {
            // 「自定义」：先把输入框亮出来，尺寸等用户填
            PanWindowSizeCustom.Visibility = Visibility.Visible;
            TextWindowWidth.Text = Math.Round(States.UI.WindowWidth).ToString("F0");
            TextWindowHeight.Text = Math.Round(States.UI.WindowHeight).ToString("F0");
            return;
        }

        PanWindowSizeCustom.Visibility = Visibility.Collapsed;
        var (width, height) = WindowSizePresets[index];
        ApplyWindowSize(width, height);
    }


    private void WindowSizeText_Change(object senderRaw, RoutedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        if (!int.TryParse(TextWindowWidth.Text, out var width)) return;
        if (!int.TryParse(TextWindowHeight.Text, out var height)) return;
        ApplyWindowSize(width, height);
    }


    /// <summary>
    ///     写进 <see cref="States.UI" /> 并立刻套到主窗口上，不用重启。
    /// </summary>
    private void ApplyWindowSize(double width, double height)
    {
        // 别超过屏幕工作区，否则窗口会有一截跑到屏幕外、拖也拖不回来。
        // 用 Max 兜一下：极小的远程桌面分辨率下 min 可能比 max 还大，Math.Clamp 会直接抛
        var work = SystemParameters.WorkArea;
        var appliedWidth = Math.Clamp(width, MinWindowWidth, Math.Max(work.Width, MinWindowWidth));
        var appliedHeight = Math.Clamp(height, MinWindowHeight, Math.Max(work.Height, MinWindowHeight));

        States.UI.WindowWidth = appliedWidth;
        States.UI.WindowHeight = appliedHeight;

        if (ModMain.frmMain is { } main)
        {
            main.Width = appliedWidth;
            main.Height = appliedHeight;
        }

        // 被屏幕夹小了就把输入框改回实际值，免得显示的数字和窗口对不上
        if (Math.Abs(appliedWidth - width) > 0.5 || Math.Abs(appliedHeight - height) > 0.5)
        {
            ModAnimation.AniControlEnabled += 1;
            try
            {
                TextWindowWidth.Text = Math.Round(appliedWidth).ToString("F0");
                TextWindowHeight.Text = Math.Round(appliedHeight).ToString("F0");
            }
            finally
            {
                ModAnimation.AniControlEnabled -= 1;
            }
        }
    }


    /// <summary>
    /// 生成主题单选组：第一行是常规主题，后两行是隐藏主题，末尾是「自定义」。
    /// </summary>
    private void BuildThemeRadios()
    {
        if (_themeRadios.Count > 0) return;

        for (var i = 0; i < PclThemePresets.All.Count; i++)
        {
            var preset = PclThemePresets.All[i];
            var radio = _CreateThemeRadio(_PresetName(preset), preset.Index);
            Grid.SetRow(radio, i / 5);
            Grid.SetColumn(radio, i % 5);
            PanThemeRadios.Children.Add(radio);
            _themeRadios.Add(radio);
        }

        var custom = _CreateThemeRadio(Lang.Text("Setup.Ui.Theme.Custom"), PclThemePresets.CustomIndex);
        Grid.SetRow(custom, 2);
        Grid.SetColumn(custom, 4);
        PanThemeRadios.Children.Add(custom);
        _themeRadios.Add(custom);
    }


    /// <summary>
    /// 取主题的本地化名称；语言包里没有对应键时退回内置中文名。
    /// </summary>
    private static string _PresetName(PclThemePreset preset)
    {
        var key = $"Setup.Ui.Theme.Preset.{preset.Index}";
        var text = Lang.Text(key);
        return text == key || text.StartsWith('!') ? preset.Name : text;
    }


    private MyRadioBox _CreateThemeRadio(string text, int index)
    {
        var radio = new MyRadioBox
        {
            Text = text,
            Height = 26,
            Tag = index,
            Margin = new Thickness(0, 0, 10, 0)
        };
        radio.Check += ThemeRadio_Check;
        return radio;
    }


    private void ThemeRadio_Check(object sender, ModBase.RouteEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0) return;
        if (sender is not MyRadioBox { Tag: int index }) return;

        if (index == PclThemePresets.CustomIndex)
        {
            // 切到「自定义」：保留当前参数，只把面板展开
            SetByTag("UiCustomTheme", true);
            Config.Preference.Theme.ThemeSelected = PclThemePresets.CustomIndex;
            PanCustomTheme.Visibility = Visibility.Visible;
            RefreshCustomThemeLabels();
            RefreshCustomThemePreview();
            RefreshThemePresetCards();
            return;
        }

        if (PclThemePresets.ByIndex(index) is { } preset)
            ApplyPreset(preset, false);
    }


    /// <summary>
    /// 把单选的勾选状态同步到当前主题编号。
    /// </summary>
    private void RefreshThemeRadioState()
    {
        var selected = Config.Preference.Theme.ThemeSelected;
        if (selected != PclThemePresets.CustomIndex && PclThemePresets.ByIndex(selected) is null)
            selected = PclThemePresets.CustomIndex;

        ModAnimation.AniControlEnabled += 1;
        try
        {
            foreach (var radio in _themeRadios)
            {
                var should = radio.Tag is int index && index == selected;
                if (radio.Checked != should) radio.Checked = should;
            }
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }
    }


    /// <summary>
    /// 刷新自定义主题区域：面板可见性、滑块数值、数值标签与色卡预览。
    /// </summary>
    public void RefreshCustomTheme()
    {
        BuildThemeRadios();

        _customThemeBinding = true;
        try
        {
            var config = Config.Preference.Theme;
            SliderCustomHue.Value = config.WindowHue;
            SliderCustomSat.Value = config.WindowSat;
            SliderCustomLight.Value = config.WindowLight;
            SliderCustomDelta.Value = config.WindowDelta;
            PanCustomTheme.Visibility = config.ThemeSelected == PclThemePresets.CustomIndex
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _customThemeBinding = false;
        }

        RefreshCustomThemeLabels();
        RefreshCustomThemePreview();
        RefreshThemePresetCards();
        RefreshThemeRadioState();
    }


    /// <summary>
    /// 生成「看腻了蓝色？」下面那一排预设色卡。
    /// <para>
    /// 每张色卡用该主题的主色填充，点击直接套用；当前配色与某个预设完全一致时打勾。
    /// </para>
    /// </summary>
    private void RefreshThemePresetCards()
    {
        if (PanThemePresets.Children.Count != PclThemePresets.All.Count)
        {
            PanThemePresets.Children.Clear();
            foreach (var preset in PclThemePresets.All)
            {
                var swatch = new Border
                {
                    Width = 32,
                    Height = 32,
                    Margin = new Thickness(0, 0, 8, 0),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Tag = preset,
                    SnapsToDevicePixels = true,
                    ToolTip = preset.IsRandom ? $"{preset.Name}（每次点击随机换色）" : preset.Name
                };
                swatch.Child = new TextBlock
                {
                    Text = "✓",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
                    Effect = new DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 5,
                        ShadowDepth = 0,
                        Opacity = 0.55
                    }
                };
                swatch.MouseLeftButtonUp += ThemePreset_Click;
                PanThemePresets.Children.Add(swatch);
            }
        }

        // 找出当前配色命中的预设，命中则打勾
        var current = ThemeService.GetCustomThemeArgs();
        var matched = current is { } args ? PclThemePresets.Match(args) : null;
        var isDark = ThemeService.IsDarkMode;

        foreach (var child in PanThemePresets.Children)
        {
            if (child is not Border { Tag: PclThemePreset preset } swatch) continue;

            // 深色模式压暗一档，浅色模式提亮一档，保证色卡本身在两种模式下都看得清
            var (r, g, b) = PclThemeEngine.FromHsl2(
                preset.Args.Hue, preset.Args.Saturation, isDark ? 42d : 58d);
            swatch.Background = new SolidColorBrush(
                Color.FromRgb((byte)r, (byte)g, (byte)b));

            var selected = matched is not null && matched.Index == preset.Index;
            swatch.BorderBrush = selected
                ? (Brush)FindResource("ColorBrush1")
                : Brushes.Transparent;
            if (swatch.Child is TextBlock check)
                check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }


    private void ThemePreset_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: PclThemePreset preset }) return;
        // 色卡是「自定义」模式下的快速填充工具，点完仍留在自定义
        ApplyPreset(preset, true);
    }


    /// <summary>
    /// 套用一个预设配色，并把参数写回配置（含标题栏三点渐变）。
    /// </summary>
    /// <param name="preset">要套用的预设。</param>
    /// <param name="keepCustom">
    /// 为 <see langword="true"/> 时留在「自定义」模式（点色卡），
    /// 否则把主题编号切到该预设（点单选）。
    /// </param>
    private void ApplyPreset(PclThemePreset preset, bool keepCustom)
    {
        var args = preset.Args;
        if (preset.IsRandom)
        {
            // 原版「欧皇彩」：每次点都随机一个色相
            args = args with { Hue = Random.Shared.Next(0, 360) };
        }

        ModAnimation.AniControlEnabled += 1;
        try
        {
            SliderCustomHue.Value = args.Hue;
            SliderCustomSat.Value = args.Saturation;
            SliderCustomLight.Value = args.LightAdjust + 20;
            SliderCustomDelta.Value = args.TopbarDelta + 90;
            PanCustomTheme.Visibility = keepCustom ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }

        SetByTag("UiCustomTheme", true);
        SetByTag("UiLauncherHue", args.Hue);
        SetByTag("UiLauncherSat", args.Saturation);
        SetByTag("UiLauncherLight", args.LightAdjust + 20);
        SetByTag("UiLauncherDelta", args.TopbarDelta + 90);
        SetByTag("UiLauncherDeltaTriple",
            args.TopbarHueTriple is { Length: 3 } triple ? string.Join(",", triple) : "");
        Config.Preference.Theme.ThemeSelected = keepCustom ? PclThemePresets.CustomIndex : preset.Index;

        RefreshCustomThemeLabels();
        RefreshCustomThemePreview();
        RefreshThemePresetCards();
        if (!keepCustom) RefreshThemeRadioState();
        ThemeManager.ThemeRefresh();

        HintService.Hint(string.Format(Lang.Text("Setup.Ui.CustomTheme.Imported"), preset.Name),
            HintType.Success, false);
    }


    private void RefreshCustomThemeLabels()
    {
        TextCustomHue.Text = $"{SliderCustomHue.Value}°";
        TextCustomSat.Text = $"{SliderCustomSat.Value}%";
        var light = SliderCustomLight.Value - 20;
        TextCustomLight.Text = light switch
        {
            > 0 => $"+{light}",
            _ => light.ToString()
        };
        var delta = SliderCustomDelta.Value - 90;
        TextCustomDelta.Text = $"{delta}°";
    }


    /// <summary>
    /// 按当前滑块数值实时渲染八级色卡。
    /// </summary>
    private void RefreshCustomThemePreview()
    {
        var args = new PclThemeArgs(
            Math.Clamp(SliderCustomHue.Value, 0, 360),
            Math.Clamp(SliderCustomSat.Value, 0, 100),
            Math.Clamp(SliderCustomLight.Value - 20, -20, 20),
            Math.Clamp(SliderCustomDelta.Value - 90, -180, 180));
        // 最后一项是近乎全透明的 SemiTransparent，放在色卡里只会显示成一块黑，直接跳过
        var swatches = PclThemeEngine.GenerateSwatches(args, ThemeService.IsDarkMode, ThemeService.CurrentTone)[..10];

        if (PanCustomPreview.Children.Count != swatches.Length)
        {
            PanCustomPreview.Children.Clear();
            foreach (var _ in swatches)
                PanCustomPreview.Children.Add(new Border { BorderThickness = new Thickness(0) });
        }

        for (var i = 0; i < swatches.Length; i++)
            ((Border)PanCustomPreview.Children[i]).Background = new SolidColorBrush(swatches[i]);
    }


    private void CustomThemeSlider_OnChange(object senderRaw, bool user)
    {
        SliderChange(senderRaw, user);
        RefreshCustomThemeLabels();
        RefreshCustomThemePreview();

        // 手动调过滑块就不再属于任何预设：清掉三点渐变，并留在「自定义」
        if (!_customThemeBinding && ModAnimation.AniControlEnabled == 0)
        {
            SetByTag("UiLauncherDeltaTriple", "");
            Config.Preference.Theme.ThemeSelected = PclThemePresets.CustomIndex;
            RefreshThemePresetCards();
            ThemeManager.ThemeRefresh();
        }
    }


    private void BtnCustomReset_Click(object sender, MouseButtonEventArgs e)
    {
        ApplyPreset(PclThemePresets.Main[0], true);
    }



    // 赞助
    private void BtnLauncherDonate_Click(object sender, MouseButtonEventArgs e)
    {
        ModBase.OpenWebsite("https://afdian.com/a/LTCat");
    }


    // 滑动条
    private void SliderLoad()
    {
        SliderMusicVolume.getHintText = new Func<object, object>(v =>
            Lang.Number(Math.Ceiling(Convert.ToDouble(v) * 0.1d) / 100d, "P0"));
        SliderLauncherOpacity.getHintText = new Func<object, object>(v =>
            Lang.Number(LiquidGlassCore.IsEnabled
                ? PCL.Core.UI.LiquidGlassComposition.BackgroundOpacity(true, Convert.ToInt32(v))
                : PCL.Core.UI.LiquidGlassComposition.WindowOpacity(false, Convert.ToInt32(v)), "P0"));
        SliderBackgroundOpacity.getHintText = new Func<object, object>(v =>
            Lang.Number(Math.Round(Convert.ToDouble(v) * 0.1d) / 100d, "P0"));
        SliderBackgroundBlur.getHintText = new Func<object, object>(v => Lang.Text("Setup.Ui.Slider.Pixel", Lang.Number(Convert.ToDouble(v), "N0")));
        SliderBlurValue.getHintText = new Func<object, object>(v => Lang.Text("Setup.Ui.Slider.Pixel", Lang.Number(Convert.ToDouble(v), "N0")));
        SliderBlurSamplingRate.getHintText = new Func<object, object>(v => Lang.Number(Convert.ToDouble(v) / 100d, "P0"));
    }


    private void CheckMusicStart_OnChange(object sender, bool user)
    {
        CheckBoxChange(sender, user);
        CheckMusicStart_Change(sender, user);
    }


    private void CheckMusicStop_OnChange(object sender, bool user)
    {
        CheckBoxChange(sender, user);
        CheckMusicStop_Change();
    }



    /// <summary>
    ///     是否强制显示被禁用的功能。
    /// </summary>
    public static bool HiddenForceShow
    {
        get => field;
        set
        {
            field = value;
            HiddenRefresh();
        }
    }


    /// <summary>
    ///     更新功能隐藏带来的显示变化。
    /// </summary>
    public static void HiddenRefresh()
    {
        if (ModMain.frmMain.PanTitleSelect is null || !ModMain.frmMain.PanTitleSelect.IsLoaded)
            return;
        try
        {
            // 获取配置组引用以缩短代码
            var conf = Config.Preference.Hide;

            // 主导航布局由 FormMain 按折叠名单和实际可见页面数统一重排。
            ModMain.frmMain.PanTitleSelect.Visibility = Visibility.Visible;

            // 功能隐藏设置卡片
            if (ModMain.frmSetupUI is not null)
            {
                ModMain.frmSetupUI.CardSwitch.Visibility = !HiddenForceShow && conf.FunctionHidden
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupUI.CardSwitch.Title = HiddenForceShow ? Lang.Text("Setup.Ui.FeatureHide.TitleTemporarilyDisabled") : Lang.Text("Setup.Ui.FeatureHide.Title");
            }

            // 设置子页面 (FrmSetupLeft)
            if (ModMain.frmSetupLeft is not null)
            {
                ModMain.frmSetupLeft.ItemLaunch.Visibility =
                    !HiddenForceShow && conf.SetupLaunch ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemUI.Visibility =
                    !HiddenForceShow && conf.SetupUi ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemLauncherLanguage.Visibility = !HiddenForceShow && conf.SetupLauncherLanguage
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupLeft.ItemGameManage.Visibility = !HiddenForceShow && conf.SetupGameManage
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupLeft.ItemLauncherMisc.Visibility = !HiddenForceShow && conf.SetupLauncherMisc
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupLeft.ItemJava.Visibility =
                    !HiddenForceShow && conf.SetupJava ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemUpdate.Visibility =
                    !HiddenForceShow && conf.SetupUpdate ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemAbout.Visibility =
                    !HiddenForceShow && conf.SetupAbout ? Visibility.Collapsed : Visibility.Visible;
                ModMain.frmSetupLeft.ItemFeedback.Visibility = !HiddenForceShow && conf.SetupFeedback
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                ModMain.frmSetupLeft.ItemLog.Visibility =
                    !HiddenForceShow && conf.SetupLog ? Visibility.Collapsed : Visibility.Visible;

                var categories = new[]
                {
                    (ModMain.frmSetupLeft.TextGameCategory,
                        !(conf.SetupLaunch && conf.SetupJava && conf.SetupGameManage)),
                    (ModMain.frmSetupLeft.TextLauncherCategory, !(conf.SetupUi && conf.SetupLauncherLanguage && conf.SetupLauncherMisc)),
                    (ModMain.frmSetupLeft.TextAboutCategory,
                        !(conf.SetupAbout && conf.SetupUpdate && conf.SetupFeedback && conf.SetupLog))
                };

                foreach (var category in categories)
                {
                    var isVisible = category.Item2 || HiddenForceShow;
                    category.Item1.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
                    if (isVisible)
                        category.Item1.Opacity = 0.6d;
                }

                // 统计设置页可用项数量
                var setupCount = 0;
                if (!conf.SetupLaunch)
                    setupCount += 1;
                if (!conf.SetupUi)
                    setupCount += 1;
                if (!conf.SetupLauncherLanguage)
                    setupCount += 1;
                if (!conf.SetupGameManage)
                    setupCount += 1;
                if (!conf.SetupLauncherMisc)
                    setupCount += 1;
                if (!conf.SetupJava)
                    setupCount += 1;
                if (!conf.SetupUpdate)
                    setupCount += 1;
                if (!conf.SetupAbout)
                    setupCount += 1;
                if (!conf.SetupFeedback)
                    setupCount += 1;
                if (!conf.SetupLog)
                    setupCount += 1;
                ModMain.frmSetupLeft.PanItem.Visibility =
                    setupCount < 2 && !HiddenForceShow ? Visibility.Collapsed : Visibility.Visible;
            }

            ModMain.frmMain.RefreshNavigationOverflow();

            // 工具子页面 (FrmToolsLeft)
            if (ModMain.frmToolsLeft is not null)
            {
                ModMain.frmToolsLeft.ItemTest.Visibility =
                    !HiddenForceShow && conf.ToolsTest ? Visibility.Collapsed : Visibility.Visible;

                // 处理分类标题
                var isToolsVisible = (!HiddenForceShow && !conf.ToolsTest) || HiddenForceShow;
                ModMain.frmToolsLeft.TextToolsCategory.Visibility = isToolsVisible ? Visibility.Visible : Visibility.Collapsed;
                if (isToolsVisible) ModMain.frmToolsLeft.TextToolsCategory.Opacity = 0.6;

                // 统计工具页可用项数量
                var toolsCount = 0;
                if (!conf.ToolsTest)
                    toolsCount += 1;
                ModMain.frmToolsLeft.PanItem.Visibility =
                    toolsCount < 2 && !HiddenForceShow ? Visibility.Collapsed : Visibility.Visible;
            }

            // 其他入口刷新
            if (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSelect)
                ModMain.frmSelectRight.BtnEmptyDownload_Loaded();
            if (ModMain.frmMain.pageCurrent == FormMain.PageType.Launch)
                ModMain.frmLaunchLeft.RefreshButtonsUI();
            if (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSetup &&
                ModMain.frmInstanceModDisabled is not null)
                ModMain.frmInstanceModDisabled.BtnDownload_Loaded();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "刷新功能隐藏项目失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Setup.Ui.Error.OperationFailed"));
        }
    }


    // ================= 设置页面协同 =================
    private void HiddenSetupMain()
    {
        var isChecked = (bool)CheckHiddenPageSetup.Checked;
        CheckHiddenSetupLaunch.Checked = isChecked;
        CheckHiddenSetupUI.Checked = isChecked;
        CheckHiddenSetupLauncherLanguage.Checked = isChecked;
        CheckHiddenSetupGameManage.Checked = isChecked;
        CheckHiddenLauncherMisc.Checked = isChecked;
        CheckHiddenSetupJava.Checked = isChecked;
        CheckHiddenSetupUpdate.Checked = isChecked;
        CheckHiddenSetupAbout.Checked = isChecked;
        CheckHiddenSetupFeedback.Checked = isChecked;
        CheckHiddenSetupLog.Checked = isChecked;
    }


    // ================= 设置页面协同 =================
    private void HiddenSetupMain(object sender, bool user)
    {
        if (!user)
            return; // 仅处理用户点击，防止死循环
        var isChecked = (bool)CheckHiddenPageSetup.Checked;
        CheckHiddenSetupLaunch.Checked = isChecked;
        CheckHiddenSetupUI.Checked = isChecked;
        CheckHiddenSetupLauncherLanguage.Checked = isChecked;
        CheckHiddenSetupGameManage.Checked = isChecked;
        CheckHiddenLauncherMisc.Checked = isChecked;
        CheckHiddenSetupJava.Checked = isChecked;
        CheckHiddenSetupUpdate.Checked = isChecked;
        CheckHiddenSetupAbout.Checked = isChecked;
        CheckHiddenSetupFeedback.Checked = isChecked;
        CheckHiddenSetupLog.Checked = isChecked;
    }


    private void HiddenSetupSub(object sender, bool user)
    {
        if (!user)
            return;
        var conf = Config.Preference.Hide;
        // 判断是否全部勾选
        var allChecked = conf.SetupLaunch && conf.SetupUi && conf.SetupLauncherLanguage && conf.SetupJava &&
                         conf.SetupUpdate && conf.SetupAbout && conf.SetupFeedback &&
                         conf.SetupLog && conf.SetupLauncherMisc && conf.SetupGameManage;
        CheckHiddenPageSetup.Checked = allChecked;
    }


    // ================= 工具页面协同 =================
    private void HiddenToolsMain(object sender, bool user)
    {
        if (!user)
            return;
        var isChecked = (bool)CheckHiddenPageTools.Checked;
        CheckHiddenToolsTest.Checked = isChecked;
    }


    private void HiddenToolsSub(object sender, bool user)
    {
        if (!user)
            return;
        var conf = Config.Preference.Hide;
        var allChecked = conf.ToolsTest;
        CheckHiddenPageTools.Checked = allChecked;
    }


    // 警告提示
    private void HiddenHint(object sender, bool user)
    {
        if (ModAnimation.AniControlEnabled == 0 && sender is MyCheckBox checkBox && checkBox.Checked == true)
            HintService.Hint(Lang.Text("Setup.Ui.FeatureHide.TemporaryHint"));
    }

}
