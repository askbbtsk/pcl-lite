using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net.Consent;
using PCL.Core.Minecraft.Worlds;

namespace PCL;

/// <summary>
/// PCL Lite 欢迎页。
/// <para>
/// 静态文案按语言分分支存放，加载时由 <see cref="HomepageLanguageApi"/> 按当前 UI 语言过滤——
/// 与自定义主页（Custom.xaml）走的是同一套语言协议，所以欢迎页本身就是该协议的第一个内置示例。
/// 卡片标题、按钮与状态行等需要随语言即时刷新的部分沿用 DynamicResource / <see cref="Lang.Text"/>。
/// </para>
/// <para>
/// 只使用内置的 <see cref="MyCard"/> / <see cref="MyButton"/> 等控件，因此视觉与其余页面一致。
/// </para>
/// </summary>
public partial class PageWelcome
{
    private TextBlock? _networkStatus;
    private MyCard? _lastPlayedWorldCard;

    public PageWelcome()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
        // 切换语言后整体重建，让 lang: 分支重新按新语言过滤。
        // 注意：必须用静态方法组而不是 static lambda，否则 WeakLanguageChanged 会因
        // 委托带有 Target 而抛 ArgumentException（详见 WeakLanguageChanged.Add 的注释）。
        WeakLanguageChanged.Add(this, OnLanguageChanged);
        Loaded += (_, _) =>
        {
            RecentWorldHistory.Changed -= OnRecentWorldChanged;
            RecentWorldHistory.Changed += OnRecentWorldChanged;
        };
        Unloaded += (_, _) => RecentWorldHistory.Changed -= OnRecentWorldChanged;
    }

    private static void OnLanguageChanged(PageWelcome page) => ModBase.RunInUi(page.Build);
    private void OnRecentWorldChanged() => ModBase.RunInUi(() => { if (_lastPlayedWorldCard is not null) _RefreshLastPlayedWorldCard(_lastPlayedWorldCard); });

    /// <summary>
    ///     构建（或重建）欢迎页内容并应用当前语言的分支过滤。
    /// </summary>
    private void Build()
    {
        PanRoot.Children.Clear();
        PanRoot.Children.Add(_BuildHero());
        PanRoot.Children.Add(_BuildRecentCard());
        PanRoot.Children.Add(_BuildLastPlayedWorldCard());
        PanRoot.Children.Add(_BuildNetworkCard());
        PanRoot.Children.Add(_BuildCommunityCard());
        // 主页语言 API：同一父容器下只保留命中当前语言的分支
        HomepageLanguageApi.ApplyToTree(this, LocalizationService.CurrentLanguage.Code);
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

        // 标语按语言分分支，未命中当前语言时保留全部分支（不会变空白）
        var slogan = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _AddLocalizedText(slogan, 15, false,
            ("zh-CN", "极客 · 隐私 · 自主"),
            ("zh-TW", "極客 · 隱私 · 自主"),
            ("en", "Geek · Privacy · Autonomy"));
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

    /// <summary>
    ///     当前所选实例：显示已选中版本的名字与游戏版本，并提供直接启动的入口。
    ///     <para>
    ///         这里刻意使用「当前所选」而不是「上次游玩」——启动器目前没有记录最近一次成功启动的时间戳，
    ///         若把「刚选中但从未启动过」的版本称作上次游玩会产生误导。
    ///         没有选中实例时只显示一句提示，不隐藏卡片，避免主页高度来回跳动。
    ///     </para>
    /// </summary>
    private MyCard _BuildRecentCard()
    {
        var card = new MyCard
        {
            Margin = new Thickness(0, 15, 0, 0),
            Title = Lang.Text("Welcome.Recent.Title"),
            CanSwap = false
        };

        var panel = new StackPanel { Margin = new Thickness(25, 38, 25, 18) };

        McInstance instance = null;
        try
        {
            instance = ModInstanceList.McMcInstanceSelected;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Welcome] 读取上次游玩的实例失败", ModBase.LogLevel.Debug);
        }

        if (instance is null)
        {
            var empty = new TextBlock
            {
                Text = Lang.Text("Welcome.Recent.Empty"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
            panel.Children.Add(empty);
            card.Children.Add(panel);
            return card;
        }

        var name = new TextBlock
        {
            Text = instance.Name,
            FontSize = 15,
            FontWeight = FontWeights.Bold
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        panel.Children.Add(name);

        var version = new TextBlock
        {
            Text = instance.Info?.VanillaName ?? "",
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0)
        };
        version.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
        panel.Children.Add(version);

        var launch = new MyButton
        {
            Text = Lang.Text("Welcome.Recent.Launch"),
            ColorType = MyButton.ColorState.Highlight,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        launch.Click += (_, _) => ModLaunch.McLaunchStart();
        panel.Children.Add(launch);

        card.Children.Add(panel);
        return card;
    }


    private MyCard _BuildLastPlayedWorldCard()
    {
        var card = new MyCard { Margin = new Thickness(0, 15, 0, 0), Title = _RecentWorldTitle(), CanSwap = false };
        _lastPlayedWorldCard = card;
        _RefreshLastPlayedWorldCard(card);
        return card;
    }

    private static string _RecentWorldTitle() => LocalizationService.CurrentLanguage.Code switch
    {
        "zh-CN" => "最近游玩的世界",
        "zh-TW" => "最近遊玩的世界",
        _ => "Recently played world",
    };

    private static string _RecentWorldEmpty() => LocalizationService.CurrentLanguage.Code switch
    {
        "zh-CN" => "还没有记录最近游玩的世界。启动一个单人世界后，它会显示在这里。",
        "zh-TW" => "尚未記錄最近遊玩的世界。啟動單人世界後，它會顯示在這裡。",
        _ => "No recently played world yet. Launch a single-player world and it will appear here.",
    };

    private static string _RecentWorldLaunch() => LocalizationService.CurrentLanguage.Code switch
    {
        "zh-CN" => "继续游玩",
        "zh-TW" => "繼續遊玩",
        _ => "Continue playing",
    };

    private void _RefreshLastPlayedWorldCard(MyCard card)
    {
        card.Children.Clear();
        var panel = new StackPanel { Margin = new Thickness(25, 38, 25, 18) };
        RecentWorldEntry? recent = null;
        try { recent = new RecentWorldHistory().MostRecent(); }
        catch (Exception ex) { ModBase.Log(ex, "[Welcome] 读取最近世界失败", ModBase.LogLevel.Debug); }
        if (recent is null)
        {
            var empty = new TextBlock { Text = _RecentWorldEmpty(), FontSize = 13, TextWrapping = TextWrapping.Wrap };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
            panel.Children.Add(empty);
            card.Children.Add(panel);
            return;
        }
        var name = new TextBlock { Text = recent.WorldName, FontSize = 15, FontWeight = FontWeights.Bold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        panel.Children.Add(name);
        var instanceName = Path.GetFileName(recent.InstancePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var version = new TextBlock { Text = string.IsNullOrWhiteSpace(recent.MinecraftVersion) ? instanceName : $"{instanceName} · {recent.MinecraftVersion}", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        version.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
        panel.Children.Add(version);
        var launch = new MyButton { Text = _RecentWorldLaunch(), ColorType = MyButton.ColorState.Highlight, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        launch.Click += (_, _) => _LaunchRecentWorld(recent);
        panel.Children.Add(launch);
        card.Children.Add(panel);
    }

    private static void _LaunchRecentWorld(RecentWorldEntry recent)
    {
        try
        {
            var worldPath = Path.GetFullPath(recent.WorldPath);
            if (!Directory.Exists(worldPath) || !File.Exists(Path.Combine(worldPath, "level.dat")))
            {
                HintService.Hint(_RecentWorldMissing(), HintType.Error);
                return;
            }
            var instance = new McInstance(recent.InstancePath);
            instance.Load();
            if (instance.state == McInstanceState.Error || !(bool)new ModLaunch.LaunchArgument(instance).HasArguments("--quickPlaySingleplayer"))
            {
                HintService.Hint(_RecentWorldUnsupported(), HintType.Error);
                return;
            }
            ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions
            {
                instance = instance,
                WorldName = Path.GetFileName(worldPath),
                WorldPath = worldPath,
            });
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Welcome] 启动最近游玩的世界失败", ModBase.LogLevel.Hint, userSummary: _RecentWorldUnsupported());
            HintService.Hint(_RecentWorldUnsupported(), HintType.Error);
        }
    }

    private static string _RecentWorldMissing() => LocalizationService.CurrentLanguage.Code switch
    {
        "zh-CN" => "这个世界文件夹已不存在。",
        "zh-TW" => "這個世界資料夾已不存在。",
        _ => "The world folder is no longer available.",
    };

    private static string _RecentWorldUnsupported() => LocalizationService.CurrentLanguage.Code switch
    {
        "zh-CN" => "此实例无法快速进入单人世界，请从实例页面手动启动。",
        "zh-TW" => "此實例無法快速進入單人世界，請從實例頁面手動啟動。",
        _ => "This instance cannot quick-launch a single-player world. Launch it from the instance page.",
    };

    private MyCard _BuildNetworkCard()
    {
        var card = new MyCard
        {
            Margin = new Thickness(0, 15, 0, 0),
            Title = Lang.Text("Welcome.Network.Title"),
            CanSwap = false
        };

        var panel = new StackPanel { Margin = new Thickness(25, 38, 25, 18) };

        var description = new StackPanel();
        _AddLocalizedText(description, 13, false,
            ("zh-CN", "PCL Lite 不会在你不知情时联网。每一次出站连接都会先归入某个「用途」，" +
                      "由你决定是否放行；未登记的目标会被直接拦截，请求不会离开进程。"),
            ("zh-TW", "PCL Lite 不會在你不知情時連網。每一次連出連線都會先歸入某個「用途」，" +
                      "由你決定是否放行；未登記的目標會被直接攔截，請求不會離開行程。"),
            ("en", "PCL Lite never goes online behind your back. Every outbound connection is first " +
                   "classified into a purpose that you choose to allow or block; unregistered targets " +
                   "are dropped before the request ever leaves the process."));
        panel.Children.Add(description);

        _networkStatus = _Text("", 13, bold: true);
        _networkStatus.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_networkStatus);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0)
        };
        buttons.Children.Add(_Button(Lang.Text("Welcome.Network.Manage"),
            () => _Navigate(FormMain.PageType.Setup, FormMain.PageSubType.SetupLauncherMisc)));
        panel.Children.Add(buttons);

        card.Children.Add(panel);
        card.Loaded += (_, _) => RefreshStatus();
        return card;
    }

    /// <summary>
    ///     社区卡片：官方 QQ 交流群。
    ///     <para>
    ///         群号只在 <see cref="QqGroup"/> 里写一份，贡献者页复用同一处。
    ///     </para>
    /// </summary>
    private MyCard _BuildCommunityCard()
    {
        var card = new MyCard
        {
            Margin = new Thickness(0, 15, 0, 0),
            Title = Lang.Text("Welcome.Community.Title"),
            CanSwap = false
        };

        var panel = new StackPanel { Margin = new Thickness(25, 38, 25, 18) };

        var link = new MyTextButton
        {
            Text = Lang.Text("Community.Qq.Link"),
            FontSize = 13,
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        link.Click += (_, _) => QqGroup.Open();
        panel.Children.Add(link);

        card.Children.Add(panel);
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
                ? Lang.Text("Welcome.Network.Status.All", purposes.Length)
                : Lang.Text("Welcome.Network.Status.Partial", allowed, purposes.Length);
        }

    }

    #endregion

    #region 构造辅助

    /// <summary>
    ///     添加一组按语言分分支的文本，每个分支一个 <see cref="TextBlock"/>，
    ///     由 <see cref="HomepageLanguageApi"/> 在加载时保留命中当前语言的那个。
    /// </summary>
    private static void _AddLocalizedText(StackPanel panel, double size, bool bold,
        params (string Code, string Text)[] branches)
    {
        foreach (var (code, text) in branches)
            panel.Children.Add(_Text(text, size, bold, HomepageLanguageApi.LanguageTagPrefix + code));
    }

    private static TextBlock _Text(string text, double size, bool bold = false, string? tag = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
        };
        if (tag is not null) block.Tag = tag;
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
