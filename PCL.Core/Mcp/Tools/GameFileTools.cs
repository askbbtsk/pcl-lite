using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Http;
using PCL.Core.Mcp.Analysis;

namespace PCL.Core.Mcp.Tools;

/// <summary>
/// 游戏目录里的「配置文件 / 存档 / 新装 mod」三类写操作。
/// <para>
/// 这三件事以前只能靠用户自己动手，模型只能建议不能执行，审计里被点名为功能断点。
/// 补上时统一遵守三条：
/// </para>
/// <list type="number">
/// <item>写入前先备份，且备份落在游戏目录内（<c>PCL/McpBackup</c>），不往系统目录扔东西。</item>
/// <item>所有路径过 <see cref="McpPath.ResolveUnder" />，不认绝对路径与 <c>..</c>。</item>
/// <item>外部下载只认 https，限制体积，校验扩展名与 zip 魔数，绝不覆盖已有文件。</item>
/// </list>
/// </summary>
internal static class GameFileTools
{
    private const string BackupFolderName = "McpBackup";
    private const long MaxModBytes = 128L * 1024 * 1024;

    /// <summary>options.txt 的键名规则。Minecraft 自己的键都是字母数字下划线，允许点与横线留点余量。</summary>
    private static readonly Regex OptionKeyPattern = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);

    public static void Register(McpServerCatalog catalog)
    {
        RegisterOptionGet(catalog);
        RegisterOptionSet(catalog);
        RegisterBackupSaves(catalog);
        RegisterListBackups(catalog);
        RegisterInstallMod(catalog);
    }

    #region options.txt

    private static void RegisterOptionGet(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_game_option_get",
            Title = "读取游戏设置",
            Description =
                "读实例的 options.txt（游戏内的视频/音频/控制设置）。\n\n"
                + "传 key 只取一项（例如 renderDistance、fov、lang）；不传就列出全部，条目很多，建议先按关键词过滤。\n"
                + "这个文件在游戏退出时会被游戏自己重写，所以「读到的」和「现在界面上的」可能差一次退出。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Str("key", "只读某一项，例如 renderDistance。")
                .Str("filter", "按关键词过滤键名，例如 \"volume\"。")
                .Int("limit", "最多返回多少项，默认 200。", min: 1, max: 2000)
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var path = Path.Combine(loc.GameRoot, "options.txt");
                if (!File.Exists(path))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"实例 {loc.Name} 里没有 options.txt。",
                        "游戏至少启动过一次才会生成这个文件。"));

                var options = ReadOptions(path);
                var key = args.Str("key", "").Trim();
                if (key.Length > 0)
                {
                    // KeyValuePair 是值类型，FirstOrDefault 找不到时返回 default 而不是 null，
                    // 用可空包装才能区分「没有这一项」
                    KeyValuePair<string, string>? found = null;
                    foreach (var option in options)
                    {
                        if (!string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                        found = option;
                        break;
                    }

                    return Task.FromResult<object?>(found is null
                        ? McpResults.Error(
                            $"options.txt 里没有「{key}」这一项。",
                            "用 pcl_game_option_get 不带 key 看全部键名。")
                        : McpResults.Ok(
                            ("instance", loc.Name),
                            ("path", path),
                            ("key", found.Value.Key),
                            ("value", found.Value.Value),
                            ("found", true)));
                }

                var filter = args.Str("filter", "");
                var limit = Math.Clamp(args.Int("limit", 200), 1, 2000);

                IEnumerable<KeyValuePair<string, string>> source = options;
                if (!string.IsNullOrWhiteSpace(filter))
                    source = source.Where(o => o.Key.Contains(filter, StringComparison.OrdinalIgnoreCase));

                var list = source.Take(limit)
                    .Select(o => new Dictionary<string, object?> { ["key"] = o.Key, ["value"] = o.Value })
                    .ToList();

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("path", path),
                    ("total", options.Count),
                    ("returned", list.Count),
                    ("truncated", options.Count > list.Count),
                    ("options", list)));
            }
        });
    }

    private static void RegisterOptionSet(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_game_option_set",
            Title = "修改游戏设置",
            Description =
                "改实例 options.txt 里的某一项，例如把 renderDistance 设成 8。\n\n"
                + "改之前会把原文件备份到 `PCL/McpBackup/`，改坏了可以拿回来。\n"
                + "只改这一行，其余行原样保留。键不存在时追加到末尾。\n\n"
                + "**游戏正在运行时不要改**：游戏退出时会把内存里的设置整个写回去，你的修改会被覆盖。"
                + "需要改就先 pcl_stop_game。\n\n"
                + "值必须是不含换行的单行文本；布尔值写 true/false。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Str("key", "设置项键名，例如 renderDistance。", required: true)
                .Str("value", "新值，例如 8。", required: true)
                .Build(),
            Required = McpCapability.Write,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var key = args.Str("key", "").Trim();
                if (!OptionKeyPattern.IsMatch(key))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"「{key}」不是合法的设置项名。",
                        "键名只能是字母、数字、下划线、点、横线，最长 64 个字符。"));

                var value = args.Str("value", "");
                if (value.Contains('\n') || value.Contains('\r'))
                    return Task.FromResult<object?>(McpResults.Error("值里不能有换行。"));
                if (value.Length > 1024)
                    return Task.FromResult<object?>(McpResults.Error("值太长了（超过 1024 字符）。"));

                var path = Path.Combine(loc.GameRoot, "options.txt");
                var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];

                var replaced = false;
                var oldValue = "";
                for (var i = 0; i < lines.Count; i++)
                {
                    var separator = lines[i].IndexOf(':');
                    if (separator <= 0) continue;
                    if (!string.Equals(lines[i][..separator], key, StringComparison.OrdinalIgnoreCase)) continue;

                    oldValue = lines[i][(separator + 1)..];
                    lines[i] = $"{key}:{value}";
                    replaced = true;
                    break;
                }

                if (!replaced) lines.Add($"{key}:{value}");

                var backupPath = File.Exists(path) ? BackupFile(loc, path, "options") : null;

                try
                {
                    File.WriteAllLines(path, lines, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    return Task.FromResult<object?>(McpResults.Error(
                        $"写入 options.txt 失败：{ex.Message}",
                        "游戏正在运行时会占用这个文件，先 pcl_stop_game 再试。"));
                }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("path", path),
                    ("key", key),
                    ("oldValue", replaced ? oldValue : null),
                    ("value", value),
                    ("created", !replaced),
                    ("backup", backupPath),
                    ("message", replaced
                        ? $"已把 {key} 从 {oldValue} 改成 {value}。"
                        : $"options.txt 里原来没有 {key}，已追加为 {value}。")));
            }
        });
    }

    private static List<KeyValuePair<string, string>> ReadOptions(string path)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            result.Add(new KeyValuePair<string, string>(line[..separator], line[(separator + 1)..]));
        }

        return result;
    }

    #endregion

    #region 存档备份

    private static void RegisterBackupSaves(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_backup_saves",
            Title = "备份存档",
            Description =
                "把实例的存档打包成 zip，放进 `PCL/McpBackup/`。\n\n"
                + "传 save 只备份那一个存档；不传就把 saves 下所有存档一起打包。\n"
                + "只备份，不动原存档——这是给「想试点危险操作」留的退路。\n\n"
                + "游戏正在运行且有存档被占用时，部分文件可能读不到，结果里会写明跳过数量。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Str("save", "只备份这个存档（saves 下的目录名）。留空备份全部。")
                .Build(),
            Required = McpCapability.Write,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var savesDirectory = ModDirectoryScanner.Saves(loc);
                if (!Directory.Exists(savesDirectory))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"实例 {loc.Name} 没有 saves 目录。",
                        "这个实例还没生成过存档。"));

                var requested = args.Str("save", "").Trim();
                List<string> sources;
                if (requested.Length > 0)
                {
                    var resolved = McpPath.ResolveUnder(savesDirectory, requested);
                    if (resolved is null || !Directory.Exists(resolved))
                        return Task.FromResult<object?>(McpResults.Error(
                            $"找不到存档「{requested}」。",
                            "用 pcl_list_saves 看真实目录名。"));

                    sources = [resolved!];
                }
                else
                {
                    sources = Directory.GetDirectories(savesDirectory).ToList();
                }

                if (sources.Count == 0)
                    return Task.FromResult<object?>(McpResults.Error("saves 目录是空的，没有可备份的存档。"));

                var backupDirectory = Path.Combine(loc.GameRoot, "PCL", BackupFolderName);
                Directory.CreateDirectory(backupDirectory);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var label = requested.Length > 0 ? Sanitize(requested) : "all";
                var zipPath = Path.Combine(backupDirectory, $"saves-{label}-{stamp}.zip");

                long bytes = 0;
                var skipped = 0;
                try
                {
                    using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                    foreach (var source in sources)
                    {
                        var root = Path.GetFileName(source);
                        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var entryName = Path.Combine(root, Path.GetRelativePath(source, file))
                                    .Replace('\\', '/');
                                archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                                bytes += new FileInfo(file).Length;
                            }
                            catch (Exception)
                            {
                                // 被游戏占用 / 无权限的文件跳过，最后统一报数量
                                skipped++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.Delete(zipPath);
                    }
                    catch (Exception)
                    {
                        // 删不掉就算了，不要让错误信息被覆盖
                    }

                    return Task.FromResult<object?>(McpResults.Error(
                        $"打包存档失败：{ex.Message}",
                        "游戏运行中会占用 region 文件，先 pcl_stop_game 再试。"));
                }

                var zipSize = new FileInfo(zipPath).Length;

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("path", zipPath),
                    ("saves", sources.Select(Path.GetFileName).ToList()),
                    ("saveCount", sources.Count),
                    ("rawSize", InstanceAnalyzer.FormatSize(bytes)),
                    ("zipSize", InstanceAnalyzer.FormatSize(zipSize)),
                    ("skippedFiles", skipped),
                    ("message", $"已备份 {sources.Count} 个存档到 {zipPath}。")));
            }
        });
    }

    private static void RegisterListBackups(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_list_backups",
            Title = "列出备份",
            Description =
                "列出 `PCL/McpBackup/` 里已有的备份文件（存档 zip、options.txt 快照）。\n\n"
                + "恢复动作没有做成工具：把文件复制回原位这一步涉及覆盖存档，交给用户自己在资源管理器里做更安全。\n"
                + "这里只负责告诉用户「备份在哪、有多新」。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var directory = Path.Combine(loc.GameRoot, "PCL", BackupFolderName);
                if (!Directory.Exists(directory))
                    return Task.FromResult<object?>(McpResults.Ok(
                        ("instance", loc.Name),
                        ("directory", directory),
                        ("exists", false),
                        ("count", 0),
                        ("backups", new List<object?>())));

                var files = new DirectoryInfo(directory)
                    .EnumerateFiles()
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(200)
                    .Select(f => new Dictionary<string, object?>
                    {
                        ["fileName"] = f.Name,
                        ["size"] = InstanceAnalyzer.FormatSize(f.Length),
                        ["created"] = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                    })
                    .ToList();

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("directory", directory),
                    ("exists", true),
                    ("count", files.Count),
                    ("backups", files)));
            }
        });
    }

    #endregion

    #region 安装 mod

    private static void RegisterInstallMod(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_install_mod",
            Title = "下载安装 mod",
            Description =
                "从 https 链接下载一个 jar，校验后放进实例的 mods 目录。\n\n"
                + "用途：补齐前置（Fabric API、Architectury 之类）、装用户指定的 mod。\n"
                + "**不会**替你去 Modrinth / CurseForge 搜索——你得先把直链拿到。\n\n"
                + "安全检查：只接受 https；文件必须是 zip（jar 就是 zip）；体积上限 128 MB；\n"
                + "同名文件已存在时直接失败，不覆盖；传了 sha256 就强制校验。\n"
                + "装完会顺手读出 modId 和版本，方便判断前置是否装对了。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Str("url", "jar 的 https 直链。", required: true)
                .Str("fileName", "保存成什么文件名。留空从 URL 最后一段推导。")
                .Str("sha256", "期望的 SHA-256（可选）。给了就必须完全匹配。")
                .Bool("enable", "是否立即启用（不加 .disabled 后缀）。默认启用。")
                .Build(),
            Required = McpCapability.Write,
            Handler = async (args, token) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return error;
                var loc = location!;

                var url = args.Str("url", "").Trim();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return McpResults.Error("url 不是合法的绝对地址。");
                if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    return McpResults.Error(
                        "只接受 https 链接。",
                        "http 下载的内容可能被中间人替换，jar 是会被游戏加载执行的代码，不能冒这个风险。");

                var fileName = McpPath.FileNameOnly(args.Str("fileName", ""));
                if (fileName.Length == 0) fileName = McpPath.FileNameOnly(Uri.UnescapeDataString(uri.AbsolutePath));
                if (fileName.Length == 0)
                    return McpResults.Error("无法从 URL 推断文件名，请显式传 fileName。");
                if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    return McpResults.Error(
                        $"「{fileName}」不是 .jar 文件。",
                        "这个工具只装 mod / 前置，不处理资源包与整合包。");

                var modsDirectory = ModDirectoryScanner.Mods(loc);
                Directory.CreateDirectory(modsDirectory);

                var destination = Path.Combine(modsDirectory, fileName);
                if (File.Exists(destination) || File.Exists(destination + ModDirectoryScanner.DisabledSuffix))
                    return McpResults.Error(
                        $"mods 目录里已经有「{fileName}」了，不覆盖。",
                        "确认要换版本的话，先 pcl_delete_mod 删掉旧的（默认软删除，可恢复）再装。");

                var tempPath = destination + ".mcpdownload";
                var expectedHash = args.Str("sha256", "").Trim();

                try
                {
                    using var response = await HttpRequest.Create(url).SendAsync(cancellationToken: token)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return McpResults.Error($"下载失败：HTTP {(int)response.StatusCode}。");

                    if (response.Content.Headers.ContentLength is { } declared && declared > MaxModBytes)
                        return McpResults.Error($"文件太大（{InstanceAnalyzer.FormatSize(declared)}），上限 128 MB。");

                    await using (var source = await response.AsStreamAsync(token).ConfigureAwait(false))
                    await using (var target = File.Create(tempPath))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > MaxModBytes)
                                return McpResults.Error("文件超过 128 MB，已中止下载。");
                            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        }
                    }

                    if (!LooksLikeZip(tempPath))
                        return McpResults.Error(
                            "下载到的内容不是 zip/jar（文件头不对）。",
                            "链接可能指向了网页而不是文件本体，检查一下是不是需要登录或跳转。");

                    if (expectedHash.Length > 0)
                    {
                        var actual = Convert.ToHexString(await SHA256.HashDataAsync(
                            File.OpenRead(tempPath), token).ConfigureAwait(false));
                        if (!string.Equals(actual, expectedHash.Replace("-", "").Trim(),
                                StringComparison.OrdinalIgnoreCase))
                            return McpResults.Error(
                                $"SHA-256 不匹配：期望 {expectedHash}，实际 {actual}。",
                                "文件可能不完整或被替换，已丢弃。");
                    }

                    if (!args.Bool("enable", true)) destination += ModDirectoryScanner.DisabledSuffix;
                    File.Move(tempPath, destination);
                }
                catch (OperationCanceledException)
                {
                    return McpResults.Error("下载已取消或超时。");
                }
                catch (Exception ex)
                {
                    return McpResults.Error(
                        $"安装失败：{ex.Message}",
                        "检查网络与链接是否可访问；游戏运行中会占用 mods 目录。");
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch (Exception)
                    {
                        // 临时文件删不掉不影响结果
                    }
                }

                var metadata = ModJarReader.Read(destination);

                return McpResults.Ok(
                    ("instance", loc.Name),
                    ("fileName", Path.GetFileName(destination)),
                    ("path", destination),
                    ("enabled", !destination.EndsWith(ModDirectoryScanner.DisabledSuffix,
                        StringComparison.OrdinalIgnoreCase)),
                    ("size", InstanceAnalyzer.FormatSize(new FileInfo(destination).Length)),
                    ("modId", metadata?.ModId ?? ""),
                    ("name", metadata?.Name ?? ""),
                    ("version", metadata?.Version ?? ""),
                    ("depends", metadata?.Depends ?? []),
                    ("message", $"已安装「{metadata?.Name ?? Path.GetFileName(destination)}」。"));
            }
        });
    }

    #endregion

    #region 辅助

    private static string BackupFile(InstanceLocation location, string path, string tag)
    {
        var directory = Path.Combine(location.GameRoot, "PCL", BackupFolderName);
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, $"{tag}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
        File.Copy(path, target, overwrite: true);
        return target;
    }

    /// <summary>zip 与 jar 都以 <c>PK\x03\x04</c>（空包是 <c>PK\x05\x06</c>）开头。</summary>
    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            if (stream.Read(header) < 4) return false;
            return header[0] == 0x50 && header[1] == 0x4B
                   && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        return builder.ToString();
    }

    #endregion
}
