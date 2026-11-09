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
    ///     文件的校验规则。
    /// </summary>
    public class FileChecker
    {
        /// <summary>
        ///     文件的准确大小。
        ///     不检查则为 -1。
        /// </summary>
        public long actualSize = -1;

        /// <summary>
        ///     是否可以使用已经存在的文件。
        /// </summary>
        public bool canUseExistsFile = true;

        /// <summary>
        ///     文件的 MD5、SHA1 或 SHA256。会根据输入字符串的长度自动判断种类。
        ///     不检查则为 Nothing。
        /// </summary>
        public string hash;

        /// <summary>
        ///     是否要求为 JSON 文件。
        ///     即，开头结尾必须为 {} 或 []。
        /// </summary>
        public bool isJson;

        /// <summary>
        ///     文件的最小大小。
        ///     不检查则为 -1。
        /// </summary>
        public long minSize = -1;

        public FileChecker(long minSize = -1, long actualSize = -1, string hash = null, bool canUseExistsFile = true,
            bool isJson = false)
        {
            this.actualSize = actualSize;
            this.minSize = minSize;
            this.hash = hash;
            this.canUseExistsFile = canUseExistsFile;
            this.isJson = isJson;
        }

        /// <summary>
        ///     检查文件。若成功则返回 Nothing，失败则返回错误的描述文本，描述文本不以句号结尾。不会抛出错误。
        /// </summary>
        public string Check(string localPath)
        {
            try
            {
                Log($"[Checker] 开始校验文件 {localPath}", LogLevel.Developer);
                var info = new FileInfo(localPath);
                if (!info.Exists)
                    return "文件不存在：" + localPath;
                var fileSize = info.Length;
                var errorMessage = new List<string>();
                var allowIgnore = false; // 允许相信哈希正确但是大小不正确
                if (!string.IsNullOrEmpty(hash))
                {
                    if (hash.Length < 35) // MD5
                    {
                        var computedHash = GetFileMD5(localPath);
                        if ((hash.ToLowerInvariant() ?? "") != (computedHash ?? ""))
                            errorMessage.Add("文件 MD5 应为 " + hash + "，实际为 " + computedHash);
                    }
                    else if (hash.Length == 64) // SHA256
                    {
                        var computedHash = GetFileSHA256(localPath);
                        if ((hash.ToLowerInvariant() ?? "") != (computedHash ?? ""))
                            errorMessage.Add("文件 SHA256 应为 " + hash + "，实际为 " + computedHash);
                    }
                    else // SHA1 (40)
                    {
                        var computedHash = GetFileSHA1(localPath);
                        if ((hash.ToLowerInvariant() ?? "") != (computedHash ?? ""))
                            errorMessage.Add("文件 SHA1 应为 " + hash + "，实际为 " + computedHash);
                    }

                    allowIgnore = errorMessage.Count == 0;
                }

                if (actualSize >= 0L && actualSize != fileSize && !allowIgnore) // 不允许忽略大小不正确的情况
                    errorMessage.Add($"文件大小应为 {actualSize} B，实际为 {fileSize} B" +
                                     (fileSize < 2000L ? "，内容为" + ReadFile(localPath) : ""));

                if (minSize >= 0L && minSize > fileSize)
                    errorMessage.Add($"文件大小应大于 {minSize} B，实际为 {fileSize} B" +
                                     (fileSize < 2000L ? "，内容为：" + ReadFile(localPath) : ""));

                if (isJson)
                {
                    var content = ReadFile(localPath);
                    if (string.IsNullOrEmpty(content))
                        throw new Exception("读取到的文件为空");
                    try
                    {
                        GetJson(content);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(Lang.Text("Common.Error.InvalidJson"), ex);
                    }
                }

                if (errorMessage.Count != 0)
                {
                    errorMessage.Insert(0, $"实际校验地址：{localPath}");
                    return errorMessage.Join(";");
                }

                return null;
            }
            catch (Exception ex)
            {
                Log(ex, "检查文件出错");
                return ex.ToString();
            }
        }
    }


    /// <summary>
    ///     等待文件就绪可读，在指定超时时间内轮询检查文件是否存在且内容非空。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="timeoutMs">超时时间（毫秒）。</param>
    public static void WaitForFileReady(string filePath, int timeoutMs = 2000)
    {
        WaitForFileReady(filePath, timeoutMs, false);
    }


    /// <summary>
    ///     等待文件就绪可读，在指定超时时间内轮询检查文件是否存在且内容非空。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="timeoutMs">超时时间（毫秒）。</param>
    /// <param name="requireJson">是否要求文件为合法 JSON。</param>
    public static void WaitForFileReady(string filePath, int timeoutMs, bool requireJson)
    {
        filePath = filePath.Contains(@":\") ? filePath : exePath + filePath;
        var start = Environment.TickCount;
        long lastSize = -1;
        while (Environment.TickCount - start < timeoutMs)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    var info = new FileInfo(filePath);
                    var size = info.Length;
                    if (size <= 0)
                        continue;
                    if (!requireJson)
                    {
                        if (size == lastSize)
                            return;
                        lastSize = size;
                    }
                    else
                    {
                        var content = ReadFile(filePath);
                        if (!string.IsNullOrEmpty(content) && content.Trim().StartsWith("{"))
                            return;
                    }
                }
                catch (IOException)
                {
                }
            }
            Thread.Sleep(50);
        }
    }


    /// <summary>
    ///     尝试根据后缀名判断文件种类并解压文件，支持 gz 与 zip，会尝试将 Jar 以 zip 方式解压。
    ///     会尝试创建，但不会清空目标文件夹。
    /// </summary>
    public static void ExtractFile(string compressFilePath, string destDirectory, Encoding encode = null,
        Action<double> progressIncrementHandler = null)
    {
        Directory.CreateDirectory(destDirectory);
        destDirectory = Path.GetFullPath(destDirectory);
        if (!destDirectory.EndsWith(Path.DirectorySeparatorChar.ToString()))
            destDirectory += Path.DirectorySeparatorChar.ToString();
        if (compressFilePath.EndsWithF(".gz", true))
            // 以 gz 方式解压
            using (var compressedFile = new FileStream(compressFilePath, FileMode.Open, FileAccess.Read))
            {
                using (var decompressStream = new GZipStream(compressedFile, CompressionMode.Decompress))
                {
                    using (var extractFileStream =
                           new FileStream(
                               Path.Combine(destDirectory,
                                   GetFileNameFromPath(compressFilePath).ToLower().Replace(".tar", "")
                                       .Replace(".gz", "")), FileMode.OpenOrCreate, FileAccess.Write))
                    {
                        decompressStream.CopyTo(extractFileStream);
                    }
                }
            }
        else
            // 以 zip 方式解压
            using (var archive = ZipFile.Open(compressFilePath, ZipArchiveMode.Read,
                       encode ?? Encoding.GetEncoding("GB18030")))
            {
                var totalCount = archive.Entries.Count;
                foreach (var entry in archive.Entries)
                {
                    if (progressIncrementHandler is not null)
                        progressIncrementHandler(1d / totalCount);
                    var destinationPath = Path.GetFullPath(Path.Combine(destDirectory, entry.FullName));
                    if (!destinationPath.StartsWithF(destDirectory))
                        throw new Exception(
                            $"解压文件 {entry.FullName} 错误：解压文件路径 {destinationPath} 不在目标目录 {destDirectory} 内");
                    if (destinationPath.EndsWithF(@"\") || destinationPath.EndsWithF("/"))
                    {
                    }
                    else
                    {
                        Directory.CreateDirectory(GetPathFromFullPath(destinationPath));
                        entry.ExtractToFile(destinationPath, true);
                    }
                }
            }
    }


    /// <summary>
    ///     删除文件夹，返回删除的文件个数。通过参数选择是否抛出异常。
    /// </summary>
    public static int DeleteDirectory(string path, bool ignoreIssue = false)
    {
        if (!Directory.Exists(path))
            return 0;
        var deletedCount = 0;
        string[] files;
        try
        {
            files = Directory.GetFiles(path);
        }
        catch (DirectoryNotFoundException ex) // #4549
        {
            Log(ex, $"疑似为孤立符号链接，尝试直接删除（{path}）", LogLevel.Developer);
            Directory.Delete(path);
            return 0;
        }

        foreach (var filePath in files)
        {
            var retriedFile = false;
            RetryFile: ;

            try
            {
                File.Delete(filePath);
                deletedCount += 1;
            }
            catch (Exception ex)
            {
                if (!retriedFile)
                {
                    retriedFile = true;
                    Log(ex, $"删除文件失败，将在 0.3s 后重试（{filePath}）");
                    Thread.Sleep(300);
                    goto RetryFile;
                }

                if (ignoreIssue)
                    Log(ex, "删除单个文件可忽略地失败");
                else
                    throw;
            }
        }

        foreach (var str in Directory.GetDirectories(path))
            DeleteDirectory(str, ignoreIssue);
        var retriedDir = false;
        RetryDir: ;

        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            if (!retriedDir && !RunInUi())
            {
                retriedDir = true;
                Log(ex, $"删除文件夹失败，将在 0.3s 后重试（{path}）");
                Thread.Sleep(300);
                goto RetryDir;
            }

            if (ignoreIssue)
                Log(ex, "删除单个文件夹可忽略地失败");
            else
                throw;
        }

        return deletedCount;
    }


    /// <summary>
    ///     复制文件夹，失败会抛出异常。
    /// </summary>
    public static void CopyDirectory(string fromPath, string toPath, Action<double> progressIncrementHandler = null)
    {
        fromPath = fromPath.Replace("/", @"\");
        if (!fromPath.EndsWithF(@"\"))
            fromPath += @"\";
        toPath = toPath.Replace("/", @"\");
        if (!toPath.EndsWithF(@"\"))
            toPath += @"\";
        var allFiles = EnumerateFiles(fromPath).ToList();
        var fileCount = allFiles.Count;
        foreach (var file in allFiles)
        {
            CopyFile(file.FullName, file.FullName.Replace(fromPath, toPath));
            if (progressIncrementHandler is not null)
                progressIncrementHandler(1d / fileCount);
        }
    }


    /// <summary>
    ///     遍历文件夹中的所有文件。
    /// </summary>
    public static IEnumerable<FileInfo> EnumerateFiles(string directory)
    {
        var info = new DirectoryInfo(ShortenPath(directory));
        if (!info.Exists)
            return new List<FileInfo>();
        return info.EnumerateFiles("*", SearchOption.AllDirectories);
    }


    /// <summary>
    ///     若路径长度大于指定值，则将长路径转换为短路径。
    /// </summary>
    public static string ShortenPath(string longPath, int shortenThreshold = 247)
    {
        if (longPath.Length <= shortenThreshold)
            return longPath;
        var shortPath = new StringBuilder(260);
        GetShortPathName(longPath, shortPath, 260);
        return shortPath.ToString();
    }


    public static void MoveDirectory(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);
        foreach (var filePath in Directory.GetFiles(sourceDir))
        {
            var fileName = GetFileNameFromPath(filePath);
            File.Move(filePath, Path.Combine(targetDir, fileName));
        }

        foreach (var dirPath in Directory.GetDirectories(sourceDir))
        {
            var dirName = GetFolderNameFromPath(dirPath);
            MoveDirectory(dirPath, Path.Combine(targetDir, dirName));
        }
    }


    [DllImport("kernel32", EntryPoint = "GetShortPathNameA")]
    private static extern int GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, int cchBuffer);


    public static void CreateSymbolicLink(string linkPath, string targetPath, int flags)
    {
        var cMDProcess = new Process();
        var linkDPath = ModLaunch.ExtractLinkD();
        {
            var withBlock = cMDProcess.StartInfo;
            withBlock.FileName = linkDPath;
            withBlock.Arguments = $"\"{linkPath}\" \"{targetPath}\"";
            withBlock.CreateNoWindow = true;
            withBlock.UseShellExecute = false;
        }
        cMDProcess.Start();
        while (!cMDProcess.HasExited)
        {
        }
    }

}
