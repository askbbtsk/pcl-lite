using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xaml;
using System.Xml.Linq;
using Microsoft.VisualBasic;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO;
using PCL.Core.Logging;
using PCL.Core.Utils;
using PCL.Core.Utils.Codecs;
using PCL.Core.Utils.Hash;
using PCL.Core.Utils.OS;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Size = System.Windows.Size;

namespace PCL;

public static partial class ModBase
{

    // 下列版本信息由更新器自动修改
    public static readonly string versionBaseName = Basics.VersionName;
    public static readonly string versionStandardCode = Basics.Metadata.Version.StandardVersion;
    public static readonly string upstreamVersion = Basics.Metadata.Version.UpstreamVersion;
    public static readonly string commitHash = Basics.Metadata.Version.Commit;
    public static readonly string commitHashShort = Basics.Metadata.Version.CommitDigest;
    public static readonly int versionCode = Basics.VersionCode;

#if DEBUG
    public const string versionBranchName = "Debug";
    public const string versionBranchCode = "100";
#elif DEBUGCI
    public const string versionBranchName = "CI";
    public const string versionBranchCode = "50";
#else
    public const string versionBranchName = "Publish";
    public const string versionBranchCode = "0";
#endif
    /// <summary>
    ///     主窗口句柄。
    /// </summary>
    public static nint frmHandle;

    // 龙猫味石山小记: 用最不靠谱的实现写出能跑的代码 (AppDomain.CurrentDomain.SetupInformation.ApplicationBase 获取到的是当前工作目录而不是可执行文件所在目录)
    /// <summary>
    ///     程序可执行文件所在目录，以“\”结尾。
    /// </summary>
    public static readonly string exePath = (Basics.ExecutableDirectory.EndsWith(@"\")
        ? Basics.ExecutableDirectory
        : Basics.ExecutableDirectory + @"\");

    /// <summary>
    ///     程序内嵌图片文件夹路径，以“/”结尾。
    /// </summary>
    public static readonly string pathImage = "pack://application:,,,/Plain Craft Launcher 2;component/Images/";

    /// <summary>
    ///     当前程序的语言。
    /// </summary>
    public static string currentLang = "zh_CN";

    /// <summary>
    ///     设置对象。
    /// </summary>
    public static ModSetup setup = new();

    /// <summary>
    ///     程序的打开计时。
    /// </summary>
    public static long applicationStartTick = TimeUtils.GetTimeTick();

    /// <summary>
    ///     程序打开时的时间。
    /// </summary>
    public static DateTime applicationOpenTime = DateTime.Now;

    /// <summary>
    ///     程序是否已结束。
    /// </summary>
    public static bool isProgramEnded = false;

    /// <summary>
    ///     程序的缓存文件夹路径，以 \ 结尾。
    /// </summary>
    public static string pathTemp = Paths.Temp + @"\";

    /// <summary>
    ///     AppData 中的 PCL 文件夹路径，以 \ 结尾。
    /// </summary>
    public static string pathAppdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCL") + @"\";

    /// <summary>
    ///     AppData 中的 PCLCE 配置文件夹路径，以 \ 结尾。
    /// </summary>
    public static string pathAppdataConfig = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) +
                                             (versionBranchName == "Debug" ? @"\.pclcedebug\" : @"\.pclce\");



    // =============================
    // ini
    // =============================

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> iniCache = new();

    private static readonly object writeIniLock = new();



    public static char vbLQ = Convert.ToChar(8220);
    public static char vbRQ = Convert.ToChar(8221);

    /// <summary>
    ///     本地搜索返回的最大模糊结果数。
    /// </summary>
    public const int MaxLocalSearchDepth = 25;

    /// <summary>
    ///     可用于临时存放文件的，不含任何特殊字符的文件夹路径，以“\”结尾。
    /// </summary>
    public static string pathPure = GetPureASCIIDir();

    private static int uuid = 1;
    private static object uuidLock;

    // DPI 转换
    public static readonly int dpi = (int)Math.Round(Graphics.FromHwnd(nint.Zero).DpiX);

    private static readonly int uiThreadId = Thread.CurrentThread.ManagedThreadId;



    public static bool modeDebug = false;

    private static bool isCriticalErrorTriggered;

}


/// <summary>
///     对数据绑定进行加法运算，使用参数决定加数。
/// </summary>
public class AdditionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return 0;
        double before;
        if (!double.TryParse(value.ToString(), out before))
            return 0;
        var scale = 1d;
        if (parameter is not null)
            double.TryParse(parameter.ToString(), out scale);
        return before + scale;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return Binding.DoNothing;
        double before;
        if (!double.TryParse(value.ToString(), out before))
            return Binding.DoNothing;
        var scale = 1d;
        if (parameter is not null)
            double.TryParse(parameter.ToString(), out scale);
        if (scale == 0d)
            return Binding.DoNothing;
        return before - scale;
    }
}

/// <summary>
///     对数据绑定进行乘法运算，使用参数决定乘数。
/// </summary>
public class MultiplicationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return 0;
        double before;
        if (!double.TryParse(value.ToString(), out before))
            return 0;
        var scale = 1d;
        if (parameter is not null)
            double.TryParse(parameter.ToString(), out scale);
        return before * scale;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return Binding.DoNothing;
        double before;
        if (!double.TryParse(value.ToString(), out before))
            return Binding.DoNothing;
        var scale = 1d;
        if (parameter is not null)
            double.TryParse(parameter.ToString(), out scale);
        if (scale == 0d)
            return Binding.DoNothing;
        return before / scale;
    }
}

/// <summary>
///     将取反的 Boolean 绑定到 Visibility。
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return Visibility.Visible;
        bool boolValue;
        return bool.TryParse(value.ToString(), out boolValue)
            ? boolValue ? Visibility.Collapsed : Visibility.Visible
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return false;
        return value is Visibility
            ? (Visibility)value != Visibility.Visible
            : false;
    }
}

/// <summary>
///     将 Boolean 取反。
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return false;
        bool boolValue;
        return bool.TryParse(value.ToString(), out boolValue) ? !boolValue : false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return false;

        if (bool.TryParse(value.ToString(), out var result)) return !result;

        return false;
    }
}
