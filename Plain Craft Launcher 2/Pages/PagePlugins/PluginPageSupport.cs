using System.Windows;
using System.Windows.Controls;
using PCL.Core.App.Localization;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;

namespace PCL;

internal static class PluginPageSupport
{
    public static StackPanel CreateRoot(string title, out TextBlock status, out StackPanel content)
    {
        var root = new StackPanel { Margin = new Thickness(25, 10, 25, 24) };
        var heading = new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 12)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        root.Children.Add(heading);
        status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 0, 0, 12),
            Opacity = 0.9
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2");
        root.Children.Add(status);
        content = new StackPanel();
        root.Children.Add(content);
        return root;
    }

    public static MyCard CreateCard(UIElement child, string? title = null)
    {
        if (child is FrameworkElement element)
            element.Margin = string.IsNullOrWhiteSpace(title)
                ? new Thickness(20, 17, 20, 17)
                : new Thickness(20, 40, 20, 18);

        var card = new MyCard
        {
            Title = title ?? string.Empty,
            Margin = new Thickness(0, 0, 0, 15)
        };
        card.Children.Add(child);
        return card;
    }

    public static TextBlock Text(string text, double size = 14, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 0, 0, 6)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, bold ? "ColorBrush1" : "ColorBrushGray2");
        return block;
    }

    public static MyButton Button(string label, MyButton.ClickEventHandler click, bool accent = false)
    {
        var button = new MyButton
        {
            Text = label,
            Height = 35,
            MinWidth = 104,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 10, 8),
            VerticalAlignment = VerticalAlignment.Center,
            ColorType = accent ? MyButton.ColorState.Highlight : MyButton.ColorState.Normal
        };
        button.Click += click;
        return button;
    }

    public static Task<bool> ConfirmInstallationAsync(PluginPackageCheck check, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.FromResult(false);
        var githubDetails = check.GitHubProvenance is { } provenance
            ? Lang.Text("Plugin.Install.GitHubVerified", provenance.Repository, provenance.Tag,
                provenance.PublishedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), provenance.Sha256,
                check.SignedAtUtc is null
                    ? Lang.Text("Plugin.Install.GitHubNoSignatureTime")
                    : provenance.SignatureTimeAfterPublication
                        ? Lang.Text("Plugin.Install.GitHubTimeConflict")
                        : Lang.Text("Plugin.Install.GitHubTimeConsistent"))
            : Lang.Text("Plugin.Install.GitHubNotVerified");
        var pluginInfo = Lang.Text("Plugin.Install.PluginInfo", check.Manifest.Name, check.Manifest.Version,
            check.Manifest.Author, check.Manifest.Id);
        var mixinCount = (string.IsNullOrWhiteSpace(check.Manifest.MixinConfig) ? 0 : 1) +
                         (check.Manifest.MixinConfigs?.Count ?? 0);
        var mixinWarning = mixinCount == 0
            ? string.Empty
            : "\n\n" + Lang.Text("Plugin.Install.MixinWarning", mixinCount);
        if (!check.IsSigned)
            return dispatcher.InvokeAsync(() => ModMain.MyMsgBox(Lang.Text("Plugin.Install.UnsignedMessage", pluginInfo, githubDetails + mixinWarning),
                Lang.Text("Plugin.Install.UnsignedTitle"), Lang.Text("Plugin.Install.UnsignedConfirm"),
                Lang.Text("Plugin.Install.Cancel"), isWarn: true) == 1).Task;

        var signedAt = check.SignedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? Lang.Text("Plugin.Install.UnknownTime");
        var info = Lang.Text("Plugin.Install.SignerInfo", check.Signer ?? Lang.Text("Plugin.Install.UnknownSigner"),
            check.SignerThumbprint ?? Lang.Text("Plugin.Install.UnknownSigner"), signedAt, pluginInfo, githubDetails + mixinWarning);
        return dispatcher.InvokeAsync(() => ModMain.MyMsgBox(info, Lang.Text("Plugin.Install.SignedTitle"),
            Lang.Text("Plugin.Install.Confirm"), Lang.Text("Plugin.Install.Cancel"), isWarn: false) == 1).Task;
    }

    public static void ShowPluginNotice(string message, PluginNotificationKind kind = PluginNotificationKind.Info)
    {
        var hint = kind switch
        {
            PluginNotificationKind.Success => HintType.Success,
            PluginNotificationKind.Warning => HintType.Warning,
            PluginNotificationKind.Error => HintType.Error,
            _ => HintType.Info
        };
        ModBase.RunInUi(() => HintService.Hint(message, hint));
    }
}
