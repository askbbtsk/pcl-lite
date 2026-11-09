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
    ///     清除某 ini 文件的运行时缓存。
    /// </summary>
    /// <param name="fileName">文件完整路径或简写文件名。简写将会使用“ApplicationName\文件名.ini”作为路径。</param>
    public static void IniClearCache(string fileName)
    {
        if (!fileName.Contains(@":\"))
            fileName = $@"{exePath}PCL\{fileName}.ini";
        if (iniCache.ContainsKey(fileName))
            iniCache.Remove(fileName, out _);
    }


    /// <summary>
    ///     获取 ini 文件缓存。如果没有，则新读取 ini 文件内容。
    ///     在文件不存在或读取失败时返回 Nothing。
    /// </summary>
    /// <param name="fileName">文件完整路径或简写文件名。简写将会使用“ApplicationName\文件名.ini”作为路径。</param>
    private static ConcurrentDictionary<string, string> IniGetContent(string fileName)
    {
        try
        {
            // 还原文件路径
            if (!fileName.Contains(@":\"))
                fileName = $@"{exePath}PCL\{fileName}.ini";
            // 检索缓存
            if (iniCache.ContainsKey(fileName))
                return iniCache[fileName];
            // 读取文件
            if (!File.Exists(fileName))
                return null;
            var ini = new ConcurrentDictionary<string, string>();
            foreach (var line in ReadFile(fileName)
                         .Split("\r\n".ToArray(), StringSplitOptions.RemoveEmptyEntries))
            {
                var index = line.IndexOfF(":");
                if (index > 0)
                    ini[line.Substring(0, index)] = line.Substring(index + 1); // 可能会有重复键，见 #3616
            }

            iniCache[fileName] = ini;
            return ini;
        }
        catch (Exception ex)
        {
            Log(ex, $"生成 ini 文件缓存失败（{fileName}）", LogLevel.Hint);
            return null;
        }
    }


    /// <summary>
    ///     读取 ini 文件。这可能会使用到缓存。
    /// </summary>
    /// <param name="fileName">文件完整路径或简写文件名。简写将会使用“ApplicationName\文件名.ini”作为路径。</param>
    /// <param name="key">键。</param>
    /// <param name="defaultValue">没有找到键时返回的默认值。</param>
    public static string ReadIni(string fileName, string key, string defaultValue = "")
    {
        var content = IniGetContent(fileName);
        if (content is null || !content.ContainsKey(key))
            return defaultValue;
        return content[key];
    }


    /// <summary>
    ///     判断 ini 文件中是否包含某个键。这可能会使用到缓存。
    /// </summary>
    public static bool HasIniKey(string fileName, string key)
    {
        var content = IniGetContent(fileName);
        return content is not null && content.ContainsKey(key);
    }


    /// <summary>
    ///     从 ini 文件中移除某个键。这会更新缓存。
    /// </summary>
    public static void DeleteIniKey(string fileName, string key)
    {
        WriteIni(fileName, key, null);
    }


    /// <summary>
    ///     写入 ini 文件，这会更新缓存。
    ///     若 Value 为 Nothing，则删除该键。
    /// </summary>
    /// <param name="fileName">文件完整路径或简写文件名。简写将会使用“ApplicationName\文件名.ini”作为路径。</param>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    /// <remarks></remarks>
    public static void WriteIni(string fileName, string key, string value)
    {
        try
        {
            // 预处理
            if (key.Contains(":"))
                throw new Exception($"尝试写入 ini 文件 {fileName} 的键名中包含了冒号：{key}");
            key = key.Replace("\r", "").Replace("\n", "");
            value = value?.Replace("\r", "").Replace("\n", "");
            // 防止争用
            lock (writeIniLock)
            {
                // 获取目前文件
                var content = IniGetContent(fileName);
                if (content is null)
                    content = new ConcurrentDictionary<string, string>();
                // 更新值
                if (value is null)
                {
                    if (!content.ContainsKey(key))
                        return; // 无需处理
                    content.Remove(key, out _);
                }
                else
                {
                    if (content.ContainsKey(key) && (content[key] ?? "") == (value ?? ""))
                        return; // 无需处理
                    content[key] = value;
                }

                // 写入文件
                var fileContent = new StringBuilder();
                foreach (var pair in content)
                {
                    fileContent.Append(pair.Key);
                    fileContent.Append(":");
                    fileContent.Append(pair.Value);
                    fileContent.Append("\r\n");
                }

                if (!fileName.Contains(@":\"))
                    fileName = $@"{exePath}PCL\{fileName}.ini";
                WriteFile(fileName, fileContent.ToString());
            }
        }
        catch (Exception ex)
        {
            Log(ex, $"写入文件失败（{fileName} → {key}:{value}）", LogLevel.Hint);
        }
    }


    // 路径处理
    /// <summary>
    ///     从文件路径或者 Url 获取不包含文件名的路径，或获取文件夹的父文件夹路径。
    ///     取决于原路径格式，路径以 / 或 \ 结尾。
    ///     不包含路径将会抛出异常。
    /// </summary>
    public static string GetPathFromFullPath(string filePath)
    {
        string getPathFromFullPathRet = default;
        if (!(filePath.Contains(@"\") || filePath.Contains("/")))
            throw new Exception("不包含路径：" + filePath);
        if (filePath.EndsWithF(@"\") || filePath.EndsWithF("/"))
        {
            // 是文件夹路径
            var isRight = filePath.EndsWithF(@"\");
            filePath = filePath.Substring(0, filePath.Length - 1);
            getPathFromFullPathRet = filePath.Substring(0, filePath.LastIndexOfAny(new[] { '\\', '/' })) +
                                     (isRight ? @"\" : "/");
        }
        else
        {
            // 是文件路径
            getPathFromFullPathRet = filePath.Substring(0, filePath.LastIndexOfAny(new[] { '\\', '/' }) + 1);
            if (string.IsNullOrEmpty(getPathFromFullPathRet))
                throw new Exception("不包含路径：" + filePath);
        }

        return getPathFromFullPathRet;
    }


    /// <summary>
    ///     从文件路径或者 Url 获取不包含路径的文件名。不包含文件名将会抛出异常。
    /// </summary>
    public static string GetFileNameFromPath(string filePath)
    {
        filePath = filePath.Replace("/", @"\");
        if (filePath.EndsWithF(@"\"))
            throw new Exception("不包含文件名：" + filePath);
        if (filePath.Contains("?"))
            filePath = filePath.Substring(0, filePath.IndexOfF("?")); // 去掉网络参数后的 ?
        if (filePath.Contains(@"\"))
            filePath = filePath.Substring(filePath.LastIndexOfF(@"\") + 1);
        var length = filePath.Length;
        if (length == 0)
            throw new Exception("不包含文件名：" + filePath);
        if (length > 250)
            throw new PathTooLongException("文件名过长：" + filePath);
        return filePath;
    }


    /// <summary>
    ///     从文件路径或者 Url 获取不包含路径与扩展名的文件名。不包含文件名将会抛出异常。
    /// </summary>
    public static string GetFileNameWithoutExtentionFromPath(string filePath)
    {
        return Path.GetFileNameWithoutExtension(filePath);
    }


    /// <summary>
    ///     从文件夹路径获取文件夹名。
    /// </summary>
    public static string GetFolderNameFromPath(string folderPath)
    {
        if (folderPath.EndsWithF(@":\") || folderPath.EndsWithF(@":\\"))
            return folderPath.Substring(0, 1);
        if (folderPath.EndsWithF(@"\") || folderPath.EndsWithF("/"))
            folderPath = folderPath.Substring(0, folderPath.Length - 1);
        return GetFileNameFromPath(folderPath);
    }


    // 读取、写入、复制文件
    /// <summary>
    ///     复制文件。会自动创建文件夹、会覆盖已有的文件。
    /// </summary>
    public static void CopyFile(string fromPath, string toPath)
    {
        try
        {
            // 还原文件路径
            if (!fromPath.Contains(@":\"))
                fromPath = exePath + fromPath;
            if (!toPath.Contains(@":\"))
                toPath = exePath + toPath;
            // 如果复制同一个文件则跳过
            if ((fromPath ?? "") == (toPath ?? ""))
                return;
            // 确保目录存在
            Directory.CreateDirectory(GetPathFromFullPath(toPath));
            // 复制文件
            File.Copy(fromPath, toPath, true);
        }
        catch (Exception ex)
        {
            throw new Exception("复制文件出错：" + fromPath + " → " + toPath, ex);
        }
    }


    /// <summary>
    ///     读取文件，如果失败则返回空数组。
    /// </summary>
    public static byte[] ReadFileBytes(string filePath, Encoding encoding = null)
    {
        try
        {
            // 还原文件路径
            if (!filePath.Contains(@":\"))
                filePath = exePath + filePath;
            if (File.Exists(filePath))
                using (var readStream =
                       new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var ms = new MemoryStream())
                    {
                        readStream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }

            Log("[System] 欲读取的文件不存在，已返回空内容：" + filePath);
            return Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            Log(ex, "读取文件出错：" + filePath);
            return Array.Empty<byte>();
        }
    }


    /// <summary>
    ///     读取文件，如果失败则返回空字符串。
    /// </summary>
    /// <param name="filePath">文件完整或相对路径。</param>
    public static string ReadFile(string filePath, Encoding encoding = null)
    {
        string readFileRet = default;
        var fileBytes = ReadFileBytes(filePath);
        readFileRet = encoding is null ? DecodeBytes(fileBytes) : encoding.GetString(fileBytes);
        return readFileRet;
    }


    /// <summary>
    ///     读取流中的所有文本。
    /// </summary>
    public static string ReadFile(Stream stream, Encoding encoding = null)
    {
        try
        {
            var readedContent = new MemoryStream();
            stream.CopyTo(readedContent);
            var bts = readedContent.ToArray();
            return (encoding ?? EncodingDetector.DetectEncoding(bts)).GetString(bts);
        }
        catch (Exception ex)
        {
            Log(ex, "读取流出错");
            return "";
        }
    }


    /// <summary>
    ///     写入文件。
    /// </summary>
    /// <param name="filePath">文件完整或相对路径。</param>
    /// <param name="text">文件内容。</param>
    /// <param name="append">是否将文件内容追加到当前文件，而不是覆盖它。</param>
    public static void WriteFile(string filePath, string text, bool append = false, Encoding? encoding = null)
    {
        // 处理相对路径
        if (!filePath.Contains(@":\"))
            filePath = exePath + filePath;
        // 确保目录存在
        Directory.CreateDirectory(GetPathFromFullPath(filePath));
        // 写入文件
        if (append)
            // 追加目前文件
            using (var writer = new StreamWriter(filePath, true,
                       encoding ?? EncodingDetector.DetectEncoding(ReadFileBytes(filePath))))
            {
                writer.Write(text);
            }
            else
            {
                // 直接写入字节
                var bytes = encoding is null ? new UTF8Encoding(false).GetBytes(text) : encoding.GetBytes(text);
                var tempPath = filePath + ".pcltmp." + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, filePath, true);
            }
    }


    /// <summary>
    ///     写入文件。
    ///     如果 CanThrow 设置为 False，返回是否写入成功。
    /// </summary>
    /// <param name="filePath">文件完整或相对路径。</param>
    /// <param name="content">文件内容。</param>
    /// <param name="append">是否将文件内容追加到当前文件，而不是覆盖它。</param>
    public static void WriteFile(string filePath, byte[] content, bool append = false)
    {
        // 处理相对路径
        if (!filePath.Contains(@":\"))
            filePath = exePath + filePath;
        // 确保目录存在
        Directory.CreateDirectory(GetPathFromFullPath(filePath));
        // 写入文件
        File.WriteAllBytes(filePath, content);
    }


    /// <summary>
    ///     将流写入文件。
    /// </summary>
    /// <param name="filePath">文件完整或相对路径。</param>
    public static bool WriteFile(string filePath, Stream stream)
    {
        try
        {
            // 还原文件路径
            if (!filePath.Contains(@":\"))
                filePath = exePath + filePath;
            // 确保目录存在
            Directory.CreateDirectory(GetPathFromFullPath(filePath));
            // 读取流
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                fs.SetLength(0L);
                stream.CopyTo(fs);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log(ex, "保存流出错");
            return false;
        }
    }


    /// <summary>
    ///     解码 Bytes。
    /// </summary>
    public static string DecodeBytes(byte[] bytes)
    {
        var length = bytes.Length;
        if (length < 3)
            return Encoding.UTF8.GetString(bytes);
        // 根据 BOM 判断编码
        if (bytes[0] >= 0xEF)
        {
            // 有 BOM 类型
            if (bytes[0] == 0xEF && bytes[1] == 0xBB) return Encoding.UTF8.GetString(bytes, 3, length - 3);

            if (bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 3, length - 3);

            if (bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 3, length - 3);

            return Encoding.GetEncoding("GB18030").GetString(bytes, 3, length - 3);
        }

        // 无 BOM 文件：GB18030（ANSI）或 UTF8
        var uTF8 = Encoding.UTF8.GetString(bytes);
        var errorChar = Encoding.UTF8.GetString(new[] { (byte)239, (byte)191, (byte)189 }).ToCharArray()[0];
        if (uTF8.Contains(errorChar)) return Encoding.GetEncoding("GB18030").GetString(bytes);

        return uTF8;
    }


    public static object GetHexString(Memory<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var c in bytes.Span)
            sb.Append(c.ToString("x2"));

        return sb.ToString();
    }


    // 文件校验
    /// <summary>
    ///     获取文件 MD5，若失败则返回空字符串。
    /// </summary>
    public static string GetFileMD5(string filePath)
    {
        var retry = false;
        Re: ;

        try
        {
            // 获取 MD5
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return (string)GetHexString(MD5Provider.Instance.ComputeHash(fs));
            }
        }
        catch (Exception ex)
        {
            if (retry || ex is FileNotFoundException)
            {
                Log(ex, "获取文件 MD5 失败：" + filePath);
                return "";
            }

            retry = true;
            Log(ex, "获取文件 MD5 可重试失败：" + filePath, LogLevel.Normal);
            Thread.Sleep(RandomUtils.NextInt(200, 500));
            goto Re;
        }
    }


    /// <summary>
    ///     获取文件 SHA512，若失败则返回空字符串。
    /// </summary>
    public static string GetFileSHA512(string filePath)
    {
        var retry = false;
        Re: ;

        try
        {
            // '检测该文件是否在下载中，若在下载则放弃检测
            // If IgnoreOnDownloading AndAlso NetManage.Files.ContainsKey(FilePath) AndAlso NetManage.Files(FilePath).State <= NetState.Merge Then Return ""
            // 获取 SHA512
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return (string)GetHexString(SHA512Provider.Instance.ComputeHash(fs));
            }
        }
        catch (Exception ex)
        {
            if (retry || ex is FileNotFoundException)
            {
                Log(ex, "获取文件 SHA512 失败：" + filePath);
                return "";
            }

            retry = true;
            Log(ex, "获取文件 SHA512 可重试失败：" + filePath, LogLevel.Normal);
            Thread.Sleep(RandomUtils.NextInt(200, 500));
            goto Re;
        }
    }


    /// <summary>
    ///     获取文件 SHA256，若失败则返回空字符串。
    /// </summary>
    public static string GetFileSHA256(string filePath)
    {
        var retry = false;
        Re: ;

        try
        {
            // '检测该文件是否在下载中，若在下载则放弃检测
            // If IgnoreOnDownloading AndAlso NetManage.Files.ContainsKey(FilePath) AndAlso NetManage.Files(FilePath).State <= NetState.Merge Then Return ""
            // 获取 SHA256
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return (string)GetHexString(SHA256Provider.Instance.ComputeHash(fs));
            }
        }
        catch (Exception ex)
        {
            if (retry || ex is FileNotFoundException)
            {
                Log(ex, "获取文件 SHA256 失败：" + filePath);
                return "";
            }

            retry = true;
            Log(ex, "获取文件 SHA256 可重试失败：" + filePath, LogLevel.Normal);
            Thread.Sleep(RandomUtils.NextInt(200, 500));
            goto Re;
        }
    }


    /// <summary>
    ///     获取文件 SHA1，若失败则返回空字符串。
    /// </summary>
    public static string GetFileSHA1(string filePath)
    {
        var retry = false;
        Re: ;

        try
        {
            // 获取 SHA1
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return (string)GetHexString(SHA1Provider.Instance.ComputeHash(fs));
            }
        }
        catch (Exception ex)
        {
            if (retry || ex is FileNotFoundException)
            {
                Log(ex, "获取文件 SHA1 失败：" + filePath);
                return "";
            }

            retry = true;
            Log(ex, "获取文件 SHA1 可重试失败：" + filePath, LogLevel.Normal);
            Thread.Sleep(RandomUtils.NextInt(200, 500));
            goto Re;
        }
    }


    /// <summary>
    ///     获取流的 SHA1，若失败则返回空字符串。
    /// </summary>
    public static string GetAuthSHA1(Stream inputStream)
    {
        try
        {
            return (string)GetHexString(SHA1Provider.Instance.ComputeHash(inputStream));
        }
        catch (Exception ex)
        {
            Log(ex, "获取流 SHA1 失败");
            return "";
        }
    }

}
