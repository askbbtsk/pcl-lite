using PCL.Plugin.SDK;

namespace PCL.Plugin.ImcProxy;

public sealed class Plugin : IPclPlugin
{
    private IPluginContext? _context;
    private ImcProxyApi? _api;
    private ImcProxyRuntime? _runtime;
    private ControlPage? _page;

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _api = new ImcProxyApi();
        _runtime = new ImcProxyRuntime(context, _api);
        _page = new ControlPage(context, _runtime, _api);

        context.Ui.RegisterPage("imcproxy", "IMCProxy", _page.CreateView);
        context.Logger.Info("IMCProxy plugin started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runtime is not null)
        {
            await _runtime.StopAsync(cancellationToken);
        }
        _context?.Logger.Info("IMCProxy plugin stopped.");
        _context = null;
        _api = null;
        _runtime = null;
        _page = null;
    }
}
