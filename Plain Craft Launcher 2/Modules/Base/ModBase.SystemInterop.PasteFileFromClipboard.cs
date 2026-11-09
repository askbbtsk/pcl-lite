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

    /// <summary>
    ///     从剪切板粘贴文件或文件夹
    /// </summary>
    /// <param name="dest">目标文件夹</param>
    /// <param name="copyFile">是否粘贴文件</param>
    /// <param name="copyDir">是否粘贴文件夹</param>
    /// <returns>总共粘贴的数量</returns>
    public static int PasteFileFromClipboard(string dest, bool copyFile = true, bool copyDir = true)
    {
        Log("[System] 从剪贴板粘贴文件到：" + dest);
        try
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count.Equals(0))
            {
                Log("[System] 剪贴板内无文件可粘贴");
                return 0;
            }

            var copiedFiles = 0;
            var copiedFolders = 0;
            foreach (var i in files)
            {
                if (copyFile && File.Exists(i)) // 文件
                    try
                    {
                        var thisDest = dest + GetFileNameFromPath(i);
                        if (File.Exists(thisDest))
                        {
                            Log("[System] 已存在同名文件：" + thisDest);
                        }
                        else
                        {
                            File.Copy(i, thisDest);
                            copiedFiles += 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "[System] 复制文件时出错");
                        continue;
                    }

                if (copyDir && Directory.Exists(i)) // 文件夹
                    try
                    {
                        var thisDest = dest + GetFolderNameFromPath(i);
                        if (Directory.Exists(thisDest))
                        {
                            Log("[System] 已存在同名文件夹：" + thisDest);
                        }
                        else
                        {
                            CopyDirectory(i, thisDest);
                            copiedFolders += 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "[System] 复制文件时出错");
                    }
            }

            HintService.Hint(Lang.Text("Common.Hint.FilesPasted", copiedFiles, copiedFolders));
        }
        catch (Exception ex)
        {
            Log(ex, "[System] 从剪切板粘贴文件失败", LogLevel.Hint);
        }

        return 0;
    }


    /// <summary>
    ///     获取程序打包资源的输入流。该资源必须声明为 <c>Resource</c> 类型，否则将会报错，<c>Images</c>
    ///     和 <c>Resources</c> 目录已默认声明该类型。
    /// </summary>
    public static Stream GetResourceStream(string path)
    {
        var resourceInfo =
            System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/{path}", UriKind.Absolute));
        return resourceInfo?.Stream;
    }



    /// <summary>
    ///     检查是否拥有某一文件夹的 I/O 权限。如果文件夹不存在，会返回 False。
    /// </summary>
    public static bool CheckPermission(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return false;
            if (!path.EndsWithF(@"\"))
                path += @"\";
            if (path.EndsWithF(@":\System Volume Information\") || path.EndsWithF(@":\$RECYCLE.BIN\"))
                return false;
            if (!Directory.Exists(path))
                return false;
            var fileName = "CheckPermission" + GetUuid();
            if (File.Exists(path + fileName))
                File.Delete(path + fileName);
            File.Create(path + fileName).Dispose();
            File.Delete(path + fileName);
            return true;
        }
        catch (Exception ex)
        {
            Log(ex, "没有对文件夹 " + path + " 的权限，请尝试以管理员权限运行 PCL");
            return false;
        }
    }


    /// <summary>
    ///     检查是否拥有某一文件夹的 I/O 权限。如果出错，则抛出异常。
    /// </summary>
    public static void CheckPermissionWithException(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentNullException("文件夹名不能为空！");
        if (!path.EndsWithF(@"\"))
            path += @"\";
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("文件夹不存在！");
        if (File.Exists(path + "CheckPermission"))
            File.Delete(path + "CheckPermission");
        File.Create(path + "CheckPermission").Dispose();
        File.Delete(path + "CheckPermission");
    }

}
