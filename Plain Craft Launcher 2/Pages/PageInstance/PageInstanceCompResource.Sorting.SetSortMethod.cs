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

    private void SetSortMethod(SortMethod target)
    {
        currentSortMethod = target;
        BtnSort.Text = Lang.Text("Instance.Resource.Sort.Text", GetSortName(target));
        // RefreshUI()
        DoSort();
    }


    private enum SortMethod
    {
        FileName,
        CompName,
        TagNums,
        CreateTime,
        ModFileSize
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
            case SortMethod.TagNums:
            {
                return Lang.Text("Instance.Resource.Sort.TagCount");
            }
            case SortMethod.CreateTime:
            {
                return Lang.Text("Instance.Resource.Sort.AddTime");
            }
            case SortMethod.ModFileSize:
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
                var invalid = items.Where(i =>
                    i.Entry is null || (currentSortMethod == SortMethod.TagNums && i.Entry.Comp is null &&
                                        !i.Entry.IsFolder)).ToList();
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
                    userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
            }
        }
    }


    private Func<ModLocalComp.LocalCompFile, ModLocalComp.LocalCompFile, int> GetSortMethod(SortMethod method)
    {
        // 通用的文件夹置顶比较函数
        int folderFirstCompare(ModLocalComp.LocalCompFile a, ModLocalComp.LocalCompFile b)
        {
            if (a.IsFolder && !b.IsFolder)
                return -1;
            if (!a.IsFolder && b.IsFolder)
                return 1;
            return 0; // 相同类型，需要进一步比较
        }

        ;

        switch (method)
        {
            case SortMethod.FileName:
            {
                return (a, b) =>
                {
                    // 文件夹始终排在最前面
                    var folderResult = folderFirstCompare(a, b);
                    if (folderResult != 0)
                        return folderResult;
                    // 如果都是文件夹或都是文件，则按文件名排序
                    return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
                };
            }
            case SortMethod.CompName:
            {
                return (a, b) =>
                {
                    // 文件夹始终排在最前面
                    var folderResult = folderFirstCompare(a, b);
                    if (folderResult != 0)
                        return folderResult;
                    // 如果都是文件夹或都是文件，则按资源名称排序
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                };
            }
            case SortMethod.TagNums:
            {
                return (a, b) =>
                {
                    // 文件夹始终排在最前面
                    var folderResult = folderFirstCompare(a, b);
                    if (folderResult != 0)
                        return folderResult;
                    // 如果都是文件夹，则按名称排序
                    if (a.IsFolder && b.IsFolder)
                        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    // 如果都是文件，则按标签数量排序（标签多的在前）
                    if (!a.IsFolder && !b.IsFolder)
                    {
                        // 安全检查，确保Comp不为空
                        var aTagCount = a.Comp?.Tags?.Count ?? 0;
                        var bTagCount = b.Comp?.Tags?.Count ?? 0;
                        return bTagCount.CompareTo(aTagCount);
                    }

                    // 理论上不会到达这里，但为了安全起见
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                };
            }
            case SortMethod.CreateTime:
            {
                return (a, b) =>
                {
                    // 文件夹始终排在最前面
                    var folderResult = folderFirstCompare(a, b);
                    if (folderResult != 0)
                        return folderResult;
                    // 如果都是文件夹或都是文件，则按创建时间排序（新的在前）
                    var aPath = a.IsFolder ? a.ActualPath : a.path;
                    var bPath = b.IsFolder ? b.ActualPath : b.path;
                    var aDate = GetModFileInfo(aPath).CreationTime;
                    var bDate = GetModFileInfo(bPath).CreationTime;
                    if (aDate == DateTime.MinValue && bDate == DateTime.MinValue)
                        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

                    if (aDate == DateTime.MinValue) return 1; // 出错的文件排在后面

                    if (bDate == DateTime.MinValue) return -1;
                    return bDate.CompareTo(aDate);
                };
            }
            case SortMethod.ModFileSize:
            {
                return (a, b) =>
                {
                    // 文件夹始终排在最前面
                    var folderResult = folderFirstCompare(a, b);
                    if (folderResult != 0)
                        return folderResult;
                    // 如果都是文件夹，则按名称排序
                    if (a.IsFolder && b.IsFolder)
                        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    // 如果都是文件，则按文件大小排序（大的在前）
                    if (!a.IsFolder && !b.IsFolder)
                    {
                        var aSize = GetModFileInfo(a.ActualPath).Length;
                        var bSize = GetModFileInfo(b.ActualPath).Length;
                        if (aSize == 0L && bSize == 0L)
                            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

                        if (aSize == 0L) return 1;

                        if (bSize == 0L) return -1;
                        return bSize.CompareTo(aSize);
                    }

                    // 理论上不会到达这里，但为了安全起见
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                };
            }

            default:
            {
                return (a, b) =>
                {
                    // 文件夹始终排在最前面
                    var folderResult = folderFirstCompare(a, b);
                    if (folderResult != 0)
                        return folderResult;
                    // 如果都是文件夹或都是文件，则按名称排序
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                };
            }
        }
    }

}
