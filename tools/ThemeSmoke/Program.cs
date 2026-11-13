using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using PCL;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Configuration.Storage;
using PCL.Core.UI.Theme;
using PCL.Core.UI.Theme.Packages;

// No Window.Show, screenshots, input simulation, user configuration or game scans.
// Instantiate production controls against in-memory configuration and render to RAM.
internal static class Program
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        try
        {
            InitializeMemoryConfiguration();
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LoadBuiltInResources(application);
            CheckLearningExampleExport();
            CheckThemeSettingsPlacement();
            CheckLiquidGlassSettings();
            var source = System.IO.Path.GetFullPath("examples/themes/SoftSlate");
            var service = new ThemePackageService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcl-theme-smoke"),
                System.IO.Path.GetTempPath());
            var package = Task.Run(() => service.InspectAsync(source)).GetAwaiter().GetResult();
            string archivePath = Path.Combine(Path.GetTempPath(), "pcl-soft-slate-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                System.IO.Compression.ZipFile.CreateFromDirectory(source, archivePath);
                var archived = Task.Run(() => service.InspectAsync(archivePath)).GetAwaiter().GetResult();
                if (archived.ContentHash != package.ContentHash)
                    throw new InvalidDataException("The test theme ZIP differs from its directory package.");
            }
            finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
            var layer = new ThemeResourceLoader().Load(package, false, false);
            var assembly = typeof(FormMain).Assembly;
            Type registry = assembly.GetType("PCL.ThemeControlRegistry", true)!;
            registry.GetMethod("ValidateTemplates", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [layer]);
            var main = (FormMain)RuntimeHelpers.GetUninitializedObject(typeof(FormMain));
            var form = new Grid();
            typeof(FormMain).GetField("PanForm", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, form);
            form.Resources.MergedDictionaries.Add(layer);
            var runtime = (CustomThemeRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CustomThemeRuntime));
            typeof(CustomThemeRuntime).GetField("_active", PrivateInstance)!.SetValue(runtime, package);
            typeof(CustomThemeRuntime).GetField("_layer", PrivateInstance)!.SetValue(runtime, layer);
            typeof(CustomThemeRuntime).GetField("<Current>k__BackingField", PrivateStatic)!.SetValue(null, runtime);
            registry.GetField("_window", PrivateStatic)!.SetValue(null, main);
            try
            {
                CheckButtonRoundTrip(registry, form, layer);
                CheckTextBoxRoundTrip(registry, form, layer);
                CheckCardRoundTrip(registry, form, layer);
                CheckComboBoxRoundTrip(registry, form, layer);
                CheckThemeDropDownRecovery(registry, form, layer);
                CheckProtectedThemeSelector(registry, form);
                CheckSelectionAndListRoundTrips(registry, form);
                CheckProgrammaticPresentationState(registry, form);
                CheckRootResourceRestoration(registry, main, form, runtime, layer);
            }
            finally
            {
                registry.GetField("_window", PrivateStatic)!.SetValue(null, null);
                typeof(CustomThemeRuntime).GetField("<Current>k__BackingField", PrivateStatic)!.SetValue(null, null);
                application.Shutdown();
            }
            Console.WriteLine("Theme smoke passed: production button/card/textbox retain content and behavior across repeated template switches; no visible windows.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("::error::Theme smoke failed: " + error);
            return 1;
        }
    }

    private static void LoadBuiltInResources(System.Windows.Application application)
    {
        var source = XDocument.Load("Plain Craft Launcher 2/Application.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(source.Descendants(presentation + "ResourceDictionary").First());
        foreach (var declaration in source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            string value = declaration.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                && !declaration.Value.Contains(";assembly=", StringComparison.Ordinal)
                ? declaration.Value + ";assembly=Plain Craft Launcher 2" : declaration.Value;
            dictionary.SetAttributeValue(declaration.Name, value);
        }
        foreach (var attribute in dictionary.Descendants().Attributes("Source")
                     .Where(attribute => attribute.Value.StartsWith("/Resources/", StringComparison.Ordinal)))
            attribute.Value = "/Plain Craft Launcher 2;component" + attribute.Value;
        foreach (var element in dictionary.DescendantsAndSelf())
            if (element.Name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal)
                && !element.Name.NamespaceName.Contains(";assembly=", StringComparison.Ordinal))
                element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Plain Craft Launcher 2");
        application.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
    }

    private static void CheckThemeSettingsPlacement()
    {
        var parent = new StackPanel();
        var basic = new Border();
        var glass = new Border();
        var next = new Border();
        var themes = new Border();
        parent.Children.Add(basic);
        parent.Children.Add(glass);
        parent.Children.Add(next);
        typeof(PageSetupUI).GetMethod("InsertThemePackagesCard", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [parent, glass, themes]);
        if (parent.Children.Count != 4 || parent.Children[0] != basic || parent.Children[1] != glass
            || parent.Children[2] != themes || parent.Children[3] != next)
            throw new InvalidDataException("Theme package settings must appear immediately below liquid glass.");
        var page = XDocument.Load("Plain Craft Launcher 2/Pages/PageSetup/PageSetupUI.xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        if (!page.Descendants().Any(element => (string?)element.Attribute(xaml + "Name") == "CardLiquidGlass"
                && (string?)element.Attribute("Title") == "{DynamicResource Setup.Ui.Tahoe.Title}"))
            throw new InvalidDataException("The actual settings XAML must name the liquid glass anchor.");
        Console.WriteLine("Theme package settings insert immediately below the actual liquid glass card.");
    }

    private static void CheckLiquidGlassSettings()
    {
        var config = Config.Preference.TahoeGlass;
        config.PerformancePreset = 3;
        config.MaximumFrameRate = 120;
        config.SamplingFrameRate = -1;
        config.BlurRadius = config.Dispersion = config.Highlight = config.Motion = -1;
        config.Diffuse = config.EdgeSharpness = -1;
        static PageSetupUI CreatePage()
        {
            // Match the production page-construction guard: unrelated XAML defaults
            // must not invoke main-window preference observers during InitializeComponent.
            ModAnimation.AniControlEnabled++;
            try { return new PageSetupUI(); }
            finally { ModAnimation.AniControlEnabled--; }
        }
        var page = CreatePage(); // Never loaded into a Window; no capture or desktop access.
        var refresh = typeof(PageSetupUI).GetMethod("RefreshLiquidGlassSettings", PrivateInstance)!;
        var custom = typeof(PageSetupUI).GetMethod("UseCustomGlassSettings", PrivateInstance)!;
        refresh.Invoke(page, null);
        custom.Invoke(page, null);
        if (config.PerformancePreset != PCL.Core.UI.LiquidGlassPerformance.CustomPreset ||
            config.BlurRadius != 3 || config.Dispersion != 30 || config.Highlight != 80 ||
            config.Motion != 1 || config.SamplingFrameRate != 120)
            throw new InvalidDataException("Custom glass settings lost inherited Ultra values.");
        var combo = (MyComboBox)page.FindName("ComboLiquidGlassPreset");
        if (combo.Items.Count != 5 || combo.SelectedIndex != 4 || combo.MinHeight < 38)
            throw new InvalidDataException("Glass recommendations must include a full-height Custom option.");
        var slider = (MySlider)page.FindName("SliderLiquidGlassRefraction");
        slider.Value = 0;
        typeof(PageSetupUI).GetMethod("LiquidGlassRefraction_Changed", PrivateInstance)!.Invoke(page, [slider, true]);
        if (config.RefractionStrength != 0 || config.BlurRadius != 3)
            throw new InvalidDataException("Zero refraction must be accepted without resetting other tuning.");
        slider.Value = 10;
        typeof(PageSetupUI).GetMethod("LiquidGlassRefraction_Changed", PrivateInstance)!.Invoke(page, [slider, true]);
        if (config.RefractionStrength != 10)
            throw new InvalidDataException("The refraction control must reach 200%.");
        var sampling = (MySlider)page.FindName("SliderLiquidGlassSampling");
        sampling.Value = 0;
        typeof(PageSetupUI).GetMethod("LiquidGlassSampling_Changed", PrivateInstance)!.Invoke(page, [sampling, true]);
        if (config.SamplingFrameRate != 30 || config.MaximumFrameRate != 120)
            throw new InvalidDataException("Desktop sampling must be independent of animation frame rate.");
        var reopened = CreatePage();
        refresh.Invoke(reopened, null);
        if (((MyComboBox)reopened.FindName("ComboLiquidGlassPreset")).SelectedIndex != 4 ||
            ((MySlider)reopened.FindName("SliderLiquidGlassSampling")).Value != 0)
            throw new InvalidDataException("Reopening settings must retain custom tuning.");
        Console.WriteLine("Glass settings: Custom option, inherited-value preservation, 0–200% refraction and independent sampling passed.");
    }

    private static void CheckLearningExampleExport()
    {
        string data = Path.Combine(Path.GetTempPath(), "pcl-theme-learning-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Type exporter = typeof(FormMain).Assembly.GetType("PCL.ThemeLearningExample", true)!;
            MethodInfo export = exporter.GetMethod("Export", BindingFlags.NonPublic | BindingFlags.Static)!;
            string directory = (string)export.Invoke(null, [data])!;
            foreach (string file in new[] { "StarterTheme.xaml", "README.md" })
                if (!File.ReadAllBytes(Path.Combine(directory, file)).AsSpan()
                        .SequenceEqual(File.ReadAllBytes(Path.Combine("docs", "theme-examples", file))))
                    throw new InvalidDataException("The shipped theme learning file differs from its documented source: " + file);
            string edited = Path.Combine(directory, "StarterTheme.xaml");
            File.AppendAllText(edited, "\n<!-- USER EDIT: keep me -->");
            string expected = File.ReadAllText(edited);
            File.Delete(Path.Combine(directory, "README.md"));
            string secondDirectory = (string)export.Invoke(null, [data])!;
            if (directory != secondDirectory || File.ReadAllText(edited) != expected
                || !File.Exists(Path.Combine(directory, "README.md"))
                || Directory.EnumerateFiles(directory, "*.tmp").Any())
                throw new InvalidDataException("Opening the learning example must preserve user edits and recover missing files.");
            var service = new ThemePackageService(Path.Combine(data, "Themes"), Path.Combine(data, "Temp"));
            var package = Task.Run(() => service.InspectAsync(edited)).GetAwaiter().GetResult();
            var resources = new ThemeResourceLoader().Load(package, false, false);
            typeof(FormMain).Assembly.GetType("PCL.ThemeControlRegistry", true)!
                .GetMethod("ValidateTemplates", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [resources]);
            Console.WriteLine("Embedded annotated example matches docs, remains installable and never overwrites user edits.");
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    private static void CheckButtonRoundTrip(Type registry, Grid form, ResourceDictionary layer)
    {
        var button = new MyButton { Text = "按钮 / Button", Width = 180, Height = 44 };
        form.Children.Add(button);
        var original = button.Child;
        var baseline = Render(button);
        object entry = Entry(registry, button, "Button");
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        for (int iteration = 0; iteration < 20; iteration++)
        {
            Invoke(entry, "Apply");
            if (button.Child is not ThemePresenter presenter || !ReferenceEquals(presenter.Content, original))
                throw new InvalidDataException("Button template lost its original visual/content identity.");
            if (button.Text != "按钮 / Button") throw new InvalidDataException("Button text changed during styling.");
            _ = Render(button);
            Invoke(entry, "Restore");
            if (!ReferenceEquals(button.Child, original) || !baseline.AsSpan().SequenceEqual(Render(button)))
                throw new InvalidDataException("Button did not restore its built-in pixels/content.");
        }
        if (clicks != 0) throw new InvalidDataException("Theme switching executed a button action.");
        form.Children.Remove(button);
        Console.WriteLine("20 production MyButton visual round trips restored original pixels and did not execute actions.");
    }

    private static void CheckTextBoxRoundTrip(Type registry, Grid form, ResourceDictionary layer)
    {
        var editor = new MyTextBox { Text = "中文 input", HintText = "Hint", Width = 220, Height = 42 };
        form.Children.Add(editor);
        editor.ApplyTemplate();
        var originalTemplate = editor.Template;
        object entry = Entry(registry, editor, "TextBox");
        for (int iteration = 0; iteration < 20; iteration++)
        {
            editor.Select(2, 3);
            Invoke(entry, "Apply");
            _ = Render(editor);
            if (editor.Template.FindName("PART_ContentHost", editor) is not ScrollViewer)
                throw new InvalidDataException("Text editor lost its required editing surface.");
            if (editor.SelectionStart != 2 || editor.SelectionLength != 3 || editor.Text != "中文 input")
                throw new InvalidDataException("Text or selection changed on theme switch.");
            editor.SelectedText = "abc";
            if (editor.Text != "中文abcput") throw new InvalidDataException("Text editor cannot edit after theme switch.");
            editor.Text = "中文 input";
            Invoke(entry, "Restore");
            if (!ReferenceEquals(editor.Template, originalTemplate)) throw new InvalidDataException("Text template failed to restore.");
        }
        form.Children.Remove(editor);
        Console.WriteLine("20 production MyTextBox switches preserved selection and editing, then restored its original template.");
    }

    private static void CheckCardRoundTrip(Type registry, Grid form, ResourceDictionary layer)
    {
        var card = new MyCard { Title = "Card", Width = 300 };
        var content = new StackPanel { Height = 100, Margin = new Thickness(10, 40, 10, 10) };
        card.Children.Add(content);
        _ = card.MainTextBlock;
        form.Children.Add(card);
        UIElement[] original = card.Children.Cast<UIElement>().ToArray();
        object entry = Entry(registry, card, "Card");
        for (int iteration = 0; iteration < 20; iteration++)
        {
            Invoke(entry, "Apply");
            if (!original.SequenceEqual(card.Children.Cast<UIElement>()))
                throw new InvalidDataException("Card theme altered its public child collection.");
            card.Title = "Updated " + iteration;
            card.IsSwapped = iteration % 2 == 0;
            if (card.MainTextBlock.Text != card.Title) throw new InvalidDataException("Card title no longer updates.");
            Invoke(entry, "Restore");
        }
        form.Children.Remove(card);
        Console.WriteLine("20 production MyCard switches preserved the child collection and title binding.");
    }

    private static void CheckComboBoxRoundTrip(Type registry, Grid form, ResourceDictionary layer)
    {
        var combo = new MyComboBox { Width = 240, Height = 42, IsEditable = true };
        combo.Items.Add(new MyComboBoxItem { Content = "one" });
        combo.Items.Add(new MyComboBoxItem { Content = "two" });
        form.Children.Add(combo);
        combo.ApplyTemplate();
        combo.Text = "中文 editable";
        var original = combo.Template;
        object entry = Entry(registry, combo, "ComboBox");
        for (int iteration = 0; iteration < 20; iteration++)
        {
            Invoke(entry, "Apply");
            _ = Render(combo);
            if (combo.Template.FindName("PART_EditableTextBox", combo) is not MyTextBox editor)
                throw new InvalidDataException("Editable combo lost its PCL editor.");
            if (combo.Text != "中文 editable") throw new InvalidDataException("Combo text changed on retemplate.");
            int events = 0;
            MyComboBox.TextChangedEventHandler handler = (_, _) => events++;
            combo.TextChanged += handler;
            editor.Text = "changed";
            combo.TextChanged -= handler;
            if (events != 1) throw new InvalidDataException($"Editable combo raised {events} change events instead of one.");
            combo.Text = "中文 editable";
            Invoke(entry, "Restore");
            if (!ReferenceEquals(combo.Template, original)) throw new InvalidDataException("Combo template failed to restore.");
        }
        form.Children.Remove(combo);
        Console.WriteLine("20 editable MyComboBox switches retained text and raised one change event per edit.");
    }

    private static void CheckThemeDropDownRecovery(Type registry, Grid form, ResourceDictionary layer)
    {
        var combo = new MyComboBox { Width = 300, Height = 42 };
        combo.Items.Add(new MyComboBoxItem { Content = "内置主题", MinHeight = 38 });
        combo.Items.Add(new MyComboBoxItem { Content = "Soft Slate", Tag = "pcl.soft-slate", MinHeight = 38 });
        combo.SelectedIndex = 1;
        form.Children.Add(combo);
        object entry = Entry(registry, combo, "ComboBox");
        Invoke(entry, "Apply");
        _ = Render(combo);
        var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
        // Remove the popup binding in this process only. The actual click must
        // change the owning selector state without showing a popup on the desktop.
        System.Windows.Data.BindingOperations.ClearBinding(popup, System.Windows.Controls.Primitives.Popup.IsOpenProperty);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        combo.RaiseEvent(click);
        // This host deliberately stays unloaded so no HWND popup is created.
        // WPF coerces IsDropDownOpen=false until Loaded; inspect its requested
        // local value to verify that the mouse handler actually requests opening.
        if (!Equals(combo.ReadLocalValue(ComboBox.IsDropDownOpenProperty), true))
            throw new InvalidDataException("Clicking the installed theme's selector did not open its dropdown."
                + $" bridge={typeof(MyComboBox).GetField("_themeNeedsDropDownBridge", PrivateInstance)!.GetValue(combo)}"
                + $" toggles={Descendants(combo).OfType<System.Windows.Controls.Primitives.ToggleButton>().Count()}"
                + $" handled={click.Handled} enabled={combo.IsEnabled}");
        if (combo.Items.Count != 2 || ((MyComboBoxItem)combo.Items[0]).Content?.ToString() != "内置主题")
            throw new InvalidDataException("Built-in theme must remain the first dropdown option.");
        combo.SelectedIndex = 0;
        if (((MyComboBoxItem)combo.SelectedItem).Tag is not null)
            throw new InvalidDataException("Built-in theme selection must resolve to the built-in appearance.");
        combo.IsDropDownOpen = false;
        Invoke(entry, "Restore");
        form.Children.Remove(combo);
        Console.WriteLine("Mouse input requests opening for an installed custom ComboBox, with built-in theme first.");
    }

    private static void CheckProtectedThemeSelector(Type registry, Grid form)
    {
        var selector = (MyComboBox)typeof(PageSetupUI).GetMethod("CreateThemePackageSelector",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var populate = typeof(PageSetupUI).GetMethod("PopulateThemePackageSelector", BindingFlags.Static | BindingFlags.NonPublic)!;
        ThemeCatalogItem[] installed =
        [
            new("themes/pcl.soft-slate", new ThemeManifest { Id = "pcl.soft-slate", Name = "Soft Slate" }, null),
            new("themes/broken", null, "Invalid manifest")
        ];
        form.Children.Add(selector);
        foreach (string? active in new[] { null, "pcl.soft-slate", "removed-theme", null })
        {
            populate.Invoke(null, [selector, installed, active, "内置主题"]);
            if (selector.Items.Count != 3 || selector.Items[0] is not MyComboBoxItem { Tag: null, IsEnabled: true } builtin
                || builtin.Content?.ToString() != "内置主题" || builtin.MinHeight < 38)
                throw new InvalidDataException("Built-in theme is missing/disabled/not first.");
            if (selector.SelectedIndex != (active == "pcl.soft-slate" ? 1 : 0))
                throw new InvalidDataException("Unavailable/broken packages must never replace the built-in selection.");
        }
        object entry = Entry(registry, selector, "ComboBox");
        Invoke(entry, "Apply");
        _ = Render(selector);
        var popup = (System.Windows.Controls.Primitives.Popup)selector.Template.FindName("PART_Popup", selector);
        System.Windows.Data.BindingOperations.ClearBinding(popup, System.Windows.Controls.Primitives.Popup.IsOpenProperty);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
        var toggle = Descendants(selector).OfType<System.Windows.Controls.Primitives.ToggleButton>().FirstOrDefault();
        if (toggle is null) throw new InvalidDataException("The protected theme selector lost its native PCL dropdown trigger.");
        var binding = System.Windows.Data.BindingOperations.GetBinding(toggle,
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty);
        if (binding?.Path?.Path != "IsDropDownOpen" || binding.Mode != System.Windows.Data.BindingMode.TwoWay)
            throw new InvalidDataException("Theme recovery dropdown trigger is not connected to IsDropDownOpen.");
        typeof(System.Windows.Controls.Primitives.ToggleButton).GetMethod("OnToggle",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, null);
        if (!Equals(selector.ReadLocalValue(ComboBox.IsDropDownOpenProperty), true))
            throw new InvalidDataException("Theme recovery dropdown cannot open.");
        selector.SelectedIndex = 0;
        if (((MyComboBoxItem)selector.SelectedItem).Tag is not null)
            throw new InvalidDataException("Cannot select built-in theme from the protected dropdown.");
        selector.IsDropDownOpen = false;
        Invoke(entry, "Restore");
        form.Children.Remove(selector);
        Console.WriteLine("Protected production theme selector requests opening and keeps the enabled built-in option first, including broken/missing packages.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static object Entry(Type registry, FrameworkElement host, string role) =>
        Activator.CreateInstance(registry.GetNestedType("Entry", BindingFlags.NonPublic)!,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [host, role], null)!;

    private static void CheckSelectionAndListRoundTrips(Type registry, Grid form)
    {
        var check = new MyCheckBox { Text = "Checkbox", Width = 220, Height = 38 };
        form.Children.Add(check);
        object entry = Entry(registry, check, "CheckBox");
        for (int iteration = 0; iteration < 20; iteration++)
        {
            Invoke(entry, "Apply");
            check.Checked = iteration % 2 == 0;
            _ = Render(check);
            Invoke(entry, "Restore");
            if (check.Checked != (iteration % 2 == 0) || check.Text != "Checkbox")
                throw new InvalidDataException("Checkbox state changed while restoring its theme.");
        }
        form.Children.Remove(check);
        var list = new MyListItem { Title = "List", Info = "Details", Width = 300 };
        form.Children.Add(list);
        var originalChildren = list.Children.Cast<UIElement>().ToArray();
        object listEntry = Entry(registry, list, "ListItem");
        Invoke(listEntry, "Apply");
        list.Tags = "New|Tags";
        list.Info = "Updated details";
        _ = Render(list);
        Invoke(listEntry, "Restore");
        if (!originalChildren.All(list.Children.Contains) || list.Info != "Updated details" || list.PanTags.Children.Count != 2)
            throw new InvalidDataException("List item lost dynamic children on theme restore.");
        form.Children.Remove(list);
        Console.WriteLine("Checkbox state and dynamically added list-item tags survive theme switches.");
    }
    private static void Invoke(object entry, string method) => entry.GetType().GetMethod(method)!.Invoke(entry, null);
    private static byte[] Render(FrameworkElement control)
    {
        control.Measure(new Size(300, 160));
        control.Arrange(new Rect(0, 0, double.IsFinite(control.Width) ? control.Width : 300,
            double.IsFinite(control.Height) ? control.Height : 160));
        control.UpdateLayout();
        var bitmap = new RenderTargetBitmap(300, 160, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        byte[] pixels = new byte[300 * 160 * 4];
        bitmap.CopyPixels(pixels, 300 * 4, 0);
        return pixels;
    }

    private static void InitializeMemoryConfiguration()
    {
        // This process is exclusively a test host. Skip all lifecycle services,
        // file providers, migration and observer initialization.
        var service = typeof(ConfigService);
        service.GetMethod("_InitializeConfigItems", PrivateStatic)!.Invoke(null, null);
        service.GetField("_isConfigItemsInitialized", PrivateStatic)!.SetValue(null, true);
        foreach (string field in new[] { "_sharedConfigProvider", "_sharedEncryptedConfigProvider", "_localConfigProvider", "_instanceConfigProvider" })
            service.GetField(field, PrivateStatic)!.SetValue(null, new MemoryStorage());
        service.GetField("_isProvidersInitialized", PrivateStatic)!.SetValue(null, true);
    }
    private static void CheckRootResourceRestoration(Type registry, FormMain main, Grid form,
        CustomThemeRuntime runtime, ResourceDictionary layer)
    {
        var background = new Border { Background = Brushes.White };
        typeof(FormMain).GetField("LauncherBackgroundLayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(main, background);
        var oldMain = ModMain.frmMain;
        ModMain.frmMain = main;
        try
        {
            registry.GetMethod("Refresh", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null);
            if (form.ReadLocalValue(System.Windows.Documents.TextElement.FontFamilyProperty) == DependencyProperty.UnsetValue)
                throw new InvalidDataException("Theme font was not applied to the root.");
            // Simulate a user changing normal appearance preferences while a
            // custom package is active. Restoring must not revive a stale brush.
            Config.Preference.Background.BackgroundColorful = false;
            System.Windows.Application.Current.Resources["ColorBrushBackground"] = Brushes.LimeGreen;
            typeof(CustomThemeRuntime).GetField("_active", PrivateInstance)!.SetValue(runtime, null);
            typeof(CustomThemeRuntime).GetField("_layer", PrivateInstance)!.SetValue(runtime, null);
            form.Resources.MergedDictionaries.Remove(layer);
            registry.GetMethod("Refresh", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null);
            if (form.ReadLocalValue(System.Windows.Documents.TextElement.FontFamilyProperty) != DependencyProperty.UnsetValue)
                throw new InvalidDataException("Restoring a theme left a stale local font override.");
            if (background.Background != Brushes.LimeGreen)
                throw new InvalidDataException("Restoring a theme ignored the user's current background preference.");
        }
        finally { ModMain.frmMain = oldMain; }
        Console.WriteLine("Restoring built-in appearance clears theme inheritance overrides and uses current user background settings.");
    }
    private static void CheckProgrammaticPresentationState(Type registry, Grid form)
    {
        var attach = registry.GetMethod("Attach", PrivateStatic)!;
        var button = new MyButton { Text = "State" };
        form.Children.Add(button);
        attach.Invoke(null, [button]);
        var presenter = (ThemePresenter)button.Child;
        button.ColorType = MyButton.ColorState.Red;
        if (!presenter.IsDanger) throw new InvalidDataException("Programmatic danger state did not reach the visual template.");
        button.ColorType = MyButton.ColorState.Normal;
        if (presenter.IsDanger) throw new InvalidDataException("Danger state did not clear.");
        var card = new MyCard { Title = "State" };
        form.Children.Add(card);
        attach.Invoke(null, [card]);
        var cardPresenter = (ThemePresenter)card.BorderChild;
        card.IsSwapped = true;
        if (cardPresenter.IsExpanded) throw new InvalidDataException("Programmatic card collapse did not reach the visual template.");
        card.IsSwapped = false;
        if (!cardPresenter.IsExpanded) throw new InvalidDataException("Programmatic card expansion did not reach the visual template.");
        var navigation = new MyRadioButton { Text = "Navigation" };
        var otherNavigation = new MyRadioButton { Text = "Other" };
        form.Children.Add(navigation);
        form.Children.Add(otherNavigation);
        attach.Invoke(null, [navigation]);
        var navigationPresenter = (ThemePresenter)navigation.Child;
        navigation.SetChecked(true, false, false);
        if (!navigationPresenter.IsChecked) throw new InvalidDataException("Navigation selection did not reach its theme.");
        // The existing radio group requires one selected item; choose a sibling
        // rather than incorrectly expecting an empty group to be permitted.
        otherNavigation.SetChecked(true, false, false);
        if (navigationPresenter.IsChecked) throw new InvalidDataException("Navigation deselection did not reach its theme.");
        Console.WriteLine("Programmatic danger/collapse/expand/select/deselect state updates reach theme presenters.");
    }
    private sealed class MemoryStorage : ConfigStorage
    {
        private readonly Dictionary<string, object?> _values = [];
        protected override bool OnAccess<TKey, TValue>(StorageAction action, ref TKey key,
            [NotNullWhen(true)] ref TValue value, object? argument)
        {
            string name = key?.ToString() ?? "";
            if (action == StorageAction.Set) { _values[name] = value; return false; }
            if (action == StorageAction.Delete) { _values.Remove(name); return false; }
            if (action == StorageAction.Exists) return _values.ContainsKey(name);
            if (_values.TryGetValue(name, out object? stored) && stored is TValue typed) { value = typed; return true; }
            return false;
        }
    }
}
