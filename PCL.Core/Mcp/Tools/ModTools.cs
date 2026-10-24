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
/// mod / jar / 资源包 / 存档的读取与整理工具。
/// <para>
/// 设计原则：所有「删除」默认走软删除（移到 PCL/McpTrash），可恢复；
/// 只有显式传 hard=true 才真正删文件。模型乱删东西的代价太高了。
/// </para>
/// </summary>
internal static class ModTools
{
    private const string TrashFolderName = "McpTrash";

    public static void Register(McpServerCatalog catalog)
    {
        RegisterListMods(catalog);
        RegisterModInfo(catalog);
        RegisterToggle(catalog, disable: true);
        RegisterToggle(catalog, disable: false);
        RegisterMoveMod(catalog);
        RegisterDeleteMod(catalog);
        RegisterListResourcePacks(catalog);
        RegisterListSaves(catalog);
    }

    #region 列表与详情

    private static void RegisterListMods(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_list_mods",
            Title = "列出 mod",
            Description =
                "列出实例 mods 目录里的所有 mod，默认包含已禁用的。\n\n"
                + "每条带：文件名、是否启用、大小、modId、显示名、版本、加载器、声明的 MC 版本、依赖。\n"
                + "整理 mod 的第一步永远是这里——先看清有什么，再决定动哪个。\n\n"
                + "注意：mod 的启用/禁用是靠文件名后缀 `.disabled` 实现的，返回里的 enabled 字段已经帮你判断好了。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Bool("includeDisabled", "是否包含已禁用的 mod，默认包含。")
                .Bool("readMetadata", "是否打开 jar 读元数据（modId/版本/依赖）。关掉只列文件名，速度快很多。")
                .Str("filter", "按关键词过滤，匹配文件名、modId 或显示名。")
                .Int("limit", "最多返回多少条，默认 300。", min: 1, max: 5000)
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var includeDisabled = args.Bool("includeDisabled", true);
                var readMetadata = args.Bool("readMetadata", true);
                var filter = args.Str("filter", "");
                var limit = Math.Clamp(args.Int("limit", 300), 1, 5000);

                var directory = ModDirectoryScanner.Mods(loc);
                var mods = ModDirectoryScanner.Scan(directory, readMetadata);

                if (!includeDisabled) mods = mods.Where(m => m.Enabled).ToList();

                if (!string.IsNullOrWhiteSpace(filter))
                    mods = mods.Where(m =>
                            m.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || (m.Metadata?.ModId.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                            || (m.Metadata?.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                        .ToList();

                var items = mods.Take(limit).Select(m => new Dictionary<string, object?>
                {
                    ["fileName"] = m.FileName,
                    ["enabled"] = m.Enabled,
                    ["size"] = InstanceAnalyzer.FormatSize(m.SizeBytes),
                    ["modified"] = m.Modified.ToString("yyyy-MM-dd HH:mm"),
                    ["modId"] = m.Metadata?.ModId ?? "",
                    ["name"] = m.Metadata?.Name ?? "",
                    ["version"] = m.Metadata?.Version ?? "",
                    ["loader"] = m.Metadata?.Loader ?? "",
                    ["mcVersion"] = m.Metadata?.McVersion ?? "",
                    ["depends"] = m.Metadata?.Depends ?? []
                }).ToList();

                var duplicateIds = mods
                    .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Identity))
                    .GroupBy(m => m.Identity, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("directory", directory),
                    ("directoryExists", Directory.Exists(directory)),
                    ("total", mods.Count),
                    ("enabled", mods.Count(m => m.Enabled)),
                    ("disabled", mods.Count(m => !m.Enabled)),
                    ("returned", items.Count),
                    ("truncated", mods.Count > items.Count),
                    ("duplicateModIds", duplicateIds),
                    ("mods", items)));
            }
        });
    }

    private static void RegisterModInfo(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_mod_info",
            Title = "查看单个 mod 详情",
            Description =
                "读取单个 jar 的完整元数据：modId、显示名、版本、作者、描述、入口点、依赖、不兼容项。\n\n"
                + "用来回答「这个 mod 是干嘛的」「它需要什么前置」「它跟哪个版本冲突」这类问题。\n"
                + "fileName 可以带也可以不带 `.disabled` 后缀，两种都能匹配到。",
            InputSchema = new McpParams()
                .Str("fileName", "jar 文件名，例如 sodium-0.5.8.jar。", required: true)
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var fileName = McpPath.FileNameOnly(args.Str("fileName"));
                if (fileName.Length == 0)
                    return Task.FromResult<object?>(McpResults.Error("缺少 fileName 参数。"));

                var directory = ModDirectoryScanner.Mods(loc);
                var mods = ModDirectoryScanner.Scan(directory, readMetadata: false);
                var match = FindMod(mods, fileName);
                if (match is null)
                    return Task.FromResult<object?>(McpResults.Error(
                        $"在 {loc.Name} 的 mods 目录里找不到「{fileName}」。",
                        "用 pcl_list_mods 看真实文件名。"));

                var metadata = ModJarReader.Read(match.FullPath);

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("fileName", match.FileName),
                    ("fullPath", match.FullPath),
                    ("enabled", match.Enabled),
                    ("size", InstanceAnalyzer.FormatSize(match.SizeBytes)),
                    ("modified", match.Modified.ToString("yyyy-MM-dd HH:mm:ss")),
                    ("modId", metadata?.ModId ?? ""),
                    ("name", metadata?.Name ?? ""),
                    ("version", metadata?.Version ?? ""),
                    ("description", metadata?.Description ?? ""),
                    ("authors", metadata?.Authors ?? ""),
                    ("loader", metadata?.Loader ?? "Unknown"),
                    ("mcVersion", metadata?.McVersion ?? ""),
                    ("entry", metadata?.Entry ?? ""),
                    ("depends", metadata?.Depends ?? []),
                    ("breaks", metadata?.Breaks ?? []),
                    ("metadataSource", metadata?.Source ?? ""),
                    ("parsed", metadata is not null)));
            }
        });
    }

    #endregion

    #region 启用 / 禁用

    private static void RegisterToggle(McpServerCatalog catalog, bool disable)
    {
        var name = disable ? "pcl_disable_mod" : "pcl_enable_mod";
        var title = disable ? "禁用 mod" : "启用 mod";

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = name,
            Title = title,
            Description = disable
                ? "禁用一个 mod：把 `xxx.jar` 重命名为 `xxx.jar.disabled`。\n\n"
                  + "只是改名，不删文件，随时可以用 pcl_enable_mod 恢复。\n"
                  + "排查 mod 冲突时这就是最常用的操作——二分法禁用一半，看还崩不崩。\n\n"
                  + "一次只能处理一个文件；要批量禁用请多次调用，每次都在结果里确认 ok。"
                : "启用一个 mod：把 `xxx.jar.disabled` 改回 `xxx.jar`。\n\n"
                  + "如果目标文件名已经被占用（说明同一个 mod 有两份），会直接报错而不会覆盖。",
            InputSchema = new McpParams()
                .Str("fileName", "jar 文件名。禁用时可带可不带 .disabled；启用时必须指向那个已禁用的文件。",
                    required: true)
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Build(),
            Required = McpCapability.Write,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var fileName = McpPath.FileNameOnly(args.Str("fileName"));
                if (fileName.Length == 0)
                    return Task.FromResult<object?>(McpResults.Error("缺少 fileName 参数。"));

                var directory = ModDirectoryScanner.Mods(loc);
                var mods = ModDirectoryScanner.Scan(directory, readMetadata: false);
                var match = FindMod(mods, fileName);
                if (match is null)
                    return Task.FromResult<object?>(McpResults.Error(
                        $"找不到「{fileName}」。",
                        "用 pcl_list_mods 看真实文件名。"));

                if (disable == !match.Enabled)
                    return Task.FromResult<object?>(McpResults.Ok(
                        ("fileName", match.FileName),
                        ("enabled", match.Enabled),
                        ("changed", false),
                        ("message", $"「{match.FileName}」已经处于{(disable ? "禁用" : "启用")}状态，无需操作。")));

                var targetPath = disable
                    ? match.FullPath + ModDirectoryScanner.DisabledSuffix
                    : ModDirectoryScanner.StripDisabledSuffix(match.FullPath);

                if (!disable && File.Exists(targetPath))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"无法启用：目标文件「{Path.GetFileName(targetPath)}」已经存在，说明同一个 mod 有两份。",
                        "先用 pcl_analyze_instance 看重复项，删掉多余的那份再启用。"));

                try
                {
                    File.Move(match.FullPath, targetPath);
                }
                catch (Exception ex)
                {
                    return Task.FromResult<object?>(McpResults.Error(
                        $"改名失败：{ex.Message}",
                        "游戏正在运行时会占用 jar 文件，先 pcl_stop_game 再试。"));
                }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("fileName", Path.GetFileName(targetPath)),
                    ("enabled", !disable),
                    ("changed", true),
                    ("message", $"已{(disable ? "禁用" : "启用")}「{match.FileName}」。")));
            }
        });
    }

    #endregion

    #region 移动

    private static void RegisterMoveMod(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_move_mod",
            Title = "移动 mod",
            Description =
                "把 mod 文件移到别的地方。两种用法：\n"
                + "1. 传 targetInstance：移到另一个实例的 mods 目录（跨实例共享 mod 时用）。\n"
                + "2. 传 targetDirectory：移到当前游戏根目录下的相对目录，例如 \"mods-backup\"。\n\n"
                + "两个都不传会报错。目标已存在同名文件时不会覆盖，直接失败。\n"
                + "移动不改变启用状态（`.disabled` 后缀会跟着走）。",
            InputSchema = new McpParams()
                .Str("fileName", "要移动的 jar 文件名。", required: true)
                .Str("instance", "源实例名。留空使用当前选中实例。")
                .Str("targetInstance", "目标实例名。会移到该实例的 mods 目录。")
                .Str("targetDirectory", "目标目录，相对当前实例的游戏根目录，例如 mods-backup。")
                .Build(),
            Required = McpCapability.Write,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var fileName = McpPath.FileNameOnly(args.Str("fileName"));
                if (fileName.Length == 0)
                    return Task.FromResult<object?>(McpResults.Error("缺少 fileName 参数。"));

                var targetInstance = args.Str("targetInstance");
                var targetDirectory = args.Str("targetDirectory");

                if (string.IsNullOrWhiteSpace(targetInstance) && string.IsNullOrWhiteSpace(targetDirectory))
                    return Task.FromResult<object?>(McpResults.Error(
                        "必须指定 targetInstance 或 targetDirectory 其中之一。",
                        "移到别的实例用 targetInstance；移到备份目录用 targetDirectory。"));

                var mods = ModDirectoryScanner.Scan(ModDirectoryScanner.Mods(loc), readMetadata: false);
                var match = FindMod(mods, fileName);
                if (match is null)
                    return Task.FromResult<object?>(McpResults.Error(
                        $"在 {loc.Name} 里找不到「{fileName}」。",
                        "用 pcl_list_mods 看真实文件名。"));

                string destinationDirectory;
                string destinationLabel;
                if (!string.IsNullOrWhiteSpace(targetInstance))
                {
                    var (targetLocation, targetError) = McpInstanceResolver.Resolve(targetInstance);
                    if (targetError is not null) return Task.FromResult<object?>(targetError);
                    destinationDirectory = ModDirectoryScanner.Mods(targetLocation!);
                    destinationLabel = $"实例 {targetLocation!.Name}";
                }
                else
                {
                    var resolved = McpPath.ResolveUnder(loc.GameRoot, targetDirectory);
                    if (resolved is null)
                        return Task.FromResult<object?>(McpResults.Error(
                            $"目标目录「{targetDirectory}」超出了游戏目录范围，已拒绝。"));
                    destinationDirectory = resolved!;
                    destinationLabel = targetDirectory!;
                }

                var destination = Path.Combine(destinationDirectory, match.FileName);
                if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(match.FullPath),
                        StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<object?>(McpResults.Error("源和目标是同一个文件。"));

                if (File.Exists(destination))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"目标位置已经有同名文件：{destination}",
                        "先用 pcl_list_mods 确认目标实例里是不是已经有这个 mod 了。"));

                try
                {
                    Directory.CreateDirectory(destinationDirectory);
                    File.Move(match.FullPath, destination);
                }
                catch (Exception ex)
                {
                    return Task.FromResult<object?>(McpResults.Error(
                        $"移动失败：{ex.Message}",
                        "游戏运行中会占用 jar 文件，先 pcl_stop_game 再试。"));
                }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("fileName", match.FileName),
                    ("from", match.FullPath),
                    ("to", destination),
                    ("target", destinationLabel),
                    ("message", $"已把「{match.FileName}」移动到 {destinationLabel}。")));
            }
        });
    }

    #endregion

    #region 删除

    private static void RegisterDeleteMod(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_delete_mod",
            Title = "删除 mod",
            Description =
                "删除一个 mod 文件。默认是软删除：文件被移到游戏目录下的 `PCL/McpTrash/<时间戳>/`，"
                + "用户随时可以手动恢复。\n\n"
                + "只有传 hard=true 才真正从磁盘删除，且不可恢复。\n\n"
                + "如果只是想临时排除一个 mod，用 pcl_disable_mod 更合适——它只改名，零风险。",
            InputSchema = new McpParams()
                .Str("fileName", "要删除的 jar 文件名。", required: true)
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Bool("hard", "true 表示彻底删除，不可恢复。默认 false（移到回收目录）。")
                .Build(),
            Required = McpCapability.Write,
            Destructive = true,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var fileName = McpPath.FileNameOnly(args.Str("fileName"));
                if (fileName.Length == 0)
                    return Task.FromResult<object?>(McpResults.Error("缺少 fileName 参数。"));

                var mods = ModDirectoryScanner.Scan(ModDirectoryScanner.Mods(loc), readMetadata: false);
                var match = FindMod(mods, fileName);
                if (match is null)
                    return Task.FromResult<object?>(McpResults.Error(
                        $"找不到「{fileName}」。",
                        "用 pcl_list_mods 看真实文件名。"));

                var hard = args.Bool("hard");
                try
                {
                    if (hard)
                    {
                        File.Delete(match.FullPath);
                        return Task.FromResult<object?>(McpResults.Ok(
                            ("fileName", match.FileName),
                            ("hard", true),
                            ("message", $"已彻底删除「{match.FileName}」（不可恢复）。")));
                    }

                    var trashRoot = Path.Combine(loc.GameRoot, "PCL", TrashFolderName,
                        DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.CreateDirectory(trashRoot);
                    var destination = Path.Combine(trashRoot, match.FileName);
                    File.Move(match.FullPath, destination);

                    return Task.FromResult<object?>(McpResults.Ok(
                        ("fileName", match.FileName),
                        ("hard", false),
                        ("trashPath", destination),
                        ("message", $"已把「{match.FileName}」移到回收目录，需要时可以手动移回来。")));
                }
                catch (Exception ex)
                {
                    return Task.FromResult<object?>(McpResults.Error(
                        $"删除失败：{ex.Message}",
                        "游戏运行中会占用 jar 文件，先 pcl_stop_game 再试。"));
                }
            }
        });
    }

    #endregion

    #region 资源包与存档

    private static void RegisterListResourcePacks(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_list_resourcepacks",
            Title = "列出资源包与光影",
            Description =
                "列出实例的 resourcepacks（资源包）和 shaderpacks（光影）目录内容。\n\n"
                + "注意：这两类文件的「启用状态」由 Minecraft 自己的 options.txt 控制，"
                + "不是靠文件名后缀，所以这里只能列出有哪些，无法从这里改启用状态。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                List<Dictionary<string, object?>> Describe(string directory)
                {
                    if (!Directory.Exists(directory)) return [];
                    return ModDirectoryScanner.ScanEntries(directory).Select(e => new Dictionary<string, object?>
                    {
                        ["name"] = e.Name,
                        ["type"] = e.IsDirectory ? "directory" : "file",
                        ["size"] = e.IsDirectory ? "" : InstanceAnalyzer.FormatSize(e.Size),
                        ["modified"] = e.Modified.ToString("yyyy-MM-dd HH:mm")
                    }).ToList();
                }

                var packs = Describe(ModDirectoryScanner.ResourcePacks(loc));
                var shaders = Describe(ModDirectoryScanner.ShaderPacks(loc));

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("resourcepacks", packs),
                    ("shaderpacks", shaders),
                    ("resourcepackCount", packs.Count),
                    ("shaderpackCount", shaders.Count)));
            }
        });
    }

    private static void RegisterListSaves(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_list_saves",
            Title = "列出存档",
            Description =
                "列出实例的 saves（存档）目录，每个存档带大小和最后修改时间。\n\n"
                + "开了版本隔离时每个实例有自己的存档；没开时所有实例共用 .minecraft/saves。"
                + "返回里的 gameRoot 就是实际生效的位置，用它来判断属于哪种情况。",
            InputSchema = new McpParams()
                .Str("instance", "实例名。留空使用当前选中实例。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var (location, error) = McpInstanceResolver.Resolve(args.Str("instance"));
                if (error is not null) return Task.FromResult<object?>(error);
                var loc = location!;

                var directory = ModDirectoryScanner.Saves(loc);
                var saves = new List<Dictionary<string, object?>>();

                if (Directory.Exists(directory))
                    foreach (var entry in ModDirectoryScanner.ScanEntries(directory).Where(e => e.IsDirectory))
                    {
                        var size = 0L;
                        try
                        {
                            size = new DirectoryInfo(entry.FullPath)
                                .EnumerateFiles("*", SearchOption.AllDirectories)
                                .Sum(f => f.Length);
                        }
                        catch (Exception)
                        {
                            // 存档里有不可读文件时忽略，不影响列表
                        }

                        saves.Add(new Dictionary<string, object?>
                        {
                            ["name"] = entry.Name,
                            ["size"] = InstanceAnalyzer.FormatSize(size),
                            ["modified"] = entry.Modified.ToString("yyyy-MM-dd HH:mm"),
                            ["path"] = entry.FullPath
                        });
                    }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("instance", loc.Name),
                    ("gameRoot", loc.GameRoot),
                    ("directory", directory),
                    ("count", saves.Count),
                    ("saves", saves.OrderByDescending(s => s["modified"]?.ToString()).ToList())));
            }
        });
    }

    #endregion

    #region 辅助

    /// <summary>
    /// 在扫描结果里找一个文件。允许传带 / 不带 <c>.disabled</c> 的名字，
    /// 也允许只传 modId 或部分文件名——模型经常拿不到完整文件名。
    /// </summary>
    private static ModFileEntry? FindMod(List<ModFileEntry> mods, string fileName)
    {
        var exact = mods.FirstOrDefault(m =>
            string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var stripped = ModDirectoryScanner.StripDisabledSuffix(fileName);
        var byStem = mods.FirstOrDefault(m =>
            string.Equals(ModDirectoryScanner.StripDisabledSuffix(m.FileName), stripped,
                StringComparison.OrdinalIgnoreCase));
        if (byStem is not null) return byStem;

        var byContains = mods
            .Where(m => ModDirectoryScanner.StripDisabledSuffix(m.FileName)
                .Contains(stripped, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byContains.Count == 1) return byContains[0];

        return null;
    }

    #endregion
}
