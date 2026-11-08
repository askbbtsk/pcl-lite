using System.Windows;
using System.Windows.Controls;
using PCL.Plugin.SDK;

namespace PCL.Plugin.Sample;

public sealed class Plugin : IPclPlugin
{
    private IPluginContext? _context;

    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        context.Logger.Info("Sample plugin started.");
        context.Ui.RegisterPage("sample", "Sample plugin", CreateView);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _context?.Logger.Info("Sample plugin stopped.");
        return Task.CompletedTask;
    }

    private FrameworkElement CreateView()
    {
        var context = _context ?? throw new InvalidOperationException("Plugin is not started.");
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = "PCL Lite Plugin SDK", FontSize = 24 },
                new TextBlock { Text = "This sample demonstrates a plugin page, logging and isolated storage.", Margin = new Thickness(0, 12, 0, 0) }
            }
        };
        panel.Children.Add(context.Ui.CreateButton("Show sample notification", () =>
        {
            context.Ui.ShowNotification("Hello from the sample plugin", PluginNotificationKind.Success);
            return Task.CompletedTask;
        }, PluginButtonStyle.Highlight));
        return panel;
    }
}
