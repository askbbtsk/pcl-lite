using System.Globalization;
using System.Text.RegularExpressions;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Mcp.Tools;

/// <summary>
/// 启动器自身的信息、日志与配置。
/// </summary>
internal static class LauncherTools
{
    public static void Register(McpServerCatalog catalog)
    {
        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_launcher_info",
            Title = "启动器信息",
            Description =
                "返回启动器的版本、可执行文件路径、日志目录、配置路径、当前选中实例。\n\n"
                + "排查「我在改的到底是哪份配置」时用——Debug / Release / 打包版的数据目录不是同一个。",
            InputSchema = new McpParams().Build(),
            Required = McpCapability.Read,
            Handler = (_, _) => Task.FromResult<object?>(McpResults.Ok(
                ("version", Basics.VersionName),
                ("versionCode", Basics.VersionCode),
                ("branch", Basics.VersionBranch),
                ("executable", Basics.ExecutablePath),
                ("workingDirectory", Basics.ExecutableDirectory),
                ("logDirectory", Path.Combine(Basics.ExecutableDirectory, "PCL", "Log")),
                ("currentInstance", McpHostBridge.GetSelectedInstance?.Invoke()),
                ("mcp", new Dictionary<string, object?>
                {
                    ["running"] = McpServerService.IsRunning,
                    ["port"] = McpServerService.Snapshot().Port,
                    ["endpoint"] = McpServerService.EndpointUrl(McpServerService.Snapshot())
                })))
        });

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_log_tail",
            Title = "读取启动器日志",
            Description =
                "读启动器自身日志的末尾若干行（不是游戏日志）。\n\n"
                + "启动失败、界面报错、下载失败这类问题的线索都在这里。"
                + "日志格式是 `[时间] [级别] [线程] [模块] 正文`，可以用 level 只留警告以上，"
                + "也可以用 filter 按关键词筛（模块名、异常类型都行）。",
            InputSchema = new McpParams()
                .Int("lines", "读最后多少行，默认 60，最大 2000。", min: 1, max: 2000)
                .Str("level", "级别下限：INF / WRN / ERR / FTL。默认全部。",
                    enums: ["INF", "WRN", "ERR", "FTL"])
                .Str("filter", "关键词过滤，匹配模块名和正文。")
                .Str("file", "指定日志文件名。留空用最新的一份。")
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var logDirectory = Path.Combine(Basics.ExecutableDirectory, "PCL", "Log");
                var path = ResolveLogFile(logDirectory, args.Str("file"));
                if (path is null)
                    return Task.FromResult<object?>(McpResults.Error(
                        $"没有在 {logDirectory} 下找到日志文件。",
                        "启动器还没产生过日志，或者这是第一次运行。"));

                var lines = ReadTailLines(path, 4000);
                var level = args.Str("level", "").ToUpperInvariant();
                var filter = args.Str("filter", "");
                var wanted = Math.Clamp(args.Int("lines", 60), 1, 2000);

                IEnumerable<string> selected = lines;
                if (level.Length > 0)
                {
                    var floor = Rank(level);
                    selected = selected.Where(l => Rank(ExtractLevel(l)) >= floor);
                }

                if (!string.IsNullOrWhiteSpace(filter))
                    selected = selected.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));

                var list = selected.ToList();
                var tail = list.TakeLast(wanted).ToList();

                return Task.FromResult<object?>(McpResults.Ok(
                    ("file", Path.GetFileName(path)),
                    ("path", path),
                    ("totalLines", lines.Count),
                    ("matchedLines", list.Count),
                    ("returned", tail.Count),
                    ("truncated", list.Count > tail.Count),
                    ("lines", tail)));
            }
        });

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_config_get",
            Title = "读取启动器配置",
            Description =
                "按配置键读取启动器的一项设置。\n\n"
                + "配置键是源码里 `[ConfigItem<T>(\"Key\")]` 括号里那个名字，"
                + "例如 `UiDarkMode`、`SystemMaxLog`、`LaunchRamType`。\n"
                + "不确定键名时先调 pcl_config_list 看全部可用键。",
            InputSchema = new McpParams()
                .Str("key", "配置键名。", required: true)
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var key = args.Str("key", "").Trim();
                if (key.Length == 0) return Task.FromResult<object?>(McpResults.Error("缺少 key 参数。"));

                if (!ConfigService.TryGetConfigItemNoType(key, out var item))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"配置键「{key}」不存在。",
                        "调 pcl_config_list 看所有可用键名。"));

                object? value;
                try
                {
                    value = item.GetValueNoType();
                }
                catch (Exception ex)
                {
                    return Task.FromResult<object?>(McpResults.Error($"读取失败：{ex.Message}"));
                }

                return Task.FromResult<object?>(McpResults.Ok(
                    ("key", key),
                    ("value", value),
                    ("type", item.Type.Name),
                    ("source", item.Source.ToString())));
            }
        });

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_config_list",
            Title = "列出启动器配置键",
            Description =
                "列出所有可读写的配置键。可以按前缀过滤，例如 `Ui`、`System`、`Launch`、`Mcp`。\n\n"
                + "返回的键可以直接拿去调 pcl_config_get / pcl_config_set。",
            InputSchema = new McpParams()
                .Str("prefix", "按键名前缀过滤，不区分大小写。留空返回全部。")
                .Int("limit", "最多返回多少条，默认 300。", min: 1, max: 3000)
                .Build(),
            Required = McpCapability.Read,
            Handler = (args, _) =>
            {
                var prefix = args.Str("prefix", "").Trim();
                var limit = Math.Clamp(args.Int("limit", 300), 1, 3000);

                var keys = ConfigService.KeySet
                    .Where(k => prefix.Length == 0 || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Task.FromResult<object?>(McpResults.Ok(
                    ("prefix", prefix),
                    ("total", keys.Count),
                    ("returned", Math.Min(keys.Count, limit)),
                    ("keys", keys.Take(limit).ToList())));
            }
        });

        catalog.Tools.Register(new McpToolDefinition
        {
            Name = "pcl_config_set",
            Title = "修改启动器配置",
            Description =
                "修改启动器的一项设置。改完会重新读一次确认真的写进去了。\n\n"
                + "支持的类型：布尔（true/false）、整数、字符串、枚举名。\n"
                + "注意这个工具受「允许修改启动器设置」开关控制，默认是关的——"
                + "用户需要在设置里显式打开才能用。\n\n"
                + "改之前建议先 pcl_config_get 看当前值，改完把前后值都告诉用户。",
            InputSchema = new McpParams()
                .Str("key", "配置键名。", required: true)
                .Str("value", "新值。布尔写 true/false，数字直接写，字符串照写。", required: true)
                .Build(),
            Required = McpCapability.LauncherConfig,
            Handler = (args, _) =>
            {
                var key = args.Str("key", "").Trim();
                var raw = args.Str("value");
                if (key.Length == 0) return Task.FromResult<object?>(McpResults.Error("缺少 key 参数。"));
                if (raw is null) return Task.FromResult<object?>(McpResults.Error("缺少 value 参数。"));

                if (!ConfigService.TryGetConfigItemNoType(key, out var item))
                    return Task.FromResult<object?>(McpResults.Error(
                        $"配置键「{key}」不存在。",
                        "调 pcl_config_list 看所有可用键名。"));

                object? before;
                try
                {
                    before = item.GetValueNoType();
                }
                catch (Exception)
                {
                    before = null;
                }

                if (!TryConvert(raw, item.Type, out var converted, out var convertError))
                    return Task.FromResult<object?>(McpResults.Error(convertError!));

                try
                {
                    ConfigService.TrySetValue(key, converted!);
                }
                catch (Exception ex)
                {
                    return Task.FromResult<object?>(McpResults.Error($"写入失败：{ex.Message}"));
                }

                object? after;
                try
                {
                    after = item.GetValueNoType();
                }
                catch (Exception)
                {
                    after = null;
                }

                var changed = !Equals(before?.ToString(), after?.ToString());
                return Task.FromResult<object?>(McpResults.Ok(
                    ("key", key),
                    ("before", before),
                    ("after", after),
                    ("changed", changed),
                    ("message", changed
                        ? $"「{key}」已从 {before} 改为 {after}。"
                        : $"写入没有生效，值仍然是 {after}。该键可能来自受保护的作用域，或者需要重启启动器。")));
            }
        });
    }

    #region 辅助

    private static string? ResolveLogFile(string directory, string? fileName)
    {
        if (!Directory.Exists(directory)) return null;

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var path = McpPath.ResolveUnder(directory, fileName);
            return path is not null && File.Exists(path) ? path : null;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*.log")
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> ReadTailLines(string path, int maxLines)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var queue = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                if (queue.Count == maxLines) queue.Dequeue();
                queue.Enqueue(line);
            }

            return queue.ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string ExtractLevel(string line)
    {
        var match = Regex.Match(line, @"^\[[^\]]*\]\s*\[([A-Z]+)\]");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static int Rank(string level) => level switch
    {
        "TRACE" or "TRA" => 0,
        "DEBUG" or "DBG" => 1,
        "INFO" or "INF" => 2,
        "WARN" or "WRN" => 3,
        "ERROR" or "ERR" => 4,
        "FATAL" or "FTL" => 5,
        _ => 0
    };

    private static bool TryConvert(string raw, Type targetType, out object? value, out string? error)
    {
        value = null;
        error = null;
        var text = raw.Trim();

        if (targetType == typeof(bool))
        {
            if (bool.TryParse(text, out var b))
            {
                value = b;
                return true;
            }

            if (text is "1" or "0")
            {
                value = text == "1";
                return true;
            }

            error = "这个配置项是布尔值，请传 true 或 false。";
            return false;
        }

        if (targetType == typeof(int))
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            {
                value = i;
                return true;
            }

            error = "这个配置项是整数，请传数字。";
            return false;
        }

        if (targetType == typeof(double) || targetType == typeof(float))
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                value = targetType == typeof(float) ? (float)d : d;
                return true;
            }

            error = "这个配置项是数字，请传小数。";
            return false;
        }

        if (targetType == typeof(string))
        {
            value = raw;
            return true;
        }

        if (targetType.IsEnum)
        {
            if (Enum.TryParse(targetType, text, ignoreCase: true, out var parsed))
            {
                value = parsed;
                return true;
            }

            error = $"这个配置项是枚举 {targetType.Name}，可选值：{string.Join("、", Enum.GetNames(targetType))}。";
            return false;
        }

        value = text;
        return true;
    }

    #endregion
}
