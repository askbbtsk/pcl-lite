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

    public enum FilterType
    {
        All = 0,
        Enabled = 1,
        Disabled = 2,
        CanUpdate = 3,
        Unavailable = 4,
        Duplicate = 5
    }


    /// <summary>
    ///     检查该 Mod 项是否符合当前筛选的类别。
    /// </summary>
    private bool CanPassFilter(ModLocalComp.LocalCompFile checkingMod)
    {
        switch (Filter)
        {
            case FilterType.All:
            {
                return true;
            }
            case FilterType.Enabled:
            {
                return checkingMod.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine;
            }
            case FilterType.Disabled:
            {
                return checkingMod.State == ModLocalComp.LocalCompFile.LocalFileStatus.Disabled;
            }
            case FilterType.CanUpdate:
            {
                return checkingMod.CanUpdate;
            }
            case FilterType.Unavailable:
            {
                return checkingMod.State == ModLocalComp.LocalCompFile.LocalFileStatus.Unavailable;
            }
            case FilterType.Duplicate:
            {
                var itemSource = IsSearching
                    ? searchResult
                    : ModLocalComp.compResourceListLoader.output ?? new List<ModLocalComp.LocalCompFile>();
                return itemSource is not null && itemSource.Where(m =>
                    checkingMod.Comp is not null && m.Comp is not null &&
                    (checkingMod.Comp.Id ?? "") == (m.Comp.Id ?? "")).Skip(1).Any();
            }

            default:
            {
                return false;
            }
        }
    }


    // 点击筛选项触发的改变
    private void ChangeFilter(MyRadioButton sender, bool raiseByMouse)
    {
        Filter = (FilterType)Convert.ToInt32(sender.Tag);
        RefreshUI();
        DoSort();
    }

}
