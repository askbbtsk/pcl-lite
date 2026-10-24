using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Consent;

namespace PCL;

/// <summary>
/// PCL Lite 欢迎页。
/// <para>
/// 完全由 C# 构造控件树，不解析任何远程 XAML —— 这正是本项目要消除的机制。
/// 只使用内置的 <see cref="MyCard"/> / <see cref="MyButton"/> 等控件，因此视觉与其余页面一致。
/// </para>
/// </summary>
public partial class PageWelcome
{
    private bool _built;
    private TextBlock? _networkStatus;
    private TextBlock? _privacyStatus;

    public PageWelcome()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        if (_built) return;
        _built = true;

        PanRoot.Children.Add(_BuildHero());
        PanRoot.Children.Add(_BuildNetworkCard());
        PanRoot.Children.Add(_BuildPrivacyCard());
    }

    #region 卡片

    private MyCard _BuildHero()
    {
        var card = new MyCard { Margin = new Thickness(0, 15, 0, 0), CanSwap = false };

        var panel = new StackPanel { Margin = new Thickness(25, 38, 25, 22) };

        // 「PCL」常规 + 「Lite」加粗高亮，作为品牌强调
        var titleLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleMain = new TextBlock
        {
            Text = "PCL",
            FontSize = 30,
            FontWeight = FontWeights.Light
        };
        titleMain.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        titleLine.Children.Add(titleMain);

        var titleLite = new TextBlock
        {
            Text = "Lite",
            FontSize = 30,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(8, 0, 0, 0)
        };
        titleLite.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush3");
        titleLine.Children.Add(titleLite);
        panel.Children.Add(titleLine);

        var slogan = new TextBlock
        {
            Text = "极客 · 隐私 · 自主",
            FontSize = 15,
            Margin = new Thickness(0, 8, 0, 0)
        };
        slogan.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
        panel.Children.Add(slogan);

        var version = new TextBlock
        {
            Text = $"v{Basics.VersionName}  ·  {Basics.VersionBranch}",
            FontSize = 12,
            Margin = new Thickness(0, 14, 0, 0)
        };
        version.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
        panel.Children.Add(version);

        card.Children.Add(panel);
        return card;
    }

    private MyCard _BuildNetworkCard()
    {
        var card = new MyCard { Margin = new Thickness(0, 15, 0, 0), Title = "网络自主", CanSwap = false };

        var panel = new StackPanel { Margin = new Thickness(25, 38, 25, 18) };

        panel.Children.Add(_Text(
            "PCL Lite 不会在你不知情时联网。每一次出站连接都会先归入某个「用途」，" +
            "由你决定是否放行；未登记的目标会被直接拦截，请求不会离开进程。",
            13));

        _networkStatus = _Text("", 13, bold: true);
        _networkStatus.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_networkStatus);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(_Button("管理网络白名单",
            () => _Navigate(FormMain.PageType.Setup, FormMain.PageSubType.SetupLauncherMisc)));
        panel.Children.Add(buttons);

        card.Children.Add(panel);
        card.Loaded += (_, _) => RefreshStatus();
        return card;
    }

    private MyCard _BuildPrivacyCard()
    {
        var card = new MyCard { Margin = new Thickness(0, 15, 0, 0), Title = "隐私与离线", CanSwap = false };

        var panel = new StackPanel { Margin = new Thickness(25, 38, 25, 18) };

        panel.Children.Add(_Text(
            "没有任何遥测与崩溃上报，不收集任何使用数据；崩溃分析全程在本地完成。" +
            "已安装的版本在断网状态下依然可以启动。",
            13));

        _privacyStatus = _Text("", 13, bold: true);
        _privacyStatus.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_privacyStatus);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(_Button("打开下载", () => _Navigate(FormMain.PageType.Download)));
        panel.Children.Add(buttons);

        card.Children.Add(panel);
        card.Loaded += (_, _) => RefreshStatus();
        return card;
    }

    #endregion

    #region 状态

    private void RefreshStatus()
    {
        var purposes = NetworkPurposeCatalog.All
            .Where(x => x.Purpose != NetworkPurpose.Unknown)
            .ToArray();
        var allowed = purposes.Count(x => NetworkConsentService.IsAllowed(x.Purpose));

        if (_networkStatus is not null)
        {
            _networkStatus.Text = NetworkConsentService.AllowAll
                ? $"已授权全部连接（{purposes.Length} 项网络用途）"
                : $"已授权 {allowed} / {purposes.Length} 个网络用途";
        }

        if (_privacyStatus is not null)
        {
            var privacy = Config.System.PrivacyMode ? "已开启" : "未开启";
            _privacyStatus.Text = $"隐私模式：{privacy}　·　遥测：已移除";
        }
    }

    #endregion

    #region 构造辅助

    private static TextBlock _Text(string text, double size, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, bold ? "ColorBrush1" : "ColorBrushGray2");
        return block;
    }

    private static MyButton _Button(string text, Action onClick)
    {
        var button = new MyButton
        {
            Text = text,
            Height = 32,
            MinWidth = 116,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(14, 0, 14, 0),
            ColorType = MyButton.ColorState.Highlight
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void _Navigate(FormMain.PageType page, FormMain.PageSubType subType = FormMain.PageSubType.Default)
    {
        if (ModMain.frmMain is null) return;
        ModMain.frmMain.PageChange(new FormMain.PageStackData { page = page }, subType);
    }

    #endregion
}
