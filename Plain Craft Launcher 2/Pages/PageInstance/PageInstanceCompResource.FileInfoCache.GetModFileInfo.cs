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

    // 获取模组信息（带缓存）
    private (DateTime CreationTime, long Length) GetModFileInfo(string path)
    {
        (DateTime CreationTime, long Length) cacheItem;
        if (modFileInfoCache.TryGetValue(path, out cacheItem)) return cacheItem;

        try
        {
            var fileInfo = new FileInfo(path);
            var newItem = (fileInfo.CreationTime, fileInfo.Length);
            if (!modFileInfoCache.ContainsKey(path)) modFileInfoCache.Add(path, newItem);
            return newItem;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "获取模组信息失败: " + path);
            return (DateTime.MinValue, 0L);
        }
    }


    // 页面关闭时清理缓存
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        modFileInfoCache.Clear();
    }

}
