using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PCL.Core.Mcp.Analysis;

namespace PCL.Core.Mcp.Tools;

/// <summary>
/// 回收站工具。
/// <para>
/// <c>pcl_delete_mod</c> 默认是软删除，把文件挪到 <c>PCL/McpTrash/&lt;时间戳&gt;/</c>。
/// 但在加这组工具之前，那个目录是个单向门：进去以后只能靠用户自己去文件管理器里拖回来，
/// 而且没有任何清理策略，会一直长。这里补上「看得到、放得回、清得掉」三件事。
/// </para>
/// </summary>
internal static class TrashTools
{
    private const string TrashFolderName = "McpTrash";

    public static void Register(McpServerCatalog catalog)
    {
        RegisterList(catalog);
        RegisterRestore(catalog);
        RegisterClean(catalog);
    }

    private static string TrashRoot(InstanceLocation location)
        => Path.Combine(location.GameRoot, "PCL", TrashFolderName);

    private static void RegisterList(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_list_trash",
            Title = "查看回收站",
            Description =
                "列出被 pcl_delete_mod 软删除的文件。\n\n"
                + "每条带：批次时间、原文件名、大小、可以传给 pcl_restore 的 trashPath。\n"
                + "删完东西想确认「到底挪走了什么」时用这个，比让用户去翻目录快。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Int("limit", "最多返回多少条，默认 200。", min: 1, max: 2000)
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var root = TrashRoot(loc);
                var limit = Math.Clamp(args.Int("limit", 200), 1, 2000);
                var items = new List<Dictionary<string, object?>>();

                if (Directory.Exists(root))
                    foreach (var batch in Directory.EnumerateDirectories(root).OrderByDescending(d => d))
                    foreach (var file in Directory.EnumerateFiles(batch))
                    {
                        var info = new FileInfo(file);
                        items.Add(new Dictionary<string, object?>
                        {
                            ["trashPath"] = Path.GetRelativePath(root, file),
                            ["fileName"] = info.Name,
                            ["batch"] = Path.GetFileName(batch),
                            ["size"] = InstanceAnalyzer.FormatSize(info.Length),
                            ["deletedAt"] = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                        });
                    }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("directory", root),
                    ("count", items.Count),
                    ("returned", Math.Min(items.Count, limit)),
                    ("truncated", items.Count > limit),
                    ("entries", items.Take(limit).ToList())));
            }
        });
    }

    private static void RegisterRestore(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_restore",
            Title = "从回收站还原",
            Description =
                "把回收站里的文件放回实例的 mods 目录。\n\n"
                + "trashPath 用 pcl_list_trash 返回的那个值，例如 `20261025-071500/sodium.jar`。\n"
                + "目标位置已有同名文件时不会覆盖，会直接报错。",
            InputSchema = new McpParams()
                .Str("trashPath", "pcl_list_trash 返回的相对路径。", required: true)
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Build(),
            Required = McpCapability.Write,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var relative = args.Str("trashPath", "").Trim();
                if (relative.Length == 0)
                    return Task.FromResult<object?>(McpResults.Error("缺少 trashPath 参数。"));

                var root = TrashRoot(loc);
                var source = McpPath.ResolveUnder(root, relative);
                if (source is null)
                    return Task.FromResult<object?>(McpResults.Error(
                        $"「{relative}」超出了回收站范围，已拒绝。",
                        "用 pcl_list_trash 取准确的 trashPath。"));
                if (!File.Exists(source))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"回收站里找不到「{relative}」。",
                        "用 pcl_list_trash 看现在还有哪些文件。"));

                var mods = ModDirectoryScanner.Mods(loc);
                var destination = Path.Combine(mods, Path.GetFileName(source));
                if (File.Exists(destination))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"mods 目录里已经有「{Path.GetFileName(source)}」了，不覆盖。",
                        "先确认是不是重复文件，必要时先 pcl_delete_mod 再还原。"));

                try
                {
                    Directory.CreateDirectory(mods);
                    File.Move(source, destination);

                    // 批次目录空了就顺手删掉，免得回收站里堆一堆空文件夹
                    var batch = Path.GetDirectoryName(source);
                    if (batch is not null && Directory.Exists(batch) && !Directory.EnumerateFileSystemEntries(batch).Any())
                        Directory.Delete(batch);

                    return Task.FromResult<object?>(McpResults.Ok(
                        ("instance", loc.Name),
                        ("fileName", Path.GetFileName(destination)),
                        ("restoredTo", destination),
                        ("message", $"已把「{Path.GetFileName(destination)}」还原到 mods 目录。")));
                }
                catch (Exception ex)
                {
                    return Task.FromResult<object?>(McpResults.Error(
                        $"还原失败：{ex.Message}",
                        "游戏运行中会占用 jar 文件，先 pcl_stop_game 再试。"));
                }
            }
        });
    }

    private static void RegisterClean(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_clean_trash",
            Title = "清理回收站",
            Description =
                "删除回收站里超过指定天数的批次，真正从磁盘删掉，不可恢复。\n\n"
                + "默认只清 30 天前的。传 keepDays=0 会清空整个回收站——"
                + "这一步不可逆，执行前务必先告诉用户要删什么、删多少。",
            InputSchema = new McpParams()
                .Int("keepDays", "保留最近多少天的内容，默认 30。0 表示全部清空。", min: 0, max: 3650)
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Build(),
            Required = McpCapability.Write,
            Destructive = true,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var root = TrashRoot(loc);
                if (!Directory.Exists(root))
                    return Task.FromResult<object?>(McpResults.Ok(
                        ("instance", loc.Name),
                        ("removedBatches", 0),
                        ("message", "回收站是空的。")));

                var keepDays = Math.Clamp(args.Int("keepDays", 30), 0, 3650);
                var threshold = DateTime.Now.AddDays(-keepDays);
                var removed = 0;
                var removedFiles = 0;

                foreach (var batch in Directory.EnumerateDirectories(root).ToList())
                {
                    // 用目录名里的时间戳判断年龄，比看修改时间可靠
                    if (keepDays > 0)
                    {
                        var name = Path.GetFileName(batch);
                        if (!DateTime.TryParseExact(name, "yyyyMMdd-HHmmss", null,
                                System.Globalization.DateTimeStyles.None, out var created))
                            continue;
                        if (created >= threshold) continue;
                    }

                    try
                    {
                        removedFiles += Directory.EnumerateFiles(batch, "*", SearchOption.AllDirectories).Count();
                        Directory.Delete(batch, true);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult<object?>(McpResults.Error($"删除「{batch}」失败：{ex.Message}"));
                    }
                }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("keepDays", keepDays),
                    ("removedBatches", removed),
                    ("removedFiles", removedFiles),
                    ("message", removed == 0
                        ? $"没有超过 {keepDays} 天的内容，回收站未变动。"
                        : $"已清理 {removed} 个批次、{removedFiles} 个文件（不可恢复）。")));
            }
        });
    }
}
