using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using fNbt;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;
using static PCL.ModComp;
using static PCL.ModLoader;

namespace PCL;

public static partial class ModLocalComp
{
public partial class LocalCompFile
{

        /// <summary>
        ///     是否为文件夹项。
        /// </summary>
        public bool IsFolder => path.EndsWithF(@"\__FOLDER__", true);


        /// <summary>
        ///     获取实际的文件夹路径（去除 __FOLDER__ 标记）。
        /// </summary>
        public string ActualPath
        {
            get
            {
                if (IsFolder) return path.Replace(@"\__FOLDER__", "");

                return path;
            }
        }


        /// <summary>
        ///     Mod 资源的完整路径，去除最后的 .disabled 和 .old。
        /// </summary>
        public string RawPath => ModBase.GetPathFromFullPath(path) + RawFileName;


        /// <summary>
        ///     资源的完整文件名。
        /// </summary>
        public string FileName
        {
            get
            {
                if (IsFolder && !string.IsNullOrEmpty(Name)) return Name;

                return ModBase.GetFileNameFromPath(path);
            }
        }


        /// <summary>
        ///     Mod 资源的完整文件名，去除最后的 .disabled 和 .old。
        /// </summary>
        public string RawFileName => FileName.Replace(".disabled", "").Replace(".old", "");


        /// <summary>
        ///     资源的状态。对于 Mod 有 Disabled
        /// </summary>
        public LocalFileStatus State
        {
            get
            {
                Load();
                if (!IsFileAvailable) return LocalFileStatus.Unavailable;

                if (path.EndsWithF(".disabled", true) || path.EndsWithF(".old", true)) return LocalFileStatus.Disabled;

                return LocalFileStatus.Fine;
            }
        }


        public enum LocalFileStatus
        {
            Fine = 0,
            Disabled = 1,
            Unavailable = 2
        }

}
}
