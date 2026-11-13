using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme.Packages;

namespace PCL;

public partial class PageSetupUI
{
    private MyComboBox? _themePackages;
    private TextBlock? _themePackageInfo;
    private bool _updatingThemes;
    private CustomThemeRuntime? _themeRuntime;

    private async void InitializeThemePackages(object sender, RoutedEventArgs args)
    {
        if (_themePackages is not null) { await RefreshThemePackagesAsync(); return; }
        if (CustomThemeRuntime.Current is not { } runtime) return;
        _themeRuntime = runtime;
        var card = new MyCard { Title = Lang.Text("Theme.Pack.Title"), Margin = new Thickness(0, 0, 0, 15) };
        var body = new StackPanel { Margin = new Thickness(25, 42, 25, 20) };
        var description = ThemeText("Theme.Pack.Description");
        _themePackages = CreateThemePackageSelector();
        _themePackageInfo = ThemeText("Theme.Pack.BuiltIn");
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        AddThemeAction(buttons, "Theme.Pack.OpenExample", () =>
        {
            string directory = ThemeLearningExample.Export(PCL.Core.App.Paths.Data);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        AddThemeAction(buttons, "Theme.Pack.OpenFolder", () =>
        {
            Directory.CreateDirectory(runtime.Directory);
            Process.Start(new ProcessStartInfo(runtime.Directory) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        AddThemeAction(buttons, "Theme.Pack.Install", async () =>
        {
            var dialog = new OpenFileDialog { Filter = "PCL Theme (*.xaml;*.zip)|*.xaml;*.zip",
                Multiselect = false, Title = Lang.Text("Theme.Pack.Install") };
            if (dialog.ShowDialog(ModMain.frmMain) == true) await runtime.InstallAsync(dialog.FileName);
        });
        AddThemeAction(buttons, "Theme.Pack.InstallFolder", async () =>
        {
            var dialog = new OpenFolderDialog { Title = Lang.Text("Theme.Pack.InstallFolder"), Multiselect = false };
            if (dialog.ShowDialog(ModMain.frmMain) == true) await runtime.InstallAsync(dialog.FolderName);
        });
        AddThemeAction(buttons, "Theme.Pack.Reload", async () =>
        {
            if ((_themePackages.SelectedItem as MyComboBoxItem)?.Tag is string id) await runtime.PreviewAsync(id);
        });
        body.Children.Add(description);
        body.Children.Add(_themePackages);
        body.Children.Add(_themePackageInfo);
        body.Children.Add(buttons);
        card.Children.Add(body);
        InsertThemePackagesCard(PanMain, CardLiquidGlass, card);
        _themePackages.SelectionChanged += ThemePackageSelectionChanged;
        runtime.Changed += OnThemePackagesChanged;
        Unloaded += ThemePackagesUnloaded;
        await RefreshThemePackagesAsync();
    }

    internal static void InsertThemePackagesCard(Panel parent, UIElement liquidGlassCard, UIElement themeCard)
    {
        int index = parent.Children.IndexOf(liquidGlassCard);
        if (index < 0) throw new InvalidOperationException("The liquid glass settings card is missing.");
        parent.Children.Insert(index + 1, themeCard);
    }

    internal static MyComboBox CreateThemePackageSelector()
    {
        var selector = new MyComboBox { MinHeight = 38, Margin = new Thickness(0, 10, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        // Theme recovery must remain usable even if a package's ComboBox
        // template is broken. Pin the built-in PCL style for this one selector.
        ThemeControlRegistry.SetProtected(selector, true);
        selector.Style = (Style)System.Windows.Application.Current.FindResource(typeof(MyComboBox));
        selector.ItemContainerStyle = (Style)System.Windows.Application.Current.FindResource("MyComboBoxItem");
        selector.Resources["MyComboBoxItem"] = selector.ItemContainerStyle;
        return selector;
    }

    internal static void PopulateThemePackageSelector(MyComboBox selector,
        IReadOnlyList<ThemeCatalogItem> installed, string? activeId, string builtInLabel)
    {
        selector.Items.Clear();
        var builtin = new MyComboBoxItem { Content = builtInLabel, MinHeight = 38 };
        selector.Items.Add(builtin);
        selector.SelectedItem = builtin;
        foreach (var item in installed)
        {
            var option = new MyComboBoxItem { MinHeight = 38,
                Content = item.Manifest is { } manifest ? manifest.Name + " · " + manifest.Version
                    : Path.GetFileName(item.Directory) + " · " + item.Error,
                Tag = item.Manifest?.Id, IsEnabled = item.Manifest is not null };
            selector.Items.Add(option);
            if (activeId is not null && item.Manifest?.Id == activeId) selector.SelectedItem = option;
        }
    }

    private static TextBlock ThemeText(string key)
    {
        var text = new TextBlock { Text = Lang.Text(key), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        return text;
    }
    private void AddThemeAction(Panel parent, string key, Func<Task> action)
    {
        var button = new MyButton { Text = Lang.Text(key), MinHeight = 38, MinWidth = 120,
            Margin = new Thickness(0, 0, 10, 8), Padding = new Thickness(12, 0, 12, 0) };
        button.Click += async (_, _) =>
        {
            if (_themeRuntime?.IsPreviewing == true) return;
            try { await action(); await RefreshThemePackagesAsync(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { ModMain.MyMsgBox(Lang.Text("Theme.Pack.Failed", error.Message), Lang.Text("Theme.Pack.Title")); }
        };
        parent.Children.Add(button);
    }
    private async void ThemePackageSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingThemes || _themeRuntime is null || _themeRuntime.IsPreviewing) return;
        if ((_themePackages?.SelectedItem as MyComboBoxItem)?.Tag is string id)
            await _themeRuntime.PreviewAsync(id);
        else _themeRuntime.RestoreBuiltIn();
        await RefreshThemePackagesAsync();
    }
    private async void OnThemePackagesChanged(object? sender, EventArgs args)
    {
        if (IsLoaded && _themeRuntime?.IsPreviewing != true) await RefreshThemePackagesAsync();
    }
    private async Task RefreshThemePackagesAsync()
    {
        if (_updatingThemes || _themePackages is null || _themeRuntime is null) return;
        _updatingThemes = true;
        try
        {
            var installed = await _themeRuntime.GetInstalledAsync();
            PopulateThemePackageSelector(_themePackages, installed, _themeRuntime.ActiveTheme?.Id,
                Lang.Text("Theme.Pack.BuiltIn"));
            var current = _themeRuntime.ActiveTheme;
            _themePackageInfo!.Text = _themeRuntime.LastError ?? (current is null ? Lang.Text("Theme.Pack.BuiltIn")
                : Lang.Text("Theme.Pack.Information", current.Name, current.Version,
                    string.IsNullOrEmpty(current.Author) ? Lang.Text("Theme.Pack.Undeclared") : current.Author,
                    current.LauncherVersion ?? Lang.Text("Theme.Pack.Undeclared"),
                    current.SupportsGlass ? Lang.Text("Theme.Pack.GlassSupported") : Lang.Text("Theme.Pack.GlassPaused")));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (_themePackageInfo is not null) _themePackageInfo.Text = error.Message; }
        finally { _updatingThemes = false; }
    }
    private void ThemePackagesUnloaded(object sender, RoutedEventArgs args)
    {
        if (_themeRuntime is not null) _themeRuntime.Changed -= OnThemePackagesChanged;
        Unloaded -= ThemePackagesUnloaded;
        // Page instances are reused; resubscribe when loaded again.
        Loaded += ThemePackagesReloaded;
    }
    private void ThemePackagesReloaded(object sender, RoutedEventArgs args)
    {
        Loaded -= ThemePackagesReloaded;
        if (_themeRuntime is not null) _themeRuntime.Changed += OnThemePackagesChanged;
        Unloaded += ThemePackagesUnloaded;
    }
}
