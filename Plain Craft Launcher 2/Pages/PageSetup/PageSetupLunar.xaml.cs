using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;

namespace PCL;

/// <summary>
///     Lunar Client 的启动设置。
///     <para>
///         分成三块：Java 运行环境、全局内存与 JVM 参数、按版本覆盖。
///         配置读写统一走 <see cref="LunarLaunchProfile" />，界面只管展示和回写，
///         免得「设置页算一套、启动时算另一套」。
///     </para>
///     <para>
///         这里刻意全部用中文字面量，没走 <c>DynamicResource</c>：整个 Lunar 模块
///         都是这个风格，混用会让 zh-TW / en 那几份字典凭空多出一批只在这里出现的键。
///     </para>
/// </summary>
public partial class PageSetupLunar
{
    /// <summary>
    ///     程序化改控件时置位，避免把「界面初始化」当成「用户改动」写回配置。
    /// </summary>
    private bool _suppress;

    /// <summary>
    ///     官方 Lunar 启动器的设置快照。界面初始化时读一次，点「读取」按钮时再读一次。
    /// </summary>
    private LunarLauncherSettings _lunarLauncher = LunarLauncherSettings.Empty;

    public PageSetupLunar()
    {
        // XAML 里控件的初始状态会在解析期触发 Change / TextChanged，
        // 那时候配置还没读出来，不挡住就会用默认值覆盖用户的设置
        ModAnimation.AniControlEnabled += 1;
        try
        {
            InitializeComponent();
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }

        Loaded += (_, _) =>
        {
            ModAnimation.AniControlEnabled += 1;
            try
            {
                Reload();
            }
            finally
            {
                ModAnimation.AniControlEnabled -= 1;
            }
        };
    }

    /// <summary>
    ///     当前下拉框选中的版本号。
    /// </summary>
    private string? SelectedVersionId =>
        ComboVersion.SelectedItem is MyComboBoxItem { Tag: string id } && !string.IsNullOrEmpty(id) ? id : null;

    #region 读取

    private void Reload()
    {
        _suppress = true;
        try
        {
            var lunar = Config.Preference.Lunar;

            TextJavaPath.Text = lunar.JavaPath ?? string.Empty;
            TextJvm.Text = lunar.JvmArgs ?? string.Empty;

            // 官方启动器那份设置只读不写：PCL 没填的时候沿用它，
            // 这样「Lunar 启动器里设的 8G」不会在换 PCL 启动时凭空变成 3G
            _lunarLauncher = LunarLauncherConfig.Read(LunarLauncherConfig.ResolveRoot(lunar.Root));
            TextMemory.Text = _DisplayedMemory(lunar.MemoryMb).ToString();

            RefreshVersionCombo();
        }
        finally
        {
            _suppress = false;
        }

        RefreshJavaHint();
        RefreshEnabledState();
        RefreshMemoryHint();
        LoadVersionOverride();
        RefreshEffectiveHint();

        // 版本列表可能是空的（缓存没写过、也没联网拉过）
        _EnsureVersionsAsync();
    }

    private void RefreshVersionCombo()
    {
        var wanted = ModLunar.SelectedSubVersion;

        ComboVersion.Items.Clear();
        MyComboBoxItem? selected = null;
        foreach (var version in ModLunar.Versions)
        {
            var item = new MyComboBoxItem
            {
                Content = BuildVersionLabel(version.Id),
                Tag = version.Id,
                ToolTip = ModLunar.DescribeVersion(version)
            };
            ComboVersion.Items.Add(item);
            if (string.Equals(version.Id, wanted, StringComparison.OrdinalIgnoreCase))
                selected = item;
        }

        if (ComboVersion.Items.Count == 0)
        {
            ComboVersion.Items.Add(new MyComboBoxItem
            {
                Content = Lang.Text("Lunar.Setup.VersionEmpty"),
                IsEnabled = false,
                Tag = null
            });
            ComboVersion.SelectedIndex = 0;
        }
        else
        {
            // 和 Lunar 板块一样，选中项在全部加完之后显式指定：
            // 逐项 Add 时 Selector 会自己选中第一项，而列表是版本号降序的，
            // 第一项正好是最新的 drop —— 那就成了「默认选了最新版」
            ComboVersion.SelectedItem = selected ?? ComboVersion.Items[0];
        }
    }

    private static string BuildVersionLabel(string versionId) =>
        LunarLaunchProfile.GetOverride(versionId).Enabled ? versionId + "（已覆盖）" : versionId;

    private async void _EnsureVersionsAsync()
    {
        // 没装 Lunar 也要拉：版本列表是「选哪个版本」的前提，物料由 PCL 自己补
        if (ModLunar.Versions.Count > 0) return;

        try
        {
            await ModLunar.RefreshVersionsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 拉不到就保持空列表，页面上会显示「还没拿到版本列表」
            ModBase.Log(ex, "[Lunar] 设置页获取版本列表失败", ModBase.LogLevel.Developer);
            return;
        }

        if (!IsLoaded) return;

        _suppress = true;
        try
        {
            RefreshVersionCombo();
        }
        finally
        {
            _suppress = false;
        }

        LoadVersionOverride();
        RefreshEffectiveHint();
    }

    /// <summary>
    ///     把选中版本的覆盖值填进下面的控件。
    /// </summary>
    private void LoadVersionOverride()
    {
        var id = SelectedVersionId;
        var over = LunarLaunchProfile.GetOverride(id);

        _suppress = true;
        try
        {
            CheckVersionOverride.Checked = over.Enabled;
            TextVersionMemory.Text = over.MemoryMb > 0 ? over.MemoryMb.ToString() : string.Empty;
            TextVersionJvm.Text = over.JvmArgs;
        }
        finally
        {
            _suppress = false;
        }

        RefreshEnabledState();
    }

    #endregion

    #region 界面状态

    private void RefreshEnabledState()
    {
        if (PanVersionOptions is not null)
            // 用折叠而不是 IsEnabled=false：禁用态的输入框在深色主题下会变成两条黑条，
            // 里面还留着「留空 = 沿用全局」的占位文字，看着像出了 bug
            PanVersionOptions.Visibility = CheckVersionOverride.Checked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    ///     说明这个数字是哪来的。
    ///     <para>
    ///         用户最常问的就是「我在 Lunar 启动器里调成 8G，怎么换 PCL 启动就变回去了」，
    ///         所以这里必须写清楚：现在用的是哪一份、改了会不会影响到官方启动器。
    ///     </para>
    /// </summary>
    private void RefreshMemoryHint()
    {
        if (HintMemory is null) return;

        var configured = Config.Preference.Lunar.MemoryMb;
        HintMemory.Theme = MyHint.Themes.Blue;

        if (configured > 0)
        {
            HintMemory.Text = Lang.Text("Lunar.Setup.MemoryCustom");
            return;
        }

        HintMemory.Text = _lunarLauncher.HasMemory
            ? string.Format(Lang.Text("Lunar.Setup.MemoryFromLunar"), _lunarLauncher.AllocatedMemoryMb)
            : Lang.Text("Lunar.Setup.MemoryNoNative");
    }

    private void RefreshJavaHint()
    {
        var configured = Config.Preference.Lunar.JavaPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            HintJava.Visibility = Visibility.Collapsed;
            return;
        }

        if (LunarLaunchProfile.NormalizeJavaPath(configured) is not null)
        {
            HintJava.Visibility = Visibility.Collapsed;
            return;
        }

        HintJava.Text = "这个路径下找不到 java.exe，启动时会退回 Lunar 自带的 JRE。";
        HintJava.Visibility = Visibility.Visible;
    }

    /// <summary>
    ///     把「这个版本最终会用多少内存、哪些参数」摆出来。
    ///     用户最常问的就是「我改了到底生效没有」，与其让他去看日志，不如直接写在页面上。
    /// </summary>
    private void RefreshEffectiveHint()
    {
        var id = SelectedVersionId;
        if (id is null)
        {
            HintEffective.Theme = MyHint.Themes.Yellow;
            HintEffective.Text = "先选一个版本，才能看到它实际生效的设置。";
            return;
        }

        var settings = LunarLaunchProfile.Resolve(id, ModLunar.ResolvePclGlobalMemoryMb(), _lunarLauncher);
        var memorySource = LunarLaunchProfile.DescribeSource(settings.MemorySource);
        var jvmSource = LunarLaunchProfile.DescribeSource(settings.JvmSource);
        var jvmCount = settings.JvmArguments.Count;

        var jvmPart = jvmCount == 0 ? "没有额外的 JVM 参数" : $"{jvmCount} 个附加 JVM 参数";

        HintEffective.Theme = MyHint.Themes.Blue;
        HintEffective.Text =
            $"{id} 启动时实际使用：最大内存 {settings.MemoryMb} MB（{memorySource}）；" +
            $"{jvmPart}（{jvmSource}）。" +
            (settings.JavaPathOverride is null ? " Java 用 Lunar 自带的 JRE。" : " Java 用设置里指定的那个。");
    }

    #endregion

    #region 全局设置

    private void TextJavaPath_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        Config.Preference.Lunar.JavaPath = TextJavaPath.Text.Trim();
        RefreshJavaHint();
        RefreshEffectiveHint();
    }

    // 注意参数类型：MyIconTextButton.Click 是自定义的 ClickEventHandler，
    // 第二个参数是 ModBase.RouteEventArgs，不是 RoutedEventArgs。
    // 写成 RoutedEventArgs 编译期查不出来，打开设置页时才会抛 XamlParseException。
    private void BtnBrowseJava_Click(object sender, ModBase.RouteEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 java.exe",
            Filter = "Java 可执行文件|java.exe;java|可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            TextJavaPath.Text = dialog.FileName;
            Config.Preference.Lunar.JavaPath = dialog.FileName;
            RefreshJavaHint();
            RefreshEffectiveHint();
        }
    }

    /// <summary>
    ///     内存是「自定义」的：这里填什么，启动时就用什么。
    ///     <para>
    ///         以前这一项带一个「跟随 PCL 全局」的勾，默认勾上 —— 结果共用 <c>~/.lunarclient</c>
    ///         的用户会发现 Lunar 启动器里设的 8G 被换成了 PCL 全局的 2G/3G，看起来就像
    ///         PCL 把 Lunar 的设置覆盖掉了。现在默认直接沿用官方启动器里的数字，
    ///         想改就在这里改。
    ///     </para>
    /// </summary>
    private void TextMemory_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;

        TextMemory.Validate();
        if (!TextMemory.IsValidated) return;

        Config.Preference.Lunar.MemoryMb = _ParseMemory(TextMemory.Text, LunarLaunchProfile.DefaultMemoryMb);
        RefreshMemoryHint();
        RefreshEffectiveHint();
    }

    /// <summary>
    ///     把官方 Lunar 启动器里的分配内存抄过来，落成 PCL 的自定义值。
    /// </summary>
    private void BtnLoadLunarMemory_Click(object sender, EventArgs e)
    {
        _lunarLauncher = LunarLauncherConfig.Read(LunarLauncherConfig.ResolveRoot(Config.Preference.Lunar.Root));

        if (!_lunarLauncher.HasMemory)
        {
            // 没有可抄的东西就明确说一声，别让按钮看起来「点了没反应」
            HintMemory.Theme = MyHint.Themes.Yellow;
            HintMemory.Text = Lang.Text("Lunar.Setup.MemoryNoNative");
            return;
        }

        _suppress = true;
        try
        {
            TextMemory.Text = _lunarLauncher.AllocatedMemoryMb.ToString();
            Config.Preference.Lunar.MemoryMb = _lunarLauncher.AllocatedMemoryMb;
        }
        finally
        {
            _suppress = false;
        }

        RefreshMemoryHint();
        RefreshEffectiveHint();
    }

    private void TextJvm_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        Config.Preference.Lunar.JvmArgs = TextJvm.Text;
        RefreshEffectiveHint();
    }

    // MyIconButton.Click 的第二个参数是 EventArgs
    private void BtnResetJvm_Click(object sender, EventArgs e)
    {
        TextJvm.Text = string.Empty;
        Config.Preference.Lunar.JvmArgs = string.Empty;
        RefreshEffectiveHint();
    }

    #endregion

    #region 按版本覆盖

    /// <summary>
    ///     换版本：既改「Lunar 板块启动哪个版本」，也换下面这块覆盖设置编辑的对象。
    ///     <para>
    ///         以前这里只切覆盖项，启动版本还得去 Lunar 板块再选一次，两边不同步就会
    ///         出现「设置里明明选了 1.8.9，启动出来是别的版本」。
    ///     </para>
    /// </summary>
    private void ComboVersion_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;

        if (SelectedVersionId is { } id)
            ModLunar.SelectedSubVersion = id;

        LoadVersionOverride();
        RefreshEffectiveHint();
    }

    private void CheckVersionOverride_OnChange(object sender, bool user)
    {
        if (_suppress || !user) return;

        // 刚勾上时把「现在实际生效的那个数字」填进去：留空虽然等价于沿用全局，
        // 但一个空框看不出自己继承到了多少，用户只能再去别处找
        if (CheckVersionOverride.Checked == true && string.IsNullOrWhiteSpace(TextVersionMemory.Text))
        {
            _suppress = true;
            try
            {
                TextVersionMemory.Text = _DisplayedMemory(Config.Preference.Lunar.MemoryMb).ToString();
            }
            finally
            {
                _suppress = false;
            }
        }

        _SaveVersionOverride();
        RefreshEnabledState();
    }

    private void TextVersionMemory_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        TextVersionMemory.Validate();
        if (!TextVersionMemory.IsValidated) return;
        _SaveVersionOverride();
    }

    private void TextVersionJvm_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _SaveVersionOverride();
    }

    private void _SaveVersionOverride()
    {
        var id = SelectedVersionId;
        if (id is null) return;

        var memoryText = TextVersionMemory.Text.Trim();
        var memory = string.IsNullOrEmpty(memoryText) ? 0 : _ParseMemory(memoryText, 0);

        LunarLaunchProfile.SetOverride(id, CheckVersionOverride.Checked == true, memory, TextVersionJvm.Text.Trim());

        // 只改当前这一项的文案，重建列表会把选中项和滚动位置一起冲掉
        if (ComboVersion.SelectedItem is MyComboBoxItem item)
            item.Content = BuildVersionLabel(id);

        RefreshEffectiveHint();
    }

    #endregion

    /// <summary>
    ///     文本框里该显示哪个数字：PCL 自己配过就用配置值，否则用官方 Lunar 启动器里的，
    ///     都没有才退回内置默认。一律给具体数字，不让输入框空着被校验器判红。
    /// </summary>
    private int _DisplayedMemory(int configuredMb)
    {
        if (configuredMb > 0) return configuredMb;
        if (_lunarLauncher.HasMemory) return _lunarLauncher.AllocatedMemoryMb;
        return LunarLaunchProfile.DefaultMemoryMb;
    }

    /// <summary>
    ///     宽松解析内存输入：空串、非数字、负数都退回 <paramref name="fallback" />。
    ///     校验器已经在界面上提示了，这里只保证写进配置的值是干净的。
    /// </summary>
    private static int _ParseMemory(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        return int.TryParse(text.Trim(), out var value) && value > 0 ? value : fallback;
    }
}
