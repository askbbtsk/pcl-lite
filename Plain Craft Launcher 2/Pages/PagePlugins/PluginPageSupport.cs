using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;

namespace PCL;

internal static class PluginPageSupport
{
    private static readonly HttpClient IconClient = new() { Timeout = TimeSpan.FromSeconds(5) };
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

    public static Image? PluginIcon(string? source, bool isUri)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            var uri = isUri ? new Uri(source, UriKind.Absolute) : new Uri(Path.GetFullPath(source), UriKind.Absolute);
            if (!isUri && !File.Exists(uri.LocalPath)) return null;
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (!new[] { ".png", ".jpg", ".jpeg", ".ico" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return null;
            var image = new Image { Width = 42, Height = 42, Stretch = System.Windows.Media.Stretch.Uniform,
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            image.Source = new BitmapImage(uri);
            return image;
        }
        catch { return null; }
    }

    public static async Task<Image?> LoadRemoteIconAsync(string? source, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        try
        {
            using var response = await IconClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 1024 * 1024) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > 1024 * 1024) return null;
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return CreateImage(bitmap);
        }
        catch { return null; }
    }

    public static async Task LoadRemoteIconIntoAsync(string? source, Image destination,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var loaded = await LoadRemoteIconAsync(source, cancellationToken);
            if (loaded?.Source is null || destination.Parent is null) return;
            var dispatcher = destination.Dispatcher;
            if (dispatcher.CheckAccess()) destination.Source = loaded.Source;
            else await dispatcher.InvokeAsync(() => destination.Source = loaded.Source);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", "插件市场图标加载失败"); }
    }

    private static Image CreateImage(BitmapImage source) => new()
    {
        Width = 42,
        Height = 42,
        Stretch = System.Windows.Media.Stretch.Uniform,
        Margin = new Thickness(0, 0, 10, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Source = source
    };

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
        var signerChangeWarning = check.SignerChanged
            ? "\n\n" + Lang.Text("Plugin.Install.SignerChanged", check.PreviousSignerThumbprint,
                check.IsSigned ? check.SignerThumbprint : Lang.Text("Plugin.Install.UnsignedSigner"))
            : string.Empty;
        var compatibilityWarning = check.CompatibilityWarning is null
            ? string.Empty
            : "\n\n" + Lang.Text("Plugin.Install.CoreCompatibilityWarning", check.CompatibilityWarning);
        var sourceWarning = check.SourceChanged
            ? "\n\n" + Lang.Text("Plugin.Install.SourceChanged", GetSourceName(check.PreviousInstallSourceType),
                check.PreviousInstallSourceUrl ?? Lang.Text("Plugin.Install.UnknownSource"),
                GetSourceName(check.CurrentInstallSourceType), check.CurrentInstallSourceUrl ?? Lang.Text("Plugin.Install.UnknownSource"))
            : string.Empty;
        var downgradeWarning = check.IsDowngrade
            ? "\n\n" + Lang.Text("Plugin.Install.DowngradeWarning", check.PreviousVersion!, check.Manifest.Version)
            : string.Empty;
        var packageChangedWarning = check.PackageChanged
            ? "\n\n" + Lang.Text("Plugin.Install.PackageChanged")
            : string.Empty;
        var updateWarning = sourceWarning + downgradeWarning + packageChangedWarning;
        if (!check.IsSigned)
            return dispatcher.InvokeAsync(() => ModMain.MyMsgBox(Lang.Text("Plugin.Install.UnsignedMessage", pluginInfo, githubDetails + mixinWarning + signerChangeWarning + compatibilityWarning + updateWarning),
                check.SignerChanged ? Lang.Text("Plugin.Install.SignerChangedTitle") : Lang.Text("Plugin.Install.UnsignedTitle"),
                check.SignerChanged ? Lang.Text("Plugin.Install.SignerChangedConfirm") : Lang.Text("Plugin.Install.UnsignedConfirm"),
                Lang.Text("Plugin.Install.Cancel"), isWarn: true) == 1).Task;

        var signedAt = check.SignedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? Lang.Text("Plugin.Install.UnknownTime");
        var info = Lang.Text("Plugin.Install.SignerInfo", check.Signer ?? Lang.Text("Plugin.Install.UnknownSigner"),
            check.SignerThumbprint ?? Lang.Text("Plugin.Install.UnknownSigner"), signedAt, pluginInfo,
            githubDetails + mixinWarning + signerChangeWarning + compatibilityWarning + updateWarning);
        return dispatcher.InvokeAsync(() => ModMain.MyMsgBox(info,
            check.SignerChanged ? Lang.Text("Plugin.Install.SignerChangedTitle") : Lang.Text("Plugin.Install.SignedTitle"),
            check.SignerChanged ? Lang.Text("Plugin.Install.SignerChangedConfirm") : Lang.Text("Plugin.Install.Confirm"),
            Lang.Text("Plugin.Install.Cancel"), isWarn: check.SignerChanged || check.CompatibilityWarning is not null ||
                check.SourceChanged || check.IsDowngrade || check.PackageChanged) == 1).Task;
    }

    public static void ShowPluginNotice(string message, PluginNotificationKind kind = PluginNotificationKind.Info,
        Func<Task>? onClick = null)
    {
        var hint = kind switch
        {
            PluginNotificationKind.Success => HintType.Success,
            PluginNotificationKind.Warning => HintType.Warning,
            PluginNotificationKind.Error => HintType.Error,
            _ => HintType.Info
        };
        ModBase.RunInUi(() => HintService.Hint(message, hint, onClick: onClick));
    }

    public static void PromptRestartForPluginChanges(string message)
    {
        if (ModMain.MyMsgBox(message, Lang.Text("Plugin.Restart.Title"), Lang.Text("Plugin.Restart.Now"),
                Lang.Text("Plugin.Restart.Later"), forceWait: true) != 1)
            return;

        try
        {
            var process = Process.Start(new ProcessStartInfo(Basics.ExecutablePath) { UseShellExecute = true });
            if (process is null) throw new InvalidOperationException("The launcher process could not be started.");
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "Plugin", "无法重启启动器以应用插件更改");
            ModMain.MyMsgBox(Lang.Text("Plugin.Restart.Failed", ex.Message), Lang.Text("Plugin.Restart.Title"),
                Lang.Text("Common.Action.Confirm"), forceWait: true);
            return;
        }

        FormMain.EndProgramForce();
    }

    private static string GetSourceName(string? sourceType) => sourceType switch
    {
        "market" => Lang.Text("Plugin.Installed.Source.Market"),
        "local" => Lang.Text("Plugin.Installed.Source.Local"),
        _ => Lang.Text("Plugin.Installed.Source.Unknown")
    };
}
