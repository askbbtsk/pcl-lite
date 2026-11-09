using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.UI;
using PCL.Core.UI.Theme;
using PCL.Network;
using PCL.Network.Loaders;
using FileSystem = Microsoft.VisualBasic.FileSystem;
using SearchOption = System.IO.SearchOption;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceCompResource
{

    // 单项切换选择状态
    public void CheckChanged(MyLocalCompItem sender, ModBase.RouteEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;
        // 更新选择了的内容
        var selectedKey = sender.Entry.RawPath;
        if (sender.Checked)
            selectedMods.Add(selectedKey);
        else
            selectedMods.Remove(selectedKey);
        RefreshBars();
    }


    // 切换所有项的选择状态
    private void ChangeAllSelected(bool value)
    {
        ModAnimation.AniControlEnabled += 1;
        selectedMods.Clear();
        foreach (var Item in modItems.Values)
        {
            // #4992，Mod 从过滤器看可能不应在列表中，但因为刚切换状态所以依然保留在列表中，所以应该从列表 UI 判断，而非从过滤器判断
            var shouldSelected = value && PanList.Children.Contains(Item);
            Item.Checked = shouldSelected;
            if (shouldSelected)
                selectedMods.Add(Item.Entry.RawPath);
        }

        ModAnimation.AniControlEnabled -= 1;
        RefreshBars();
    }


    private void UnselectedAllWithAnimation()
    {
        var cacheAniControlEnabled = ModAnimation.AniControlEnabled;
        ModAnimation.AniControlEnabled = 0;
        ChangeAllSelected(false);
        ModAnimation.AniControlEnabled += cacheAniControlEnabled;
    }


    private void FrmMain_KeyDown(object sender, KeyEventArgs e) // 若监听自己的事件则在进入页面后需点击右侧控件才可监听到 (#4311)
    {
        if (!ReferenceEquals(ModMain.frmMain.pageRight, this))
            return;
        if ((Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) && e.Key == Key.A)
            ChangeAllSelected(true);
    }


    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl + A 会被搜索框捕获，导致无法全选，所以在按下 Ctrl + A 时转移焦点以便捕获
        if (SearchBox.Text.Any())
            return;
        if ((Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) && e.Key == Key.A)
            PanBack.Focus();
    }

}
