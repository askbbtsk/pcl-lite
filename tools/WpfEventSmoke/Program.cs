using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using PCL;

// Exercise WPF's name-based event binding against the compiled production types.
// C# compilation alone accepts XAML handlers that EventConverter rejects at startup.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var assembly = typeof(FormMain).Assembly;
        var failures = new List<string>();
        int checkedBindings = 0;
        foreach (string path in new[]
        {
            "Plain Craft Launcher 2/FormMain.xaml",
            "Plain Craft Launcher 2/Controls/CommandPalette.xaml"
        })
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            string className = (string?)document.Root?.Attribute(xaml + "Class")
                ?? throw new InvalidDataException("Missing x:Class: " + path);
            Type rootType = assembly.GetType(className, throwOnError: true)!;
            // Bind without invoking constructors, handlers, configuration or game scans.
            // This object is never used as a WPF visual.
            object root = RuntimeHelpers.GetUninitializedObject(rootType);
            foreach (var element in document.Root!.DescendantsAndSelf())
            {
                string ns = element.Name.NamespaceName;
                if (!ns.StartsWith("clr-namespace:PCL", StringComparison.Ordinal)) continue;
                Type? control = assembly.GetType("PCL." + element.Name.LocalName);
                if (control is null) continue;
                foreach (var attribute in element.Attributes().Where(attribute => attribute.Name.Namespace == XNamespace.None))
                {
                    EventInfo? eventInfo = control.GetEvent(attribute.Name.LocalName);
                    if (eventInfo?.EventHandlerType is null) continue;
                    try
                    {
                        // Use the string overload used by System.Xaml.EventConverter;
                        // binding via MethodInfo can permit broader signatures and miss this bug.
                        _ = Delegate.CreateDelegate(eventInfo.EventHandlerType, root, attribute.Value);
                        checkedBindings++;
                    }
                    catch (Exception error)
                    {
                        failures.Add($"{path}: {control.Name}.{eventInfo.Name}=\"{attribute.Value}\": {error.Message}");
                    }
                }
            }
        }
        foreach (string failure in failures) Console.Error.WriteLine("::error::" + failure);
        Console.WriteLine($"Compiled WPF event bindings: {checkedBindings} passed, {failures.Count} failed.");
        if (failures.Count != 0) return 1;

        var application = new System.Windows.Application();
        try
        {
            // A failed/partial main-window load must tolerate ordinary input.
            var main = (FormMain)RuntimeHelpers.GetUninitializedObject(typeof(FormMain));
            var mouseDown = typeof(FormMain).GetMethod("FormMain_MouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var button in new[] { System.Windows.Input.MouseButton.Left, System.Windows.Input.MouseButton.XButton1 })
                mouseDown.Invoke(main, [main, new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, button)]);
            var keyDown = typeof(FormMain).GetMethod("FormMain_KeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var source = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters { Width = 1, Height = 1 });
            foreach (var key in new[] { System.Windows.Input.Key.Enter, System.Windows.Input.Key.Escape, System.Windows.Input.Key.F12 })
                keyDown.Invoke(main, [main, new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key)]);
            Console.WriteLine("Partial-window mouse and keyboard input guards passed.");
            VerifyNavigationGlass(main);
            return 0;
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyNavigationGlass(FormMain main)
    {
        var navigation = new System.Windows.Controls.Grid();
        var viewport = new System.Windows.Controls.Grid { Width = 0 };
        navigation.Children.Add(viewport);
        var toolbar = new TahoeGlassSurface();
        var host = new System.Windows.Controls.Grid();
        host.Children.Add(navigation);
        host.Children.Add(toolbar);
        var window = new System.Windows.Window
        {
            Content = host, Width = 300, Height = 100, ShowInTaskbar = false,
            Left = -10000, Top = -10000
        };
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(FormMain).GetField("PanTitleMain", fields)!.SetValue(main, navigation);
        typeof(FormMain).GetField("PanTitleSelectViewport", fields)!.SetValue(main, viewport);
        typeof(FormMain).GetField("TahoeToolbar", fields)!.SetValue(main, toolbar);
        MethodInfo update = typeof(LiquidGlassCore).GetMethod("UpdateTitleGlass",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            window.Show();
            // Keep the viewport unlaid out: hiding must work before a new layout pass.
            foreach (var state in new[]
            {
                (System.Windows.Visibility.Visible, true, System.Windows.Visibility.Visible),
                (System.Windows.Visibility.Visible, false, System.Windows.Visibility.Collapsed),
                (System.Windows.Visibility.Collapsed, false, System.Windows.Visibility.Collapsed),
                (System.Windows.Visibility.Collapsed, true, System.Windows.Visibility.Collapsed),
                (System.Windows.Visibility.Visible, true, System.Windows.Visibility.Visible)
            })
            {
                navigation.Visibility = state.Item1;
                navigation.IsHitTestVisible = state.Item2;
                update.Invoke(null, [main]);
                if (toolbar.Visibility != state.Item3)
                    throw new InvalidDataException("Navigation glass remained visible during child-page transition or failed to return.");
            }
            Console.WriteLine("Navigation glass hides during child-page transitions and returns with main navigation.");
        }
        finally { window.Close(); }
    }
}
