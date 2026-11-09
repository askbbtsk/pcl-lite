using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class McInstance
    {
        /// <summary>
        ///     显示的描述文本。
        /// </summary>
        public string Desc = Lang.Text("Select.Instance.Description.NotLoaded");

        /// <summary>
        ///     强制实例分类，0 为未启用，1 为隐藏，2 及以上为其他普通分类。
        /// </summary>
        public McInstanceCardType displayType = McInstanceCardType.Auto;

        public bool IsLoaded;

        /// <summary>
        /// 是否已初始化从 JAR 中读取 version.json。
        /// </summary>
        private bool _jsonVersionInited;

        /// <summary>
        ///     是否为收藏的实例。
        /// </summary>
        public bool IsStar;

        /// <summary>
        ///     显示的实例图标。
        /// </summary>
        public string Logo;

        /// <summary>
        ///     实例的发布时间。
        /// </summary>
        public DateTime releaseTime = new(1970, 1, 1, 15, 0, 0);

        /// <summary>
        ///     该实例的列表检查原始结果，不受自定义影响。
        /// </summary>
        public McInstanceState state = McInstanceState.Error;

        /// <summary></summary>
        /// <param name="name">实例名，或实例文件夹的完整路径（不规定是否以 \ 结尾）。</param>
        public McInstance(string name)
        {
            PathInstance = (name.Contains(":") ? name : Path.Combine(ModFolder.mcFolderSelected, "versions", name)) + (name.EndsWithF(@"\") ? "" : @"\");
        }
    }
