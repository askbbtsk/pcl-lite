using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Backups;
using PCL.Core.Minecraft.Worlds;
using PCL.Core.Minecraft.PersonalOptions;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.Launch.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils;
using PCL.Core.Utils.Secret;
using PCL.Network;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using System.Globalization;
using PCL.Core.Logging;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;

namespace PCL;

public static partial class ModLaunch
{



    private static void McLaunchNatives(ModLoader.LoaderTask<List<ModLibrary.McLibToken>, int> loader)
    {
        // 创建文件夹
        var target = GetNativesFolder() + @"\";
        Directory.CreateDirectory(target);

        // 解压文件
        McLaunchLog("正在解压 Natives 文件");
        var existFiles = new List<string>();
        foreach (var native in loader.input)
        {
            if (!native.IsNatives)
                continue;
            ZipArchive zip;
            try
            {
                zip = new ZipArchive(new FileStream(native.LocalPath, FileMode.Open));
            }
            catch (InvalidDataException ex)
            {
                ModBase.Log(ex, "打开 Natives 文件失败（" + native.LocalPath + "）");
                File.Delete(native.LocalPath);
                throw new Exception(Lang.Text("Minecraft.Launch.Error.NativesCorrupted", native.LocalPath));
            }

            foreach (var entry in zip.Entries)
            {
                var fileName = entry.FullName;
                if (fileName.EndsWithF(".dll", true))
                {
                    // 实际解压文件的步骤
                    var filePath = target + fileName;
                    existFiles.Add(filePath);
                    var originalFile = new FileInfo(filePath);
                    if (originalFile.Exists)
                    {
                        if (originalFile.Length == entry.Length)
                        {
                            if (ModBase.modeDebug)
                                McLaunchLog("无需解压：" + filePath);
                            continue;
                        }

                        // 删除原文件
                        try
                        {
                            File.Delete(filePath);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            McLaunchLog("删除原 dll 访问被拒绝，这通常代表有一个 MC 正在运行，跳过解压：" + filePath);
                            McLaunchLog("实际的错误信息：" + ex);
                            break;
                        }
                    }

                    // 解压新文件
                    ModBase.WriteFile(filePath, entry.Open());
                    McLaunchLog("已解压：" + filePath);
                }
            }

            if (zip is not null)
                zip.Dispose();
        }

        // 删除多余文件
        foreach (var fileName in Directory.GetFiles(target))
        {
            if (existFiles.Contains(fileName))
                continue;
            try
            {
                McLaunchLog("删除：" + fileName);
                File.Delete(fileName);
            }
            catch (UnauthorizedAccessException ex)
            {
                McLaunchLog("删除多余文件访问被拒绝，跳过删除步骤");
                McLaunchLog("实际的错误信息：" + ex);
                return;
            }
        }
    }


    /// <summary>
    ///     获取 Natives 文件夹路径，不以 \ 结尾。
    /// </summary>
    private static string GetNativesFolder()
    {
        var result = Path.Combine(ModInstanceList.McMcInstanceSelected.PathInstance, ModInstanceList.McMcInstanceSelected.Name + "-natives");
        if (SystemInfo.IsGBKEncoding || result.IsASCII())
            return result;
        result = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "bin", "natives");
        if (result.IsASCII())
            return result;
        return Path.Combine(SystemPaths.DriveLetter, "ProgramData", "PCL", "natives");
    }

}
