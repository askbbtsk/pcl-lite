using Microsoft.VisualBasic.CompilerServices;
using PCL.Core.App;
using PCL.Core.Utils;
using System.Collections;
using System.IO;
using System.Windows.Shell;
using PCL.Network;
using PCL.Network.Loaders;

using PCL.Core.App.Localization;
namespace PCL;

public static partial class ModLoader
{

    // 任务栏进度条
    public static ModBase.SafeList<LoaderBase> loaderTaskbar = new();
    public static double loaderTaskbarProgress; // 平滑后的进度
    private static TaskbarItemProgressState loaderTaskbarProgressLast = TaskbarItemProgressState.None;

    // 文件夹刷新类委托
    private static readonly Dictionary<LoaderBase, LoaderFolderDictionaryEntry> loaderFolderDictionary = new();
}
