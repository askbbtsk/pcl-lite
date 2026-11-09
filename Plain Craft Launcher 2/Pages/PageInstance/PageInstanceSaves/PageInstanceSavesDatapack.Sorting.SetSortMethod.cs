using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.UI;
using PCL.Core.UI.Theme;
using PCL.Network;
using PCL.Network.Loaders;
using FileSystem = Microsoft.VisualBasic.FileSystem;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageInstanceSavesDatapack
{

    private void SetSortMethod(SortMethod target)
    {
        currentSortMethod = target;
        BtnSort.Text = Lang.Text("Instance.Resource.Sort.Text", GetSortName(target));
        DoSort();
    }


    private enum SortMethod
    {
        FileName,
        CompName,
        CreateTime,
        DatapackFileSize
    }


    private string GetSortName(SortMethod method)
    {
        switch (method)
        {
            case SortMethod.FileName:
            {
                return Lang.Text("Instance.Resource.Sort.FileName");
            }
            case SortMethod.CompName:
            {
                return Lang.Text("Instance.Resource.Sort.ResourceName");
            }
            case SortMethod.CreateTime:
            {
                return Lang.Text("Instance.Resource.Sort.AddTime");
            }
            case SortMethod.DatapackFileSize:
            {
                return Lang.Text("Instance.Resource.Sort.FileSize");
            }

            default:
            {
                return Lang.Text("Instance.Resource.Sort.ResourceName");
            }
        }

    }


    private void BtnSortClick(object sender, ModBase.RouteEventArgs e)
    {
        var body = new ContextMenu();
        foreach (SortMethod i in Enum.GetValues(typeof(SortMethod)))
        {
            var item = new MyMenuItem();
            item.Header = GetSortName(i);
            item.Click += (_, _) => SetSortMethod(i);
            body.Items.Add(item);
        }

        body.PlacementTarget = (UIElement)sender;
        body.Placement = PlacementMode.Bottom;
        body.IsOpen = true;
    }


    private void DoSort()
    {
        lock (sortLock)
        {
            try
            {
                if (PanList is null || PanList.Children.Count < 2)
                    return;

                // 将子元素转换为可排序的列表
                var items = PanList.Children.OfType<MyLocalCompItem>().ToList();
                var method = GetSortMethod(currentSortMethod);

                // 分离有效和无效项（保持原始相对顺序）
                var invalid = items.Where(i => i.Entry is null).ToList();
                var valid = items.Except(invalid).ToList();
                // 仅对有效项进行排序
                valid.Sort((x, y) => method(x.Entry, y.Entry));
                // 合并保持无效项的原始顺序
                items = valid.Concat(invalid).ToList();

                // 批量更新UI元素
                PanList.Children.Clear();
                items.ForEach(i => PanList.Children.Add(i));
            }

            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "执行排序时出错",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
            }
        }
    }


    private Func<ModLocalComp.LocalCompFile, ModLocalComp.LocalCompFile, int> GetSortMethod(SortMethod method)
    {
        switch (method)
        {
            case SortMethod.FileName:
            {
                return (a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            }
            case SortMethod.CompName:
            {
                return (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }
            case SortMethod.CreateTime:
            {
                return (a, b) =>
                {
                    var aDate = GetDatapackFileInfo(a.path).CreationTime;
                    var bDate = GetDatapackFileInfo(b.path).CreationTime;
                    if (aDate == DateTime.MinValue && bDate == DateTime.MinValue)
                        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

                    if (aDate == DateTime.MinValue) return 1;

                    if (bDate == DateTime.MinValue) return -1;
                    return bDate.CompareTo(aDate);
                };
            }
            case SortMethod.DatapackFileSize:
            {
                return (a, b) =>
                {
                    var aSize = GetDatapackFileInfo(a.path).Length;
                    var bSize = GetDatapackFileInfo(b.path).Length;
                    if (aSize == 0L && bSize == 0L)
                        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

                    if (aSize == 0L) return 1;

                    if (bSize == 0L) return -1;
                    return bSize.CompareTo(aSize);
                };
            }

            default:
            {
                return (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

}
