using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
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

            var followGlobal = lunar.MemoryMb <= 0;
            CheckFollowGlobal.Checked = followGlobal;
            TextMemory.Text = (followGlobal ? _CurrentPclGlobalMemoryMb() : lunar.MemoryMb).ToString();

            RefreshVersionCombo();
        }
        finally
        {
            _suppress = false;
        }

        RefreshJavaHint();
        RefreshEnabledState();
        LoadVersionOverride();
        RefreshEffectiveHint();

        // 版本列表可能是空的（缓存没写过、也没联网拉过）
        _EnsureVersionsAsync();
    }

    private void RefreshVersionCombo()
    {
        var wanted = ModLunar.SelectedSubVersion;

        ComboVersion.Items.Clear();
        foreach (var version in ModLunar.Versions)
        {
            var item = new MyComboBoxItem
            {
                Content = BuildVersionLabel(version.Id),
                Tag = version.Id
            };
            ComboVersion.Items.Add(item);
            if (string.Equals(version.Id, wanted, StringComparison.OrdinalIgnoreCase))
                ComboVersion.SelectedItem = item;
        }

        if (ComboVersion.Items.Count == 0)
        {
            ComboVersion.Items.Add(new MyComboBoxItem
            {
                Content = "（还没拿到版本列表，正在尝试获取…）",
                IsEnabled = false,
                Tag = null
            });
            ComboVersion.SelectedIndex = 0;
        }
        else if (ComboVersion.SelectedItem is null)
        {
            ComboVersion.SelectedIndex = 0;
        }
    }

    private static string BuildVersionLabel(string versionId) =>
        LunarLaunchProfile.GetOverride(versionId).Enabled ? versionId + "（已覆盖）" : versionId;

    private async void _EnsureVersionsAsync()
    {
        if (ModLunar.Versions.Count > 0 || !ModLunar.IsInstalled) return;

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
        if (TextMemory is not null)
            TextMemory.IsEnabled = CheckFollowGlobal.Checked != true;

        if (PanVersionOptions is not null)
            PanVersionOptions.IsEnabled = CheckVersionOverride.Checked == true;
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

        var settings = LunarLaunchProfile.Resolve(id, ModLunar.ResolvePclGlobalMemoryMb());
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

    private void CheckFollowGlobal_OnChange(object sender, bool user)
    {
        if (_suppress || !user) return;

        _suppress = true;
        try
        {
            if (CheckFollowGlobal.Checked == true)
            {
                // 跟随：配置里存 0，显示当前换算出来的值
                Config.Preference.Lunar.MemoryMb = 0;
                TextMemory.Text = _CurrentPclGlobalMemoryMb().ToString();
            }
            else
            {
                // 取消跟随：把眼下显示的数字落成独立设置，
                // 否则用户会看到「勾去掉了但值还是那个值」，以为没生效
                var memory = _ParseMemory(TextMemory.Text, LunarLaunchProfile.DefaultMemoryMb);
                Config.Preference.Lunar.MemoryMb = memory;
                TextMemory.Text = memory.ToString();
            }
        }
        finally
        {
            _suppress = false;
        }

        RefreshEnabledState();
        RefreshEffectiveHint();
    }

    private void TextMemory_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;

        TextMemory.Validate();
        if (!TextMemory.IsValidated) return;
        if (CheckFollowGlobal.Checked == true) return;

        Config.Preference.Lunar.MemoryMb = _ParseMemory(TextMemory.Text, LunarLaunchProfile.DefaultMemoryMb);
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

    private void ComboVersion_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        LoadVersionOverride();
        RefreshEffectiveHint();
    }

    private void CheckVersionOverride_OnChange(object sender, bool user)
    {
        if (_suppress || !user) return;
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
    ///     PCL 全局内存换算出来的 MB 值；算不出来就退回内置默认，
    ///     免得文本框里出现 0 然后被校验器判红。
    /// </summary>
    private static int _CurrentPclGlobalMemoryMb()
    {
        var value = ModLunar.ResolvePclGlobalMemoryMb();
        return value > 0 ? value : LunarLaunchProfile.DefaultMemoryMb;
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
