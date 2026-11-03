using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>
///     Lunar Client 板块的左栏。
///     <para>
///         骨架与 <see cref="PageBedrockLeft" /> 一致：状态卡片 + 左下角启动按钮，
///         数据来自 <see cref="ModLunar" />。目前只有一个子页面，所以不需要真正切换。
///     </para>
///     <para>
///         左下角按钮在游戏运行时会变成「强制停止」——同一个位置、同一个控件，
///         只换文案和动作，免得用户在两颗按钮之间找。
///     </para>
/// </summary>
public partial class PageLunarLeft
{
    private bool _isLoad;
    private Brush? _brushReady;
    private Brush? _brushIdle;

    public PageLunarLeft()
    {
        InitializeComponent();
        AnimatedControl = PanItem;
        Loaded += PageLunarLeft_Loaded;
        Unloaded += PageLunarLeft_Unloaded;
    }

    private void PageLunarLeft_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoad) return;
        _isLoad = true;
        ItemLaunch.SetChecked(true, false, false);

        ModLunar.StateChanged += RefreshStatus;
        RefreshStatus();
        ModLunar.Refresh();

        // 一进 Lunar 板块就开始预热令牌：聊天和启动共用同一个 accessToken，
        // 等用户点「连接」或「启动」再刷，看到的就是好友列表空着 / 连上就掉线
        _ = ModLunar.PrepareSessionAsync();
    }

    private void PageLunarLeft_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoad = false;
        ModLunar.StateChanged -= RefreshStatus;
    }

    private void RefreshStatus()
    {
        if (!IsLoaded) return;

        var running = ModLunar.IsRunning;

        LabStatus.Text = ModLunar.StatusText;
        LabDetail.Text = ModLunar.DetailText;

        var ready = ModLunar.IsInstalled;
        _brushReady ??= (Brush)FindResource("ColorBrush3");
        _brushIdle ??= (Brush)FindResource("ColorBrushGray4");
        ShapeStatusDot.Fill = ready ? _brushReady : _brushIdle;

        LabVersionText.Text = ready ? ModLunar.SelectedSubVersion : "";

        // 账号：先看本页登录的「Lunar 身份账号」，再看 Lunar 账户库里的激活账号。
        // 身份账号必须排在前面 —— 它选中时 SelectedAccount 会故意返回 null
        // （那条路不再用 PCL 主页面登录的令牌），漏了这一步左栏就会一直写着「未找到」
        var identity = ModLunar.SelectedIdentityAccount;
        var account = ModLunar.SelectedAccount;
        if (identity is not null)
        {
            LabAccountName.Text = identity.DisplayName;
            // 令牌导入的账号没有 refreshToken，说「启动前会刷新」是骗人
            LabAccountHint.Text = identity.IsExpired
                ? Lang.Text(identity.CanRefresh
                    ? "Lunar.Left.IdentityExpiredHint"
                    : "Lunar.Left.IdentityExpiredNoRefresh")
                : Lang.Text("Lunar.Left.IdentityHint", FormatUuid(identity.Uuid));
        }
        else if (account is not null)
        {
            LabAccountName.Text = account.IsExpired
                ? Lang.Text("Lunar.Account.NameExpired", account.DisplayName)
                : account.DisplayName;
            LabAccountHint.Text = account.IsExpired
                ? Lang.Text("Lunar.Left.AccountExpiredHint")
                : Lang.Text("Lunar.Left.AccountHint", account.Type);
        }
        else
        {
            LabAccountName.Text = Lang.Text("Lunar.Left.NoAccount");
            LabAccountHint.Text = "";
        }

        // 没有说明时把那一行收起来，别在卡片底部留一条空白
        LabAccountHint.Visibility = string.IsNullOrEmpty(LabAccountHint.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        RefreshButton(running);
    }

    /// <summary>把 32 位裸 UUID 补成带横线的形式，方便和别处看到的一一对应。</summary>
    private static string FormatUuid(string? uuid)
    {
        var raw = (uuid ?? "").Replace("-", string.Empty);
        return raw.Length == 32
            ? $"{raw[..8]}-{raw[8..12]}-{raw[12..16]}-{raw[16..20]}-{raw[20..]}"
            : uuid ?? "";
    }

    private void RefreshButton(bool running)
    {
        BtnLaunch.Text = running ? Lang.Text("Lunar.Action.Stop") : Lang.Text("Lunar.Launch");
        BtnLaunch.IsEnabled = running || !ModLunar.IsBusy;
        BtnLaunch.ColorType = running ? MyButton.ColorState.Red : MyButton.ColorState.Highlight;
    }

    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        // 运行中 → 强制停止；否则走启动
        if (ModLunar.IsRunning)
        {
            ModLunar.Stop();
            RefreshButton(false);
            return;
        }

        if (ModLunar.IsBusy) return;

        // 这里以前挡着「没检测到 Lunar Client，请先安装并至少启动一次」。
        // 物料早就由 PCL 自己下了（LunarInstaller.EnsureInstalledAsync），
        // 装没装过 Lunar 启动器和能不能启动无关，拦在这里只会让人永远进不去。

        // 游戏目录由 ModLunar 自己决定（MC 根目录，不跟实例隔离走），这里不再传
        ModLunar.Launch();
    }

    #region 页面切换

    public FormMain.PageSubType pageID = FormMain.PageSubType.LunarLaunch;

    private void PageCheck(object senderRaw, ModBase.RouteEventArgs e)
    {
        var sender = (MyListItem)senderRaw;
        if (sender.Tag is not null)
            PageChange((FormMain.PageSubType)ModBase.Val(sender.Tag));
    }

    public object PageGet(FormMain.PageSubType? id = null)
    {
        ModMain.frmLunarLaunch ??= new PageLunarLaunch();
        return ModMain.frmLunarLaunch;
    }

    public void PageChange(FormMain.PageSubType id)
    {
        if (pageID == id) return;
        ModAnimation.AniControlEnabled += 1;
        try
        {
            PageChangeRun((MyPageRight)PageGet(id));
            pageID = id;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"切换 Lunar 分页面失败（ID {(int)id}）", ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Lunar.Error.OperationFailed"));
        }
        finally
        {
            ModAnimation.AniControlEnabled -= 1;
        }
    }

    private static void PageChangeRun(MyPageRight target)
    {
        // 主页面已经不是 Lunar 了（比如游戏内点「添加账号」把界面切走了），
        // 这次分页切换就已经过期：再写 PanMainRight.Child 会把右栏拽回 Lunar 页，
        // 和左栏对不上，用户看到的就是两栏撕裂。
        if (ModMain.frmMain.pageCurrent.page != FormMain.PageType.Lunar) return;

        ModAnimation.AniStop("FrmMain PageChangeRight");
        if (target.Parent is not null)
            target.SetValue(System.Windows.Controls.ContentPresenter.ContentProperty, null);
        ModMain.frmMain.pageRight = target;
        ((MyPageRight)ModMain.frmMain.PanMainRight.Child).PageOnExit();
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() =>
            {
                ((MyPageRight)ModMain.frmMain.PanMainRight.Child).PageOnForceExit();
                ModMain.frmMain.PanMainRight.Child = ModMain.frmMain.pageRight;
                ModMain.frmMain.pageRight.Opacity = 0d;
            }, 130),
            ModAnimation.AaCode(() =>
            {
                ModMain.frmMain.pageRight.Opacity = 1d;
                ModMain.frmMain.pageRight.PageOnEnter();
            }, 30, true)
        }, "PageLeft PageChange");
    }

    #endregion
}
