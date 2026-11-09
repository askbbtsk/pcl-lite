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

    public static string GetPathNameByCompType(CompType theType)
=>ResourceFolderNames.GetPathNameByCompType(theType);


    /// <summary>
    ///     通过文件名关键字和 Mod ID 比如 <c>fabric</c> <c>api</c> 和 <c>fabric-api</c> 来获取给定实例 mods 目录中某个 Mod 的
    ///     <see cref="LocalCompFile" /> 对象
    ///     <br />
    ///     <b>为了不浪费性能，关键字统一用小写</b>
    /// </summary>
    /// <returns>
    ///     如果文件名包含主关键字，以及其他关键字中的任意一个，同时 Mod ID 一致，即认为匹配，返回对应的对象，若没有匹配的文件则返回空值。
    /// </returns>
    public static LocalCompFile GetModLocalCompByKeywords(McInstance instance, string modId,
        string mainKeyword, params string[] keywords)
    {
        if (modId is null)
            return null;
        return GetModLocalCompByKeywords(instance, new[] { modId }, mainKeyword, keywords);
    }


    public static LocalCompFile GetModLocalCompByKeywords(McInstance instance, string[] modIds,
        string mainKeyword, params string[] keywords)
    {
        if (!instance.Modable)
            return null; // 跳过不可安装 Mod 实例
        var modFolder = $"{instance.PathInstance}mods";
        if (!Directory.Exists(modFolder))
            return null; // 确保 mods 目录存在
        foreach (var file in Directory.EnumerateFiles(modFolder, $"*{mainKeyword}*"))
        {
            var lowerFilePath = file.ToLower(); // 统一转为小写
            if (!regexIsJarFile.IsMatch(lowerFilePath))
                continue; // 检查是否是 jar 文件
            if ((keywords.Length > 0) && !keywords.Any(keyword => lowerFilePath.Contains(keyword)))
                continue; // 检查是否包含关键字
            var localComp = new LocalCompFile(file);
            localComp.Load();
            if (modIds.Any(modId => (localComp.ModId ?? "") == (modId ?? "")))
                return localComp;
        }

        return null;
    }

}
