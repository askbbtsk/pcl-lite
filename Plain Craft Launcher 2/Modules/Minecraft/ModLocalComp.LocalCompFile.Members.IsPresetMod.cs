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
        ///     是否可能为前置 Mod。
        /// </summary>
        public bool IsPresetMod()
        {
            return !Dependencies.Any() && Name is not null &&
                   (Name.ToLower().Contains("core") || Name.ToLower().Contains("lib"));
        }


        /// <summary>
        ///     根据完整文件路径的文件扩展名判断是否为 Mod 文件。
        /// </summary>
        public static bool IsModFile(string path)
        {
            if (path is null || !path.Contains("."))
                return false;
            path = path.ToLower();
            if (path.EndsWithF(".jar", true) || path.EndsWithF(".zip", true) || path.EndsWithF(".litemod", true) ||
                path.EndsWithF(".jar.disabled", true) || path.EndsWithF(".zip.disabled", true) ||
                path.EndsWithF(".litemod.disabled", true) || path.EndsWithF(".jar.old", true) ||
                path.EndsWithF(".zip.old", true) || path.EndsWithF(".litemod.old", true))
                return true;
            return false;
        }


        /// <summary>
        ///     检查是否为指定类型的组件文件。
        /// </summary>
        public static bool IsCompFile(string path, CompType compType)
        {
            if (path is null || !path.Contains("."))
                return false;
            path = path.ToLower();
            switch (compType)
            {
                case CompType.Mod:
                {
                    return IsModFile(path);
                }
                case CompType.ResourcePack:
                case CompType.Shader:
                {
                    return path.EndsWithF(".zip", true);
                }
                case CompType.DataPack:
                {
                    return path.EndsWithF(".zip", true) || path.EndsWithF(".zip.disabled", true);
                }
                case CompType.Schematic:
                {
                    return path.EndsWithF(".litematic", true) || path.EndsWithF(".nbt", true) ||
                           path.EndsWithF(".schematic", true) || path.EndsWithF(".schem", true);
                }

                default:
                {
                    return false;
                }
            }
        }


        /// <summary>
        ///     获取图标路径。
        /// </summary>
        public string GetLogo()
        {
            if (Comp is not null && Comp.LogoUrl is not null)
                return Comp.LogoUrl;
            if (Logo is not null)
                return Logo;

            // 为文件夹设置特定图标
            if (IsFolder) return "pack://application:,,,/images/Icons/Folder.png";

            return ModBase.pathImage + "Icons/NoIcon.png";
        }

}
}
