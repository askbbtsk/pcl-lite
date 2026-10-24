using System.IO;
using PCL.Core.Mcp.Analysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp.Tools;

/// <summary>
/// 实例相关工具：列实例、看详情、浏览目录。
/// </summary>
internal static class InstanceTools
{
    public static void Register(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_list_instances",
            Title = "列出游戏实例",
            Description =
                "列出启动器里所有 Minecraft 实例（版本），带 MC 版本、加载器、mod 数量。\n\n"
                + "任何涉及具体实例的操作之前都该先调一次：不要猜实例名，用这里返回的真实名字。\n"
                + "返回里的 current 字段是用户当前在启动器里选中的实例，多数工具不传 instance 参数时会用它。",
            InputSchema = new McpParams()
                .Bool("withModCount", "是否顺便统计每个实例的 mod 数量（会多花一点时间，实例多时建议关掉）")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                if (McpHostBridge.ListInstances is null || McpHostBridge.LocateInstance is null)
                    return Task.FromResult<object?>(McpResults.Error(McpHostBridge.RequireHostAttached()));

                var names = McpHostBridge.ListInstances();
                var withModCount = args.Bool("withModCount", true);

                var items = new List<Dictionary<string, object?>>();
                foreach (var name in names)
                {
                    var location = McpHostBridge.LocateInstance(name);
                    if (location is null) continue;

                    var item = new Dictionary<string, object?>
                    {
                        ["name"] = location.Name,
                        ["mcVersion"] = location.McVersion,
                        ["loader"] = location.Loader,
                        ["loaderVersion"] = location.LoaderVersion,
                        ["path"] = location.InstanceDirectory,
                        ["isolated"] = string.Equals(
                            Path.GetFullPath(location.InstanceDirectory).TrimEnd('\\'),
                            Path.GetFullPath(location.GameRoot).TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase)
                    };

                    if (withModCount)
                    {
                        var mods = ModDirectoryScanner.Scan(ModDirectoryScanner.Mods(location), readMetadata: false);
                        item["mods"] = mods.Count;
                        item["modsEnabled"] = mods.Count(m => m.Enabled);
                    }

                    items.Add(item);
                }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("count", items.Count),
                    ("current", McpHostBridge.GetSelectedInstance?.Invoke()),
                    ("instances", items)));
            }
        });

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_instance_info",
            Title = "实例详情",
            Description =
                "查看单个实例的完整信息：目录位置、是否版本隔离、内存分配、Java 路径、"
                + "以及 mods / resourcepacks / saves / logs / crash-reports 各目录是否存在、有多少内容。\n\n"
                + "排查「文件到底该放哪」这类问题时先看这个——版本隔离开关会让 mods 目录的位置完全不同。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                Dictionary<string, object?> Describe(string path, bool countArchives)
                {
                    var exists = Directory.Exists(path);
                    var entry = new Dictionary<string, object?>
                    {
                        ["path"] = path,
                        ["exists"] = exists
                    };
                    if (exists)
                    {
                        if (countArchives)
                        {
                            var files = ModDirectoryScanner.Scan(path, readMetadata: false);
                            entry["count"] = files.Count;
                            entry["enabled"] = files.Count(f => f.Enabled);
                        }
                        else
                        {
                            var entries = ModDirectoryScanner.ScanEntries(path);
                            entry["count"] = entries.Count;
                        }
                    }

                    return entry;
                }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("instanceDirectory", loc.InstanceDirectory),
                    ("gameRoot", loc.GameRoot),
                    ("isolated", string.Equals(
                        Path.GetFullPath(loc.InstanceDirectory).TrimEnd('\\'),
                        Path.GetFullPath(loc.GameRoot).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase)),
                    ("mcVersion", loc.McVersion),
                    ("loader", loc.Loader),
                    ("loaderVersion", loc.LoaderVersion),
                    ("javaPath", loc.JavaPath),
                    ("ramMb", loc.RamMb),
                    ("javaRecommended", InstanceAnalyzer.RecommendedJavaMajor(loc.McVersion)),
                    ("directories", new Dictionary<string, object?>
                    {
                        ["mods"] = Describe(ModDirectoryScanner.Mods(loc), true),
                        ["resourcepacks"] = Describe(ModDirectoryScanner.ResourcePacks(loc), false),
                        ["shaderpacks"] = Describe(ModDirectoryScanner.ShaderPacks(loc), false),
                        ["saves"] = Describe(ModDirectoryScanner.Saves(loc), false),
                        ["config"] = Describe(ModDirectoryScanner.Config(loc), false),
                        ["logs"] = Describe(ModDirectoryScanner.Logs(loc), false),
                        ["crash-reports"] = Describe(ModDirectoryScanner.CrashReports(loc), false)
                    })));
            }
        });

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_list_directory",
            Title = "浏览实例目录",
            Description =
                "列出实例目录下某个子目录的内容（只读）。\n\n"
                + "常用 area 取值：mods、resourcepacks、shaderpacks、saves、config、logs、crash-reports。\n"
                + "也可以传 area=\"\" 看游戏根目录下都有什么，用来确认目录结构。\n"
                + "和 pcl_list_mods 的区别：这个只看文件名和大小，不解析 jar 内容，速度快、适用于任何目录。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Str("area", "子目录名，相对游戏根目录。留空表示游戏根目录本身。")
                .Bool("recursive", "是否递归子目录，默认只列一层。")
                .Int("limit", "最多返回多少条，默认 200。", min: 1, max: 2000)
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);

                var area = args.Str("area", "");
                var target = string.IsNullOrWhiteSpace(area)
                    ? location!.GameRoot
                    : McpPath.ResolveUnder(location!.GameRoot, area);

                if (target is null)
                    return Task.FromResult<object?>(McpResults.Error(
                        $"路径「{area}」超出了游戏目录范围，已拒绝。",
                        "area 必须是游戏根目录下的相对路径。"));

                if (!Directory.Exists(target))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"目录不存在：{target}",
                        "先用 pcl_instance_info 看这个实例有哪些目录。"));

                var limit = Math.Clamp(args.Int("limit", 200), 1, 2000);
                var recursive = args.Bool("recursive");
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                var items = new List<Dictionary<string, object?>>();
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(target, "*", option))
                        items.Add(new Dictionary<string, object?>
                        {
                            ["name"] = Path.GetFileName(dir),
                            ["type"] = "directory",
                            ["path"] = Path.GetRelativePath(location.GameRoot, dir)
                        });

                    foreach (var file in Directory.EnumerateFiles(target, "*", option))
                    {
                        var info = new FileInfo(file);
                        items.Add(new Dictionary<string, object?>
                        {
                            ["name"] = info.Name,
                            ["type"] = "file",
                            ["sizeBytes"] = info.Length,
                            ["size"] = InstanceAnalyzer.FormatSize(info.Length),
                            ["modified"] = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            ["disabled"] = ModDirectoryScanner.IsDisabled(info.Name),
                            ["path"] = Path.GetRelativePath(location.GameRoot, info.FullName)
                        });
                    }
                }
                catch (Exception ex)
                {
                    return Task.FromResult<object?>(McpResults.Error($"读取目录失败：{ex.Message}"));
                }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("area", string.IsNullOrWhiteSpace(area) ? "." : area),
                    ("directory", target),
                    ("total", items.Count),
                    ("returned", Math.Min(items.Count, limit)),
                    ("truncated", items.Count > limit),
                    ("entries", items.Take(limit).ToList())));
            }
        });
    }
}
