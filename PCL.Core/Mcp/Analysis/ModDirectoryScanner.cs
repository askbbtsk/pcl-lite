using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp.Analysis;

/// <summary>
/// 游戏目录里各个资源目录的定位与扫描。
/// </summary>
public static class ModDirectoryScanner
{
    public const string DisabledSuffix = ".disabled";
    public const string OldSuffix = ".old";

    public static string Mods(InstanceLocation location) => Path.Combine(location.GameRoot, "mods");
    public static string ResourcePacks(InstanceLocation location) => Path.Combine(location.GameRoot, "resourcepacks");
    public static string ShaderPacks(InstanceLocation location) => Path.Combine(location.GameRoot, "shaderpacks");
    public static string Saves(InstanceLocation location) => Path.Combine(location.GameRoot, "saves");
    public static string Config(InstanceLocation location) => Path.Combine(location.GameRoot, "config");
    public static string CrashReports(InstanceLocation location) => Path.Combine(location.GameRoot, "crash-reports");
    public static string Logs(InstanceLocation location) => Path.Combine(location.GameRoot, "logs");

    /// <summary>文件名是否处于「禁用」状态。</summary>
    public static bool IsDisabled(string path)
        => path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>去掉禁用后缀，得到「启用时应有的文件名」。</summary>
    public static string StripDisabledSuffix(string path)
    {
        if (path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            return path[..^DisabledSuffix.Length];
        if (path.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase))
            return path[..^OldSuffix.Length];
        return path;
    }

    /// <summary>
    /// 扫描一个目录下的 jar / zip 文件。
    /// </summary>
    /// <param name="directory">目标目录。</param>
    /// <param name="readMetadata">是否打开 jar 读元数据。数量多时关掉能快很多。</param>
    /// <param name="recursive">是否递归子目录。</param>
    public static List<ModFileEntry> Scan(string directory, bool readMetadata = true, bool recursive = false)
    {
        var result = new List<ModFileEntry>();
        if (!Directory.Exists(directory)) return result;

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", option);
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var path in files)
        {
            if (!ModJarReader.LooksLikeArchive(path)) continue;

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) continue;
            }
            catch (Exception)
            {
                continue;
            }

            result.Add(new ModFileEntry
            {
                FileName = info.Name,
                FullPath = path,
                Enabled = !IsDisabled(path),
                SizeBytes = info.Length,
                Modified = info.LastWriteTime,
                Metadata = readMetadata ? ModJarReader.Read(path) : null
            });
        }

        return result
            .OrderByDescending(e => e.Enabled)
            .ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>列出目录下全部文件（不含子目录），用于资源包/存档这类非 jar 内容。</summary>
    public static List<(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified)> ScanEntries(
        string directory)
    {
        var result = new List<(string, string, bool, long, DateTime)>();
        if (!Directory.Exists(directory)) return result;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(dir);
                result.Add((info.Name, dir, true, 0, info.LastWriteTime));
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                result.Add((info.Name, file, false, info.Length, info.LastWriteTime));
            }
        }
        catch (Exception)
        {
            // 目录不可读时返回已经拿到的部分
        }

        return result.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
