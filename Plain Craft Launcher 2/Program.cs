using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft.Worlds;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;
using PCL.Core.Utils.OS;
using PCL.Network;
using PCL.Core.UI.Theme;

namespace PCL;

internal static class Program
{
    private static readonly object PluginInstanceListLock = new();

    /// <summary>
    /// Program startup point
    /// </summary>
    [STAThread]
    public static void Main()
    {
        if (Basics.CommandLineArguments.Contains("--console")) KernelInterop.AllocateConsole();
#if DEBUG
        if (Basics.CommandLineArguments.Contains("--debug"))
        {
            Console.WriteLine("Waiting for debugger...");
            while (!Debugger.IsAttached) Thread.Sleep(50);
        }
#endif
        Console.WriteLine("Welcome to PCL Lite!");
        _ConfigurePluginBridge();
        // 网络授权弹窗：必须早于任何联网操作注册
        PCL.Network.NetworkConsentDialog.Register();
        // 更新反代的域名清单由 UpdatesGitHubModel 持有，这里派生给授权白名单，
        // 免得反代增删时两处对不上（未登记的域名会在关掉「全部允许」后逐个弹窗）
        PCL.Core.IO.Net.Consent.NetworkPurposeCatalog.RegisterExtraHostsFromUrls(
            PCL.Core.IO.Net.Consent.NetworkPurpose.LauncherUpdate,
            UpdatesGitHubModel.DownloadProxies);
        // Preloading tasks
        ApplicationService.Loading = static () =>
        {
            var app = new Application();
            app.InitializeComponent();
            return app;
        };
        MainWindowService.Loading = static () =>
        {
            // 首次启动写入默认网络白名单（必须在任何联网操作之前，且此时配置系统已就绪）
            PCL.Core.IO.Net.Consent.NetworkConsentService.SeedDefaults();
            // Lunar 物料下载接入启动器自带下载管线：代理、HTTP 模式、限速、分段下载、
            // 统一重试全都生效，不再走 Core 里那份裸 HttpClient
            PCL.Core.App.Lunar.LunarClientService.DownloadDispatcher = static (urls, path, ct) =>
                FileDownloader.DownloadAsync(urls, path, cancellationToken: ct, enableParallelChunks: true);
            try { new RecentWorldHistory().RecoverInterruptedSessions(); }
            catch (Exception ex) { ModBase.Log(ex, "恢复上次运行中断的最近世界记录失败", ModBase.LogLevel.Hint); }
            _ = Task.Run(() =>
            {
                try
                {
                    PCL.Core.Minecraft.InstanceMigration.ModificationWorkspace.CleanupAbandoned(
                        error => ModBase.Log(error, "跳过仍在使用或无法安全清理的修改临时目录", ModBase.LogLevel.Developer));
                }
                catch (Exception error) { ModBase.Log(error, "清理修改临时目录失败", ModBase.LogLevel.Developer); }
            });
            var form = new FormMain();
            return form;
        };
        // From dotnet/wpf #2393: fix tablet devices broken on .NET Core 3.0+
        _ = Tablet.TabletDevices;
        // Start lifecycle
        Lifecycle.OnInitialize();
    }

    private static void _ConfigurePluginBridge()
    {
        PluginHostBridge.GetInstancesAsync = async cancellationToken =>
        {
            var nativeInstances = await Task.Run(() => _GetNativeInstances(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return ModBase.RunInUiWait(() => nativeInstances
                .Select(instance => new PluginGameInstance(instance.PathInstance, instance.Name,
                    instance.Info?.VanillaName ?? "Unknown", instance.PathIndie,
                    _SameMinecraftPath(instance.PathInstance, ModInstanceList.McMcInstanceSelected?.PathInstance)))
                .ToArray());
        };
        PluginHostBridge.GetSelectedInstanceAsync = async cancellationToken =>
        {
            await Task.Run(() => _GetNativeInstances(cancellationToken), cancellationToken).ConfigureAwait(false);
            return ModBase.RunInUiWait(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instance = ModInstanceList.McMcInstanceSelected;
                return instance is null ? null : new PluginGameInstance(instance.PathInstance, instance.Name,
                    instance.Info?.VanillaName ?? "Unknown", instance.PathIndie, true);
            });
        };
        PluginHostBridge.GetInstanceConfigurationAsync = async (instanceId, cancellationToken) =>
        {
            var nativeInstances = await Task.Run(() => _GetNativeInstances(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return ModBase.RunInUiWait<IReadOnlyDictionary<string, string>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instance = _FindNativeInstance(nativeInstances, instanceId);
                if (instance is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = instance.PathInstance,
                    ["name"] = instance.Name,
                    ["version"] = instance.Info?.VanillaName ?? "Unknown",
                    ["gameDirectory"] = instance.PathIndie
                };
            });
        };
        PluginHostBridge.LaunchInstanceWithOptionsAsync = async (instanceId, options, cancellationToken) =>
        {
            var nativeInstances = await Task.Run(() => _GetNativeInstances(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            var instance = _FindNativeInstance(nativeInstances, instanceId);
            if (instance is null) return false;
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var launchOptions = new ModLaunch.McLaunchOptions
                {
                    instance = instance,
                    WorldName = options.WorldName,
                    AdditionalJvmArguments = options.AdditionalJvmArguments?.ToList() ?? [],
                    Environment = options.Environment is null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(options.Environment, StringComparer.OrdinalIgnoreCase)
                };
                return ModLaunch.McLaunchStart(launchOptions);
            }).Task.ConfigureAwait(false);
        };
        PluginHostBridge.LaunchInstanceAsync = async (instanceId, cancellationToken) =>
        {
            var nativeInstances = await Task.Run(() => _GetNativeInstances(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            var instance = _FindNativeInstance(nativeInstances, instanceId);
            if (instance is null) return false;
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { instance = instance });
            }).Task.ConfigureAwait(false);
        };
        PluginHostBridge.CreateButton = (pluginId, text, onClick, style) =>
        {
            FrameworkElement Create()
            {
                var button = new MyButton
                {
                    Text = text,
                    Height = 35,
                    MinWidth = 104,
                    Padding = new Thickness(14, 0, 14, 0),
                    Margin = new Thickness(0, 0, 10, 8),
                    VerticalAlignment = VerticalAlignment.Center,
                    ColorType = style switch
                    {
                        PluginButtonStyle.Highlight => MyButton.ColorState.Highlight,
                        PluginButtonStyle.Danger => MyButton.ColorState.Red,
                        _ => MyButton.ColorState.Normal
                    }
                };
                button.Click += async (_, _) =>
                {
                    try { await onClick(); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        LogWrapper.Error(ex, "Plugin", $"插件 {pluginId} 的按钮回调失败：{text}");
                        PluginPageSupport.ShowPluginNotice(Lang.Text("Plugin.Common.ButtonFailed", text, ex.Message),
                            PluginNotificationKind.Error);
                    }
                };
                return button;
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) return Create();
            return dispatcher.Invoke(Create);
        };
        PluginHostBridge.ShowNotification = (message, kind) => PluginPageSupport.ShowPluginNotice(message, kind);
        PluginHostBridge.ShowNotificationWithAction = (message, kind, action) =>
            PluginPageSupport.ShowPluginNotice(message, kind, action);
        PluginHostBridge.ConfirmAsync = (title, message, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Application.Current.Dispatcher.InvokeAsync(() => ModMain.MyMsgBox(message, title,
                Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")) == 1).Task;
        };
        PluginHostBridge.ChooseAsync = (title, message, options, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var confirmText = string.IsNullOrWhiteSpace(options.ConfirmText)
                ? Lang.Text("Common.Action.Confirm")
                : options.ConfirmText;
            var cancelText = string.IsNullOrWhiteSpace(options.CancelText)
                ? Lang.Text("Common.Action.Cancel")
                : options.CancelText;
            return Application.Current.Dispatcher.InvokeAsync(() => ModMain.MyMsgBox(message, title,
                confirmText, cancelText, options.AlternateText ?? "", forceWait: true),
                System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task;
        };
        ThemeService.ColorModeChanged += (_, _) =>
            PluginRuntimeManager.Notify(new PluginThemeChangedEvent(ThemeService.CurrentTheme.ToString()));
        ThemeService.ColorThemeChanged += theme =>
            PluginRuntimeManager.Notify(new PluginThemeChangedEvent(theme.ToString()));
        PluginHostBridge.OpenUrl = uri => Basics.OpenPath(uri.AbsoluteUri);
        PluginHostBridge.OpenPath = path => Basics.OpenPath(path);
    }

    /// <summary>
    /// Load and return instances using PCL's own folder selection and version-list loader.
    /// The launch home page may have validated only the saved selection, so the list loader
    /// can still be waiting even while McMcInstanceSelected is already available.
    /// </summary>
    private static IReadOnlyList<McInstance> _GetNativeInstances(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (PluginInstanceListLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = _GetNativeSelectedFolder();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return Array.Empty<McInstance>();

            var loader = ModInstanceList.mcInstanceListLoader;
            var loadedForCurrentFolder = _SameMinecraftPath(loader.input, folder);
            if (loader.State == ModBase.LoadState.Loading && loadedForCurrentFolder)
            {
                // Join the native scan already in progress instead of aborting/restarting it.
                loader.WaitForExit(loader.input);
            }
            else if (loader.State == ModBase.LoadState.Finished && loadedForCurrentFolder)
            {
                // Reuse the same timestamp-based refresh used when the native instance page is revisited.
                ModLoader.LoaderFolderRun(loader, folder, ModLoader.LoaderFolderRunType.RunOnUpdated,
                    1, @"versions\", waitForExit: true);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                // This is the same folder scanner used by PCL's instance-selection page.
                ModLoader.LoaderFolderRun(loader, folder, ModLoader.LoaderFolderRunType.ForceRun,
                    1, @"versions\", waitForExit: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ModBase.RunInUiWait(() =>
            {
                var instances = ModInstanceList.mcInstanceList.Values
                    .SelectMany(items => items)
                    .ToList();
                var currentSelection = ModInstanceList.McMcInstanceSelected;
                if (currentSelection is not null &&
                    !instances.Any(item => _SameMinecraftPath(item.PathInstance, currentSelection.PathInstance)))
                    instances.Add(currentSelection);
                return instances.DistinctBy(instance => instance.PathInstance, StringComparer.OrdinalIgnoreCase).ToArray();
            });
        }
    }

    /// <summary>Resolve a plugin's opaque instance ID against PCL's loaded native instance objects.</summary>
    private static McInstance? _FindNativeInstance(IReadOnlyList<McInstance> instances, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        var instance = instances.FirstOrDefault(item => _SameMinecraftPath(item.PathInstance, instanceId));
        if (instance is not null) return instance;

        // The home page can hold a validated saved selection before the complete list is loaded.
        // Keep that exact native selection usable if the scan did not include it (for example,
        // while its folder is being refreshed); never construct an instance from arbitrary input.
        return ModBase.RunInUiWait(() =>
        {
            var selected = ModInstanceList.McMcInstanceSelected;
            return selected is not null && _SameMinecraftPath(selected.PathInstance, instanceId) ? selected : null;
        });
    }

    /// <summary>Resolve the same selected Minecraft folder that PCL's launch home uses.</summary>
    private static string _GetNativeSelectedFolder()
    {
        var current = ModBase.RunInUiWait(() => ModFolder.mcFolderSelected);
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current)) return current;

        // Match PageLaunchLeft_Loaded: load the native folder list, then restore the persisted
        // folder selection, falling back to the first PCL-recognized folder if it is stale.
        ModFolder.mcFolderListLoader.WaitForExit();
        var selected = ModBase.RunInUiWait(() => States.Game.SelectedFolder.ToString().Replace("$", ModBase.exePath));
        if (string.IsNullOrWhiteSpace(selected) || !Directory.Exists(selected))
        {
            var firstFolder = ModBase.RunInUiWait(() => ModFolder.mcFolderList.FirstOrDefault()?.Location);
            if (string.IsNullOrWhiteSpace(firstFolder)) return string.Empty;
            selected = firstFolder;
            ModBase.RunInUiWait(() => States.Game.SelectedFolder = selected.Replace(ModBase.exePath, "$"));
        }

        ModBase.RunInUiWait(() => ModFolder.mcFolderSelected = selected);
        return selected;
    }

    private static bool _SameMinecraftPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            var normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Trim()));
            var normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right.Trim()));
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(left.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                right.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
