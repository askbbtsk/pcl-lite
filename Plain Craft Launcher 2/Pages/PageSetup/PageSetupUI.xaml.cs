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
    public bool isLoaded;

    public PageSetupUI()
    {
        InitializeComponent();
        Loaded += InitializeThemePackages;
        CheckHiddenPageSetup.Change += HiddenSetupMain;
        foreach (var check in new[] { CheckHiddenSetupLaunch, CheckHiddenSetupUI,
                     CheckHiddenSetupLauncherLanguage, CheckHiddenSetupGameManage, CheckHiddenLauncherMisc,
                     CheckHiddenSetupJava, CheckHiddenSetupUpdate, CheckHiddenSetupAbout,
                     CheckHiddenSetupFeedback, CheckHiddenSetupLog })
            check.Change += HiddenSetupSub;
        CheckHiddenPageTools.Change += HiddenToolsMain;
        CheckHiddenToolsTest.Change += HiddenToolsSub;
        foreach (var check in new[] { CheckHiddenPageDownload, CheckHiddenPageBedrock, CheckHiddenPageLunar,
                     CheckHiddenPagePlugins, CheckHiddenPageSetup, CheckHiddenPageTools,
                     CheckHiddenFunctionSelect, CheckHiddenFunctionModUpdate, CheckHiddenFunctionHidden })
            check.Change += HiddenHint;
        Loaded += PageSetupUI_Loaded;
        Loaded += (_, _) => HiddenRefresh();
    }

    // 主题

    /// <summary>
    ///     窗口大小预设。顺序必须与 XAML 里 <c>ComboWindowSize</c> 的选项顺序一一对应，
    ///     否则 <see cref="RefreshWindowSize" /> 按尺寸反推出来的下标会选中错误的条目
    ///     （最后一项「自定义」不在表里）。
    ///     <para>
    ///         第一项是 PCL 原版的尺寸：上游 <c>FormMain.xaml</c> 里写的就是
    ///         <c>Width="850" Height="500"</c>，从最早的公开代码起就没变过。
    ///         从 PCL2 迁过来的用户按老习惯挑这一项就行。
    ///     </para>
    /// </summary>
    private static readonly (double Width, double Height)[] WindowSizePresets =
    [
        (850, 500),
        (820, 520),
        (900, 550),
        (1080, 650),
        (1280, 760)
    ];

    // 与 FormMain.xaml 的 MinWidth / MinHeight 对齐：比这更小的尺寸窗口自己会夹回去，
    // 预设写成 800 只会变成「点了没反应」
    private const double MinWindowWidth = 810;
    private const double MinWindowHeight = 470;



    private bool _customThemeBinding;

    private readonly List<MyRadioBox> _themeRadios = [];

}
