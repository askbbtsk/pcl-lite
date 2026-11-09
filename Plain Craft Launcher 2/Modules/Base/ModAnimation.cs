using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Network;

using PCL.Core.App.Localization;
namespace PCL;

public static partial class ModAnimation
{
    private static int aniCount;
    private static int aniFPSCounter;
    private static long aniFPSTimer;

    /// <summary>
    ///     当前的动画 FPS。
    /// </summary>
    public static int aniFPS;


    /// <summary>
    ///     动画速度。最大为 200。
    /// </summary>
    public static double aniSpeed = 1d;

    /// <summary>
    ///     动画组列表。
    /// </summary>
    public static ConcurrentDictionary<string, AniGroupEntry> aniGroups = new();

    /// <summary>
    ///     上一次记刻的时间。
    /// </summary>
    private static long aniLastTick;

    /// <summary>
    ///     动画模块是否正在运行。
    /// </summary>
    public static bool aniRunning;

    private static readonly object aniControlEnabledLock = new();

}
