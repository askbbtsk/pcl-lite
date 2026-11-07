using System.Diagnostics;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;
using PCL.Core.Utils.OS;
using PCL.Network;

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
        PluginHostBridge.ShowNotification = PluginPageSupport.ShowPluginNotice;
        PluginHostBridge.ConfirmAsync = (title, message, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Application.Current.Dispatcher.InvokeAsync(() => ModMain.MyMsgBox(message, title,
                Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")) == 1).Task;
        };
        PluginHostBridge.OpenUrl = uri => Basics.OpenPath(uri.AbsoluteUri);
        PluginHostBridge.OpenPath = path => Basics.OpenPath(path);
    }
}
