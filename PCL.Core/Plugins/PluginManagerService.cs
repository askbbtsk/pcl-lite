using PCL.Core.App.IoC;
using PCL.Core.Logging;
using System;
using System.Threading.Tasks;

namespace PCL.Core.Plugins;

[LifecycleService(LifecycleState.Loaded)]
[LifecycleScope("plugins", "插件系统")]
public sealed partial class PluginManagerService
{
    public static PluginRuntimeManager? Manager { get; private set; }

    [LifecycleStart]
    private static async Task _StartAsync()
    {
        Manager = new PluginRuntimeManager();
        await Manager.PrepareEnabledAsync().ConfigureAwait(false);
        LogWrapper.Info("Plugin", $"插件补丁已预载，当前插件数：{Manager.GetInstalled().Count}");
        Lifecycle.When(LifecycleState.WindowCreated, () => _ = _StartPluginsAtWindowCreatedAsync());
    }

    private static async Task _StartPluginsAtWindowCreatedAsync()
    {
        if (Manager is null) return;
        try
        {
            await Manager.StartPreparedAsync().ConfigureAwait(false);
            LogWrapper.Info("Plugin", "插件生命周期启动完成");
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Plugin", "启动待处理插件时发生错误");
        }
    }

    [LifecycleStop]
    private static async Task _StopAsync()
    {
        if (Manager is null) return;
        await Manager.DisposeAsync().ConfigureAwait(false);
        Manager = null;
    }
}
