using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;
using PCL.Core.App.Lunar.Social;
using PCL.Core.UI;
using PCL.Core.Utils.OS;

namespace PCL;

/// <summary>
///     Lunar Client 板块的右栏：版本与账号选择、状态、启动入口、说明与运行日志。
/// </summary>
/// <summary>账号下拉框里一项的内容：账号名 + UUID。</summary>
public sealed record AccountComboEntry(string Title, string Subtitle)
{
    // 日志和 MyComboBox.Text 会走 ToString()，别让它打印类型名
    public override string ToString() => Title;
}

public partial class PageLunarLaunch
{
    private bool _subscribed;
    private bool _suppressComboEvents;
    private string _versionSignature = "";
    private string _accountSignature = "";
    private string _moduleSignature = "";
    private string _gameDirPreview = "";
    private Brush? _brushReady;
    private Brush? _brushIdle;

    public PageLunarLaunch()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
        // 容器宽度变了（窗口缩放 / 页面重排）也要按百分比重摆填充条
        PanInstallProgress.SizeChanged += (_, _) => RefreshInstallProgress();
    }


    private IReadOnlyList<LunarFriend> _friends = [];
    private LunarFriend? _selectedFriend;
    private string _selectedConversation = "";
    private bool _socialSubscribed;
    private bool _socialBusy;

    /// <summary>
    ///     好友头像的接口。这是 Lunar 官方启动器自己用的那个（<c>skins.mcstats.com</c>），
    ///     直连可通、无皮肤时回默认皮肤，不会 404。
    /// </summary>
    private const string AvatarEndpoint = "https://skins.mcstats.com/face/";

    /// <summary>UUID → 本地缓存路径。<c>null</c> 表示下过但失败了，别再试。</summary>
    private readonly System.Collections.Generic.Dictionary<string, string?> _avatars =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     上一条消息的日期，用来决定要不要插一条日期分隔。
    ///     <para>默认值（<c>0001-01-01</c>）表示「还一条都没画」，第一条消息必定插分隔。</para>
    /// </summary>
    private DateOnly _lastMessageDate;

}
