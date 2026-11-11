using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class Program
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly HashSet<string> NativeControls = new(StringComparer.Ordinal)
    {
        "Button", "RepeatButton", "ToggleButton", "CheckBox", "RadioButton", "ComboBox",
        "TextBox", "RichTextBox", "PasswordBox", "Slider", "ListBox", "ListView", "TreeView",
        "DataGrid", "TabControl", "ProgressBar", "DatePicker", "Calendar"
    };
    private static readonly string[] Configurations = ["DEBUG", "CI", "RELEASE", "BETA"];
    private sealed record Finding(string Path, int Line, string Control, string Fingerprint, string Configuration = "XAML");
    private sealed record ExceptionEntry(string Path, string Control, string Fingerprint, int Count, string Reason);
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray());

    public static int Main(string[] args)
    {
        try
        {
            if (args is ["--self-test"]) return SelfTest();
            if (args is ["--stdin-xaml", var path]) return Print(InspectXaml(path, Console.In.ReadToEnd()));
            if (args.Length != 0) throw new ArgumentException("Use no arguments, --self-test, or --stdin-xaml <display-path>.");
            return Audit();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("::error::UI smoke check failed: " + Escape(error.Message));
            return 1;
        }
    }

    private static int Audit()
    {
        var roots = new[] { "Plain Craft Launcher 2", "PCL.Core" };
        foreach (string root in roots)
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Run from the repository root: " + root);
        string[] files = roots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => !path.Replace('\\', '/').Split('/').Any(part =>
                part.Equals("obj", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("publish", StringComparison.OrdinalIgnoreCase)))
            .Where(path => Path.GetExtension(path) is ".xaml" or ".cs").Order(StringComparer.Ordinal).ToArray();
        var findings = new List<Finding>();
        foreach (string path in files)
        {
            string source = File.ReadAllText(path);
            string relative = path.Replace('\\', '/');
            if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) findings.AddRange(InspectXaml(relative, source));
            else foreach (string configuration in Configurations)
                findings.AddRange(InspectCSharp(relative, source, configuration));
        }
        var exceptions = JsonSerializer.Deserialize<ExceptionEntry[]>(File.ReadAllText("tools/UiSmoke/exceptions.json"))
            ?? throw new InvalidDataException("Invalid UI exception manifest.");
        var remaining = WithoutApproved(findings, exceptions);
        Console.WriteLine($"UI smoke: {files.Length} files, all four C# configurations; {findings.Count - remaining.Count} pinned existing template/special-purpose uses.");
        return Print(remaining);
    }

    private static List<Finding> WithoutApproved(IEnumerable<Finding> findings, IEnumerable<ExceptionEntry> exceptions)
    {
        var remaining = findings.ToList();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in exceptions)
        {
            string key = entry.Path + "|" + entry.Control + "|" + entry.Fingerprint;
            if (!keys.Add(key) || entry.Count <= 0 || string.IsNullOrWhiteSpace(entry.Reason)
                || entry.Fingerprint.Length != 64 || !entry.Fingerprint.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid or duplicate UI exception: " + key);
            var matches = remaining.Where(item => item.Path == entry.Path && item.Control == entry.Control
                && item.Fingerprint == entry.Fingerprint).ToArray();
            if (matches.Length != entry.Count)
                throw new InvalidDataException($"UI exception changed or is stale: {entry.Path}, {entry.Control}; expected {entry.Count}, found {matches.Length}. Review the exact use instead of broadening the exemption.");
            foreach (var match in matches) remaining.Remove(match);
        }
        return remaining;
    }

    private static IEnumerable<Finding> InspectXaml(string path, string source, int lineOffset = 0)
    {
        using var text = new StringReader(source);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        foreach (var node in document.Descendants())
        {
            bool wpf = node.Name.NamespaceName == Presentation
                || node.Name.NamespaceName is "clr-namespace:System.Windows.Controls;assembly=PresentationFramework"
                    or "clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework";
            if (!wpf || !NativeControls.Contains(node.Name.LocalName)) continue;
            int line = ((IXmlLineInfo)node).LineNumber + lineOffset;
            string fingerprint = Hash(node.ToString(SaveOptions.DisableFormatting));
            yield return new Finding(path, line, node.Name.LocalName, fingerprint);
        }
    }

    private static IEnumerable<Finding> InspectCSharp(string path, string source, string configuration)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview,
            preprocessorSymbols: [configuration, "TRACE"]), path);
        var root = tree.GetRoot();
        var nodes = root.DescendantNodes().Where(node => node is BaseObjectCreationExpressionSyntax
            or InvocationExpressionSyntax).ToArray();
        if (nodes.Length == 0) yield break;
        // Semantic binding catches using aliases, fully qualified names, target-typed new,
        // initializer assignments and factory return types. We never execute application code.
        var globals = CSharpSyntaxTree.ParseText("global using System; global using System.Windows.Controls; global using System.Windows.Controls.Primitives;",
            (CSharpParseOptions)tree.Options);
        var compilation = CSharpCompilation.Create("UiSmoke", [globals, tree], References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        foreach (var node in nodes)
        {
            int line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
            if (node is BaseObjectCreationExpressionSyntax creation)
            {
                var type = model.GetTypeInfo(creation).Type;
                string ns = type?.ContainingNamespace?.ToDisplayString() ?? "";
                if (type is not null && NativeControls.Contains(type.Name)
                    && ns is "System.Windows.Controls" or "System.Windows.Controls.Primitives")
                    yield return new Finding(path, line, type.Name, Hash(creation.NormalizeWhitespace().ToFullString()), configuration);
            }
            else if (node is InvocationExpressionSyntax invocation
                && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
                && method.ContainingType.ToDisplayString() == "System.Windows.Markup.XamlReader"
                && method.Name is "Parse" or "Load" && invocation.ArgumentList.Arguments.Count > 0
                && model.GetConstantValue(invocation.ArgumentList.Arguments[0].Expression) is { HasValue: true, Value: string xaml })
            {
                foreach (var finding in InspectXaml(path, xaml, line - 1))
                    yield return finding with { Configuration = configuration + "/inline-XAML" };
            }
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Escape(string text) => text.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
    private static int Print(IEnumerable<Finding> findings, bool report = true)
    {
        int count = 0;
        foreach (var finding in findings.Distinct())
        {
            count++;
            string path = Escape(finding.Path).Replace(",", "%2C").Replace(":", "%3A");
            if (report) Console.Error.WriteLine($"::error file={path},line={finding.Line},title=Native WPF control::{finding.Control} ({finding.Configuration}): use a PCL control. Fingerprint {finding.Fingerprint}");
        }
        if (report) Console.WriteLine(count == 0 ? "UI smoke passed: no unapproved native interactive controls." : $"UI smoke FAILED: {count} unapproved native control use(s).");
        return count == 0 ? 0 : 1;
    }
}
