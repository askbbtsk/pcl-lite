using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;
using PCL.Core.Utils.OS;
using PCL.Network;
using PCL.Core.UI.Theme;

namespace PCL;

internal static class Program
{
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
        PluginHostBridge.GetInstancesAsync = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PluginGameInstance> instances = ModBase.RunInUiWait(() => ModInstanceList.mcInstanceList
                .Values.SelectMany(items => items)
                .DistinctBy(instance => instance.PathInstance, StringComparer.OrdinalIgnoreCase)
                .Select(instance => new PluginGameInstance(instance.PathInstance, instance.Name,
                    instance.Info?.VanillaName ?? "Unknown", instance.PathInstance,
                    ReferenceEquals(instance, ModInstanceList.McMcInstanceSelected)))
                .ToArray());
            return Task.FromResult(instances);
        };
        PluginHostBridge.GetSelectedInstanceAsync = cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ModBase.RunInUiWait(() =>
            {
                var instance = ModInstanceList.McMcInstanceSelected;
                return instance is null ? null : new PluginGameInstance(instance.PathInstance, instance.Name,
                    instance.Info?.VanillaName ?? "Unknown", instance.PathInstance, true);
            }));
        };
        PluginHostBridge.GetInstanceConfigurationAsync = (instanceId, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyDictionary<string, string>>(ModBase.RunInUiWait(() =>
            {
                var instance = ModInstanceList.mcInstanceList.Values.SelectMany(items => items)
                    .FirstOrDefault(item => string.Equals(item.PathInstance, instanceId, StringComparison.OrdinalIgnoreCase));
                if (instance is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = instance.PathInstance,
                    ["name"] = instance.Name,
                    ["version"] = instance.Info?.VanillaName ?? "Unknown",
                    ["gameDirectory"] = instance.PathIndie
                };
            }));
        };
        PluginHostBridge.LaunchInstanceWithOptionsAsync = async (instanceId, options, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var instance = ModInstanceList.mcInstanceList.Values.SelectMany(items => items)
                    .FirstOrDefault(item => string.Equals(item.PathInstance, instanceId, StringComparison.OrdinalIgnoreCase));
                if (instance is null) return false;
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
            cancellationToken.ThrowIfCancellationRequested();
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var instance = ModInstanceList.mcInstanceList.Values.SelectMany(items => items)
                    .FirstOrDefault(item => string.Equals(item.PathInstance, instanceId, StringComparison.OrdinalIgnoreCase));
                return instance is not null && ModLaunch.McLaunchStart(new ModLaunch.McLaunchOptions { instance = instance });
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
}
