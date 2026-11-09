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

    public class AniGroupEntry
    {
        public List<AniData> data;
        public long startTick;
        public int Uuid = ModBase.GetUuid();
    }


    /// <summary>
    ///     控件动画执行是否开启。先 +1，再 -1。
    /// </summary>
    public static int AniControlEnabled
    {
        get => field;
        set
        {
            lock (aniControlEnabledLock)
            {
                field = value;
            }
        }
    }

}
