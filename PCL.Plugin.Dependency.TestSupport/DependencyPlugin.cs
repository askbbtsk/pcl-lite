using PCL.Plugin.SDK;

namespace PCL.Plugin.Dependency.TestSupport;

public static class SharedPluginApi
{
    public static string GetProviderToken() => "shared-provider-assembly-ready";
}

public sealed class DependencyProviderPlugin : IPclPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken) =>
        context.Storage.WriteTextAsync("ready.txt", SharedPluginApi.GetProviderToken(), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
