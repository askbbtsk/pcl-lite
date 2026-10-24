using System.Diagnostics;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.Utils.OS;

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
            var form = new FormMain();
            return form;
        };
        // From dotnet/wpf #2393: fix tablet devices broken on .NET Core 3.0+
        _ = Tablet.TabletDevices;
        // Start lifecycle
        Lifecycle.OnInitialize();
    }
}