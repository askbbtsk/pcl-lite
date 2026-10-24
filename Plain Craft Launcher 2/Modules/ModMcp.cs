using System.Diagnostics;
using System.IO;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.Mcp;

namespace PCL;

/// <summary>
/// 把启动器的内部能力接到内置 MCP 服务上。
/// <para>
/// PCL.Core 里的 MCP 框架不引用 WPF，所以「列实例、解析路径、启动游戏」这些
/// 需要运行时状态的活由这里注入。这样一来 MCP 服务本身可以被单独测试。
/// </para>
/// </summary>
public static class ModMcp
{
    private static bool _initialized;

    /// <summary>在启动器初始化阶段调用一次。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        McpHostBridge.ListInstances = ListInstanceNames;
        McpHostBridge.GetSelectedInstance = () => ModInstanceList.McMcInstanceSelected?.Name;
        McpHostBridge.LocateInstance = LocateInstance;
        McpHostBridge.LaunchGameAsync = LaunchGameAsync;
        McpHostBridge.StopGameAsync = StopGameAsync;
        McpHostBridge.GetGameState = GetGameState;
        McpHostBridge.InvokeOnUiAsync = InvokeOnUiAsync;

        // 开关、端口、口令、权限任何一项变化都要重新应用一遍
        Config.Mcp.EnabledConfig.Observe(new ConfigObserver(ConfigEvent.Changed, _ => ApplyAsync()));
        Config.Mcp.PortConfig.Observe(new ConfigObserver(ConfigEvent.Changed, _ => ApplyAsync()));

        ApplyAsync();
    }

    private static void ApplyAsync()
    {
        ModBase.RunInNewThread(() =>
        {
            try
            {
                McpServerService.ApplyFromConfig();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[MCP] 应用配置失败");
            }
        }, "MCP 服务");
    }

    #region 实例

    /// <summary>
    /// 列出全部实例名。
    /// 实例列表还没加载完时直接扫 versions 目录，避免 MCP 在启动早期拿到空列表。
    /// </summary>
    private static IReadOnlyList<string> ListInstanceNames()
    {
        var fromList = ModInstanceList.mcInstanceList.Values
            .SelectMany(list => list)
            .Select(instance => instance.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fromList.Count > 0) return fromList;

        try
        {
            var versions = Path.Combine(ModFolder.mcFolderSelected ?? "", "versions");
            if (!Directory.Exists(versions)) return [];
            return Directory.EnumerateDirectories(versions)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static McInstance? FindInstance(string name)
    {
        var match = ModInstanceList.mcInstanceList.Values
            .SelectMany(list => list)
            .FirstOrDefault(instance => string.Equals(instance.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        // 列表里没有时按名字兜底：只要 versions 下真有这个目录就当它存在
        try
        {
            var path = Path.Combine(ModFolder.mcFolderSelected ?? "", "versions", name);
            if (Directory.Exists(path)) return new McInstance(name);
        }
        catch (Exception)
        {
            // 目录名非法等情况直接当作找不到
        }

        return null;
    }

    private static InstanceLocation? LocateInstance(string name)
    {
        return OnUiThread(() =>
        {
            var instance = FindInstance(name);
            if (instance is null) return null;

            try
            {
                if (!instance.IsLoaded) instance.Load();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"[MCP] 读取实例 {name} 失败");
            }

            return BuildLocation(instance);
        });
    }

    private static InstanceLocation BuildLocation(McInstance instance)
    {
        var info = instance.Info;
        string loader, loaderVersion;

        if (info.HasFabric)
        {
            loader = "Fabric";
            loaderVersion = info.Fabric;
        }
        else if (info.HasQuilt)
        {
            loader = "Quilt";
            loaderVersion = info.Quilt;
        }
        else if (info.HasNeoForge)
        {
            loader = "NeoForge";
            loaderVersion = info.NeoForge;
        }
        else if (info.HasForge)
        {
            loader = "Forge";
            loaderVersion = info.Forge;
        }
        else if (info.HasCleanroom)
        {
            loader = "Cleanroom";
            loaderVersion = info.Cleanroom;
        }
        else if (info.HasLiteLoader)
        {
            loader = "LiteLoader";
            loaderVersion = "";
        }
        else
        {
            loader = "Vanilla";
            loaderVersion = "";
        }

        return new InstanceLocation(
            instance.Name,
            instance.PathInstance,
            instance.PathIndie,
            info.VanillaName ?? "",
            loader,
            loaderVersion ?? "",
            Config.Launch.SelectedJava ?? "",
            GetInstanceRamMb(instance));
    }

    private static int GetInstanceRamMb(McInstance instance)
    {
        try
        {
            var tick = Config.Instance.CustomMemorySize[instance.PathInstance];
            var gigabyte = PageSetupLaunch.GetRamFromTick(tick);
            return gigabyte > 0 ? (int)Math.Round(gigabyte * 1024d) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    #endregion

    #region 游戏控制

    private static async Task<object?> LaunchGameAsync(string? instanceName, IReadOnlyList<string> extraArgs)
    {
        // 额外参数会被拼进 Java 命令行，先在协议层挡掉能在 JVM 里加载外部代码的那类
        if (!McpLaunchGuards.TryValidate(extraArgs, out var guardError))
            return new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["error"] = guardError,
                ["hint"] = "去掉这些参数再试。需要自定义 JVM 参数时，请让用户在「设置 → 启动 → 高级」里添加。"
            };

        var (started, payload) = OnUiThread(() =>
        {
            var instance = string.IsNullOrWhiteSpace(instanceName)
                ? ModInstanceList.McMcInstanceSelected
                : FindInstance(instanceName);

            if (instance is null)
                return (false, (object?)new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = string.IsNullOrWhiteSpace(instanceName)
                        ? "启动器当前没有选中实例。"
                        : $"找不到实例「{instanceName}」。"
                });

            // 只看加载器状态，不看 isLaunching：后者在启动失败后可能不会被重置，
            // 会让 MCP 误以为「永远正在启动」而拒绝后续调用。
            if (ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading)
                return (false, (object?)new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "游戏正在启动中，不能重复触发。",
                    ["hint"] = "等当前这次启动结束，或者用 pcl_game_status 看进度。"
                });

            var options = new ModLaunch.McLaunchOptions { instance = instance };
            if (extraArgs.Count > 0) options.ExtraArgs.AddRange(extraArgs);

            if (!ModLaunch.McLaunchStart(options))
                return (false, (object?)new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "启动器拒绝了这次启动，通常是实例文件缺失或 Java 不可用。",
                    ["hint"] = "用 pcl_analyze_instance 检查实例；或者 pcl_log_tail 看启动器日志。"
                });

            return (true, (object?)new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["instance"] = instance.Name,
                ["mcVersion"] = instance.Info.VanillaName,
                ["message"] = "启动流程已开始。等 10~20 秒后用 pcl_game_status 确认是否真的起来了。"
            });
        });

        if (!started || payload is null) return payload;

        // 前置检查（没登录档案、Java 缺失之类）会在几十毫秒内把加载器打成 Failed。
        // 观察一小会儿，把这种「秒失败」直接转成错误返回，省得模型以为启动成功了。
        for (var i = 0; i < 15; i++)
        {
            await Task.Delay(200).ConfigureAwait(false);
            var state = OnUiThread(() => ModLaunch.mcLaunchLoader.State);
            if (state == ModBase.LoadState.Failed)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "启动流程在准备阶段就失败了。",
                    ["loaderState"] = state.ToString(),
                    ["hint"] = "调 pcl_log_tail（lines=40）看具体原因，常见的是没选游戏档案、Java 路径无效、实例文件缺失。"
                };
            if (state != ModBase.LoadState.Loading) break;
        }

        return payload;
    }

    private static Task<object?> StopGameAsync()
    {
        var result = OnUiThread<object?>(() =>
        {
            var process = ModLaunch.mcLaunchProcess;

            if (ModLaunch.isLaunching && (process is null || process.HasExited))
            {
                ModLaunch.mcLaunchLoader.Abort();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["message"] = "已取消正在进行的启动流程。"
                };
            }

            if (process is null || process.HasExited)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["changed"] = false,
                    ["message"] = "游戏当前没有在运行。"
                };

            var pid = process.Id;
            try
            {
                process.Kill();
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"结束进程失败：{ex.Message}"
                };
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["changed"] = true,
                ["pid"] = pid,
                ["message"] = "已强制结束游戏进程。注意这不会保存世界。"
            };
        });

        return Task.FromResult(result);
    }

    private static object? GetGameState()
    {
        return OnUiThread<object?>(() =>
        {
            var process = ModLaunch.mcLaunchProcess;
            var running = false;
            var pid = 0;

            if (process is not null)
                try
                {
                    pid = process.Id;
                    running = !process.HasExited;
                }
                catch (Exception)
                {
                    running = false;
                }

            return new Dictionary<string, object?>
            {
                ["running"] = running,
                ["pid"] = pid > 0 ? pid : null,
                ["launching"] = ModLaunch.mcLaunchLoader.State == ModBase.LoadState.Loading,
                ["loaderState"] = ModLaunch.mcLaunchLoader.State.ToString(),
                ["instance"] = ModLaunch.currentLaunchOptions?.instance?.Name
                               ?? ModInstanceList.McMcInstanceSelected?.Name,
                ["javaPath"] = Config.Launch.SelectedJava,
                ["hint"] = ModLaunch.mcLaunchLoader.State switch
                {
                    ModBase.LoadState.Failed => "上一次启动失败了，用 pcl_log_tail 看原因。",
                    ModBase.LoadState.Finished when !running => "启动流程已结束，但游戏进程不在了——可能刚退出，也可能闪退了。",
                    _ => null
                }
            };
        });
    }

    #endregion

    #region 线程

    /// <summary>等待 UI 线程的上限。启动器正在下载/安装时 UI 线程会被长时间占用。</summary>
    private static readonly TimeSpan UiThreadTimeout = TimeSpan.FromSeconds(20);

    private static T OnUiThread<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) return func();

        // Dispatcher.Invoke 会一直等下去：启动器正在下载或安装时，所有 MCP 调用会一起挂死，
        // 客户端那边只能超时，而模型看不到任何原因。这里给个明确的超时。
        var operation = dispatcher.InvokeAsync(func);
        if (!operation.Task.Wait(UiThreadTimeout))
            throw new TimeoutException(
                $"启动器界面线程忙（可能正在下载或安装），{UiThreadTimeout.TotalSeconds:0} 秒内没有响应。请稍后重试。");

        return operation.Task.Result;
    }

    private static Task<object?> InvokeOnUiAsync(Func<object?> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return Task.FromResult(func());

        return dispatcher.InvokeAsync(func).Task.ContinueWith(
            t => t.IsFaulted ? (object?)new { ok = false, error = t.Exception?.GetBaseException().Message } : t.Result,
            TaskScheduler.Default);
    }

    #endregion
}
