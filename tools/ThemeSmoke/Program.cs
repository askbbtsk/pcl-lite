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
            var source = System.IO.Path.GetFullPath("examples/themes/SoftSlate");
            var service = new ThemePackageService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pcl-theme-smoke"),
                System.IO.Path.GetTempPath());
            var package = Task.Run(() => service.InspectAsync(source)).GetAwaiter().GetResult();
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
