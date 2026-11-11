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
            return 0;
        }
        finally { application.Shutdown(); }
    }
}
