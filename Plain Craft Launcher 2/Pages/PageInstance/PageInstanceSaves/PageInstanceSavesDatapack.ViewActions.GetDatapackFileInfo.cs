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

    // 获取数据包信息（带缓存）
    private (DateTime CreationTime, long Length) GetDatapackFileInfo(string path)
    {
        (DateTime CreationTime, long Length) cacheItem;
        if (datapackFileInfoCache.TryGetValue(path, out cacheItem)) return cacheItem;

        try
        {
            var fileInfo = new FileInfo(path);
            var newItem = (fileInfo.CreationTime, fileInfo.Length);
            if (!datapackFileInfoCache.ContainsKey(path)) datapackFileInfoCache.Add(path, newItem);
            return newItem;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "获取数据包信息失败: " + path);
            return (DateTime.MinValue, 0L);
        }
    }


    // 页面关闭时清理缓存
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        datapackFileInfoCache.Clear();
    }

}
