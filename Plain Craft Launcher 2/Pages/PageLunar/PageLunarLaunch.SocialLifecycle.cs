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

public partial class PageLunarLaunch
{

    /// <summary>订阅 <see cref="ModLunar" /> 的社交事件。页面每次显示都要重新挂。</summary>
    private void SubscribeSocial()
    {
        if (_socialSubscribed) return;
        ModLunar.SocialStateChanged += RefreshSocial;
        ModLunar.SocialFriendsChanged += RefreshSocial;
        ModLunar.SocialMessageReceived += OnSocialMessage;
        _socialSubscribed = true;
        RefreshSocial();
    }


    private void UnsubscribeSocial()
    {
        if (!_socialSubscribed) return;
        ModLunar.SocialStateChanged -= RefreshSocial;
        ModLunar.SocialFriendsChanged -= RefreshSocial;
        ModLunar.SocialMessageReceived -= OnSocialMessage;
        _socialSubscribed = false;
    }


    private void RefreshSocial()
    {
        // 事件是从连接线程上抛出来的，必须切回 UI 线程再动控件
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshSocial));
            return;
        }

        if (!IsLoaded) return;

        var client = ModLunar.Social;
        var connected = ModLunar.IsSocialConnected;

        LabSocialStatus.Text = _socialBusy || ModLunar.IsSocialBusy
            ? Lang.Text("Lunar.Social.Connecting")
            : connected
                ? Lang.Text("Lunar.Social.Online")
                : Lang.Text("Lunar.Social.Offline");

        LabSocialDetail.Text = connected
            ? Lang.Text("Lunar.Social.OnlineDesc", client?.Friends.Count ?? 0)
            : Lang.Text("Lunar.Social.OfflineDesc");

        BtnSocialConnect.Text = connected
            ? Lang.Text("Lunar.Social.Disconnect")
            : Lang.Text("Lunar.Social.Connect");
        BtnSocialConnect.IsEnabled = !(_socialBusy || ModLunar.IsSocialBusy);
        BtnSocialRefresh.IsEnabled = connected;

        _friends = client?.Friends ?? [];
        RebuildFriendRows();
    }


    private void RebuildFriendRows()
    {
        PanFriends.Children.Clear();
        LabFriendsEmpty.Visibility = _friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var friend in _friends.OrderByDescending(f => f.IsOnline).ThenBy(f => f.Name))
        {
            var detail = friend.IsOnline
                ? string.IsNullOrEmpty(friend.Where)
                    ? Lang.Text("Lunar.Social.WhereOnline")
                    : friend.Where
                : Lang.Text("Lunar.Social.WhereOffline");

            var item = new MyListItem
            {
                Title = friend.Name,
                Info = detail,
                Type = MyListItem.CheckType.RadioBox,
                Height = 38,
                Margin = new Thickness(0, 0, 0, 2),
                Tag = friend,
                LogoCornerRadius = new CornerRadius(5),
                Checked = _selectedFriend?.Uuid == friend.Uuid
            };
            ApplyAvatar(item, friend);
            item.Check += FriendItem_Check;
            PanFriends.Children.Add(item);
        }
    }


    /// <summary>
    ///     给一行塞头像：有缓存就直出，没有就先挂 SVG 图标并排队下载。
    ///     <para>
    ///         头像走 <see cref="MyImage" /> 那套缓存（<c>Cache/Images/&lt;md5&gt;.png</c>），
    ///         所以第二次进页面是本地读，不会每次都打网络。
    ///     </para>
    /// </summary>
    private void ApplyAvatar(MyListItem item, LunarFriend friend)
    {
        var path = ResolveAvatar(friend);
        if (path is null)
        {
            item.SvgIcon = friend.IsOnline ? "lucide/user-round" : "lucide/user";
            item.Logo = string.Empty;
            return;
        }

        item.SvgIcon = string.Empty;
        item.Logo = path;
    }


    /// <summary>拿本地头像路径；没有就返回 <see langword="null" /> 并顺手排一个下载。</summary>
    private string? ResolveAvatar(LunarFriend friend)
    {
        if (friend.Uuid.Length == 0) return null;
        if (_avatars.TryGetValue(friend.Uuid, out var known)) return known;

        var url = AvatarUrl(friend);
        var path = MyImage.GetTempPath(url);
        if (File.Exists(path) && new FileInfo(path).Length > 0L)
        {
            _avatars[friend.Uuid] = path;
            return path;
        }

        // 先记成「没有」，免得同一轮重建里反复排队
        _avatars[friend.Uuid] = null;
        _ = DownloadAvatarAsync(friend.Uuid, url);
        return null;
    }


    private async Task DownloadAvatarAsync(string uuid, string url)
    {
        string path;
        try
        {
            path = await MyImage.DownloadImageAsync(url).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, $"[Lunar] 下载好友头像失败（{uuid}）", ModBase.LogLevel.Debug);
            path = string.Empty;
        }

        if (string.IsNullOrEmpty(path))
        {
            // 失败就把「没有」这条记录清掉，下次刷新还能再试一次；
            // 留着的话会一直显示图标，直到重启 PCL
            await Dispatcher.BeginInvoke(new Action(() => _avatars.Remove(uuid)));
            return;
        }

        await Dispatcher.BeginInvoke(new Action(() =>
        {
            _avatars[uuid] = path;

            // 只换那一行的图，不整表重建 —— 重建会把用户正在点的选中态一起刷掉
            foreach (var row in PanFriends.Children.OfType<MyListItem>())
            {
                if (row.Tag is not LunarFriend friend || friend.Uuid != uuid) continue;
                row.SvgIcon = string.Empty;
                row.Logo = path;
                break;
            }
        }));
    }


    private static string AvatarUrl(LunarFriend friend) =>
        AvatarEndpoint + friend.Uuid.Replace("-", "", StringComparison.Ordinal) + "?size=64";


    private async void FriendItem_Check(object sender, ModBase.RouteEventArgs e)
    {
        if (sender is not MyListItem { Tag: LunarFriend friend }) return;
        await OpenChatAsync(friend);
    }


    private async Task OpenChatAsync(LunarFriend friend)
    {
        _selectedFriend = friend;
        _selectedConversation = "";
        PanChat.Visibility = Visibility.Visible;
        LabChatTitle.Text = friend.Name;
        PanMessages.Children.Clear();
        _lastMessageDate = default;

        var client = ModLunar.Social;
        if (client is null || !client.IsConnected)
        {
            AddSystemLine(Lang.Text("Lunar.Social.NeedConnect"));
            return;
        }

        AddSystemLine(Lang.Text("Lunar.Social.Loading"));

        try
        {
            _selectedConversation = await client.OpenConversationAsync(friend.Uuid);
            var messages = await client.LoadMessagesAsync(friend.Uuid, _selectedConversation);
            PanMessages.Children.Clear();
            _lastMessageDate = default;
            foreach (var message in messages) AddMessageRow(message);
            if (messages.Count == 0) AddSystemLine(Lang.Text("Lunar.Social.NoMessages"));
            PanChatScroll.ScrollToEnd();
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException)
        {
            PanMessages.Children.Clear();
            _lastMessageDate = default;
            AddSystemLine(ex.Message);
        }
    }


    private void OnSocialMessage(string conversationId, LunarChatMessage message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSocialMessage(conversationId, message)));
            return;
        }

        if (conversationId != _selectedConversation) return;
        AddMessageRow(message);
        PanChatScroll.ScrollToEnd();
    }


    private void AddSystemLine(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Opacity = 0.55,
            Margin = new Thickness(2, 2, 2, 6)
        };
        PanMessages.Children.Add(block);
    }


    /// <summary>
    ///     一行消息。自己发的靠右、用主题色底 + 反白字；别人的靠左、用透明底。
    ///     <para>文字颜色都显式给，深色主题下才不会出现黑字黑底。</para>
    ///     <para>
    ///         跨天的第一条消息前面插一条日期分隔。以前只显示 <c>HH:mm</c>，
    ///         昨天的消息和上个月的消息看起来一模一样，完全分不出是哪天说的。
    ///     </para>
    /// </summary>
    private void AddMessageRow(LunarChatMessage message)
    {
        var local = message.SentAt.ToLocalTime();
        var date = DateOnly.FromDateTime(local.DateTime);

        if (date != _lastMessageDate)
        {
            AddDateSeparator(date);
            _lastMessageDate = date;
        }

        // 当天只留时分，别的日子把日期带上 —— 隔天往上翻的时候不用再找分隔条
        var today = DateOnly.FromDateTime(DateTime.Now);
        var stamp = date == today
            ? local.ToString("HH:mm")
            : date.Year == today.Year
                ? local.ToString("M/d HH:mm")
                : local.ToString("yyyy/M/d HH:mm");

        var caption = message.Mine
            ? Lang.Text("Lunar.Social.Me") + " · " + stamp
            : message.SenderName + " · " + stamp;

        var stack = new StackPanel();
        var captionBlock = new TextBlock
        {
            Text = caption,
            FontSize = 10.5,
            Opacity = 0.55,
            Margin = new Thickness(2, 0, 2, 2),
            HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        captionBlock.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        stack.Children.Add(captionBlock);

        var text = new TextBlock
        {
            Text = message.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, message.Mine ? "ColorBrush8" : "ColorBrush1");

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 6, 10, 6),
            MaxWidth = 360,
            HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = text
        };
        bubble.SetResourceReference(Border.BackgroundProperty,
            message.Mine ? "ColorBrush3" : "ColorBrushTransparentBackground");

        // 对方的气泡原来是「透明底 + 透明面板」，等于没有气泡。改成描一圈细边，
        // 深浅主题下都看得见，也不用再赌一个底色跟面板撞不撞。
        if (!message.Mine)
        {
            bubble.BorderThickness = new Thickness(1);
            bubble.SetResourceReference(Border.BorderBrushProperty, "ColorBrush5");
        }

        stack.Children.Add(bubble);

        stack.Margin = new Thickness(0, 0, 0, 8);
        PanMessages.Children.Add(stack);
    }


    /// <summary>
    ///     居中一条日期分隔，形如「2026/10/31」。
    ///     <para>用 <c>yyyy/M/d</c> 而不是本地化长日期：它在四种语言下都读得懂，也省一个翻译键。</para>
    /// </summary>
    private void AddDateSeparator(DateOnly date)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lineLeft = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        lineLeft.SetResourceReference(Border.BackgroundProperty, "ColorBrush5");
        Grid.SetColumn(lineLeft, 0);

        var label = new TextBlock
        {
            Text = date.ToString("yyyy/M/d"),
            FontSize = 10.5,
            Opacity = 0.45
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        Grid.SetColumn(label, 1);

        var lineRight = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        lineRight.SetResourceReference(Border.BackgroundProperty, "ColorBrush5");
        Grid.SetColumn(lineRight, 2);

        grid.Children.Add(lineLeft);
        grid.Children.Add(label);
        grid.Children.Add(lineRight);
        PanMessages.Children.Add(grid);
    }


    private async void BtnSocialConnect_Click(object sender, MouseButtonEventArgs e)
    {
        if (_socialBusy) return;

        if (ModLunar.IsSocialConnected)
        {
            _socialBusy = true;
            RefreshSocial();
            try
            {
                await ModLunar.DisconnectSocialAsync();
            }
            finally
            {
                _socialBusy = false;
                PanChat.Visibility = Visibility.Collapsed;
                RefreshSocial();
            }

            return;
        }

        _socialBusy = true;
        RefreshSocial();
        try
        {
            await ModLunar.ConnectSocialAsync();
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException)
        {
            HintService.Hint(ex.Message, HintType.Warning);
        }
        finally
        {
            _socialBusy = false;
            RefreshSocial();
        }
    }


    private async void BtnSocialRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        var client = ModLunar.Social;
        if (client is null || !client.IsConnected) return;

        BtnSocialRefresh.IsEnabled = false;
        try
        {
            await client.LoadFriendsAsync();
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException)
        {
            HintService.Hint(ex.Message, HintType.Warning);
        }
        finally
        {
            BtnSocialRefresh.IsEnabled = true;
            RefreshSocial();
        }
    }


    private async void BtnChatSend_Click(object sender, MouseButtonEventArgs e) => await SendChatAsync();


    private async void TxtChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SendChatAsync();
    }


    private async Task SendChatAsync()
    {
        var text = TxtChatInput.Text.Trim();
        if (text.Length == 0) return;
        if (_selectedFriend is null || _selectedConversation.Length == 0) return;

        var client = ModLunar.Social;
        if (client is null || !client.IsConnected)
        {
            HintService.Hint(Lang.Text("Lunar.Social.NeedConnect"), HintType.Warning);
            return;
        }

        TxtChatInput.Text = "";
        BtnChatSend.IsEnabled = false;
        try
        {
            await client.SendMessageAsync(_selectedFriend.Uuid, _selectedConversation, text);
        }
        catch (Exception ex) when (ex is LunarSocialException or OperationCanceledException)
        {
            // 发失败就把内容放回去，别让用户白打一遍
            TxtChatInput.Text = text;
            HintService.Hint(ex.Message, HintType.Warning);
        }
        finally
        {
            BtnChatSend.IsEnabled = true;
        }
    }

}
