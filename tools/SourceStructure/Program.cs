using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Read-only source analysis. Patch generation writes to stdout; only Codex applies patches.
internal static partial class Program
{
    private const string Baseline = "93808e4f";
    private static CSharpParseOptions ParseOptions = new(LanguageVersion.Preview,
        preprocessorSymbols: new[] { "DEBUG", "TRACE" });
    private static string Repository = Directory.GetCurrentDirectory();
    private static Dictionary<string, string>? BaselineSources;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length == 0) throw new ArgumentException("Use inventory, inspect, plan or verify.");
            return args[0] switch
            {
                "inventory" => Inventory(),
                "inspect" => Inspect(args[1]),
                "plan" => Plan(args.Skip(1).ToArray()),
                "split" => Split(args[1]),
                "extract" => Extract(args[1], args[2], args.Skip(3).ToArray()),
                "types" => SeparateTypes(args[1]),
                "verify" => VerifyConfiguration(args.Skip(1).FirstOrDefault() ?? "DEBUG"),
                "report" => Report(args.Contains("--complete", StringComparer.Ordinal)),
                _ => throw new ArgumentException("Unknown command."),
            };
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static string Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(error);
        return output;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimStart('\uFEFF');
    private static int Lines(string text) => Normalize(text).Split('\n').Length;
    private static string Tokens(SyntaxNode node) => string.Join("\u001f", node.DescendantTokens().Select(token => token.Text));
    private static CompilationUnitSyntax Parse(string text) => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, ParseOptions).GetRoot();
    private static string[] SourcePaths() => Git("ls-tree", "-r", "--name-only", Baseline).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) && (path.StartsWith("Plain Craft Launcher 2/") || path.StartsWith("PCL.Core/")))
        .ToArray();
    private static string Original(string path)
    {
        if (BaselineSources is null)
        {
            var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("archive"); info.ArgumentList.Add("--format=zip"); info.ArgumentList.Add(Baseline);
            using var process = Process.Start(info)!;
            using var memory = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(memory);
            var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException(error);
            memory.Position = 0;
            using var zip = new ZipArchive(memory);
            BaselineSources = new(StringComparer.Ordinal);
            foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".cs", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(entry.Open());
                BaselineSources[entry.FullName] = Normalize(reader.ReadToEnd());
            }
        }
        return BaselineSources[path];
    }
    private static string Current(string path) => Normalize(File.ReadAllText(Path.Combine(Repository, path)));

    private static int Inventory()
    {
        foreach (var item in SourcePaths().Select(path => (Path: path, Lines: Lines(Original(path))))
                     .Where(item => item.Lines > 1000).OrderByDescending(item => item.Lines))
            Console.WriteLine($"{item.Lines,5} {item.Path}");
        return 0;
    }

    private static int Inspect(string path)
    {
        var text = Current(path);
        var root = Parse(text);
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            Console.WriteLine($"TYPE {TypeName(type)} ({Lines(type.ToFullString())} lines)");
            foreach (var member in type.Members)
                Console.WriteLine($"  {member.GetLocation().GetLineSpan().StartLinePosition.Line + 1,5} {Lines(member.ToFullString()),4} {member.Kind(),28} {MemberName(member)}");
        }
        return 0;
    }

    private static string TypeName(BaseTypeDeclarationSyntax type)
    {
        static string Name(BaseTypeDeclarationSyntax node) => node.Identifier.Text + (node is TypeDeclarationSyntax { TypeParameterList.Parameters.Count: > 0 } generic ? "`" + generic.TypeParameterList.Parameters.Count : "");
        var names = type.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(Name).Append(Name(type));
        var ns = string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString()));
        return ns + "." + string.Join("+", names);
    }

    private static string MemberName(MemberDeclarationSyntax member) => member switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.Text,
        MethodDeclarationSyntax method => method.ExplicitInterfaceSpecifier + method.Identifier.Text + method.TypeParameterList + method.ParameterList,
        ConstructorDeclarationSyntax constructor => (constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor") + constructor.ParameterList,
        PropertyDeclarationSyntax property => property.ExplicitInterfaceSpecifier + property.Identifier.Text,
        FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(variable => variable.Identifier.Text)),
        EventFieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(variable => variable.Identifier.Text)),
        EventDeclarationSyntax declaration => declaration.Identifier.Text,
        DelegateDeclarationSyntax declaration => declaration.Identifier.Text,
        _ => member.ToString().Split('\n')[0],
    };

    private sealed record Observations(Dictionary<string, string> Members, Dictionary<string, List<string>> Initializers,
        Dictionary<string, MethodDeclarationSyntax> Methods, Dictionary<string, string> Sources);

    private static Observations Observe(IEnumerable<(string Path, string Text)> sources)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        var initializers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var methods = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
        var sourcesByMember = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, text) in sources)
        {
            var root = Parse(text);
            var syntaxErrors = root.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            if (syntaxErrors.Length > 0)
                throw new InvalidDataException("Invalid C# syntax in " + path + ": " + string.Join("; ", syntaxErrors.Select(error => error.ToString())));
            foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var name = TypeName(type);
                var header = type is TypeDeclarationSyntax declaration
                    ? string.Join("|", declaration.Modifiers.Where(token => !token.IsKind(SyntaxKind.PartialKeyword)).Select(token => token.Text))
                      + ":" + declaration.Keyword.Text + ":" + declaration.TypeParameterList
                    : type.Kind().ToString();
                var headerKey = name + "::type";
                if (members.TryGetValue(headerKey, out var existing))
                    members[headerKey] = string.Join(";", existing.Split(';').Append(header).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
                else members[headerKey] = header;
                if (type is EnumDeclarationSyntax enumeration)
                {
                    members[name + "::enum"] = Tokens(enumeration);
                    continue;
                }
                if (type is not TypeDeclarationSyntax container) continue;
                if (container.BaseList is not null) members[name + "::bases"] = Tokens(container.BaseList);
                if (container.ConstraintClauses.Count > 0) members[name + "::constraints"] = string.Join("|", container.ConstraintClauses.Select(Tokens));
                if (container.AttributeLists.Count > 0) members[name + "::attributes"] = string.Join("|", container.AttributeLists.Select(Tokens));
                foreach (var member in container.Members.Where(member => member is not BaseTypeDeclarationSyntax))
                {
                    var key = name + "::" + member.Kind() + "::" + MemberName(member);
                    if (!members.TryAdd(key, Tokens(member))) throw new InvalidDataException("Duplicate member: " + key);
                    sourcesByMember[key] = path.Replace('\\', '/');
                    if (member is MethodDeclarationSyntax method) methods[key] = method;
                    if (member is FieldDeclarationSyntax field && field.Declaration.Variables.Any(variable => variable.Initializer is not null)
                        || member is PropertyDeclarationSyntax { Initializer: not null }
                        || member is EventFieldDeclarationSyntax ev && ev.Declaration.Variables.Any(variable => variable.Initializer is not null))
                    {
                        if (!initializers.TryGetValue(name, out var sequence)) initializers[name] = sequence = new();
                        sequence.Add(key);
                    }
                }
            }
        }
        return new(members, initializers, methods, sourcesByMember);
    }

    private static int Verify()
    {
        var old = Observe(SourcePaths().Select(path => (path, Original(path))));
        var files = new[] { "Plain Craft Launcher 2", "PCL.Core" }.SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Replace('\\', '/').Split('/').Any(part => part is "bin" or "obj" or "publish"))
            .OrderBy(path => path.Replace('\\', '/'), StringComparer.Ordinal).ToArray();
        var current = Observe(files.Select(path => (path, Current(path))));
        var errors = new List<string>();
        var relocationPath = Path.Combine("tools", "SourceStructure", "relocations.json");
        var relocations = File.Exists(relocationPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(relocationPath))! : new();
        foreach (var pair in old.Members)
            if (!current.Members.TryGetValue(pair.Key, out var value)) errors.Add("Missing member: " + pair.Key);
            else if (pair.Value != value && !IsApprovedChange(pair.Key, pair.Value, value)
                     && !IsReviewedFeatureChange(pair.Key, current))
            {
                if (!relocations.TryGetValue(pair.Key, out var service) || !old.Methods.TryGetValue(pair.Key, out var method)
                    || Tokens(Forward(method, service.Split('.').Last())) != value
                    || !current.Methods.TryGetValue((service.Contains('.') ? service : "PCL." + service) + pair.Key[pair.Key.IndexOf("::", StringComparison.Ordinal)..], out var implementation)
                    || Tokens(Implementation(method)) != Tokens(implementation))
                    errors.Add("Changed member: " + pair.Key);
            }
        foreach (var pair in old.Initializers)
            if (!current.Initializers.TryGetValue(pair.Key, out var value)
                || !pair.Value.SequenceEqual(value.Where(key => pair.Value.Contains(key, StringComparer.Ordinal))))
                errors.Add("Initializer order changed: " + pair.Key);
        foreach (var error in errors) Console.Error.WriteLine(error);
        Console.WriteLine($"Compared {old.Members.Count} existing type/member contracts with baseline {Baseline}; {errors.Count} differences.");
        return errors.Count == 0 ? 0 : 1;
    }

    private static int VerifyConfiguration(string configuration)
    {
        ParseOptions = ParseOptions.WithPreprocessorSymbols(configuration, "TRACE");
        Console.WriteLine("Checking configuration: " + configuration);
        return Verify();
    }

    private static int Report(bool complete)
    {
        var targets = SourcePaths().Where(path => Lines(Original(path)) > 1000).ToArray();
        var remaining = 0;
        Console.WriteLine("| Original source | Before | Current entry | Status |");
        Console.WriteLine("|---|---:|---:|---|");
        foreach (var path in targets)
        {
            var lines = Lines(Current(path));
            if (lines > 1000) remaining++;
            Console.WriteLine($"| {path} | {Lines(Original(path))} | {lines} | {(lines <= 1000 ? "split" : "pending")} |");
        }
        var newFiles = Git("diff", "--name-only", Baseline, "--").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Concat(Git("ls-files", "--others", "--exclude-standard").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) && !targets.Contains(path) && File.Exists(path));
        foreach (var path in newFiles.Distinct())
            if (Lines(Current(path)) > 1000) { Console.Error.WriteLine("New/changed source exceeds 1000 lines: " + path); remaining++; }
        Console.WriteLine($"Remaining original files: {remaining} / {targets.Length}.");
        return complete && remaining > 0 ? 1 : 0;
    }

    private static bool IsApprovedChange(string key, string original, string current)
    {
        // Explicit user-requested UI removal, independent of the mechanical refactor.
        if (key == "PCL.PageWelcome::MethodDeclaration::Build()")
        {
            foreach (var statement in new[] { "PanRoot.Children.Add(_BuildRecentCard());", "PanRoot.Children.Add(_BuildLastPlayedWorldCard());" })
                original = original.Replace(Tokens(SyntaxFactory.ParseStatement(statement)) + "\u001f", "", StringComparison.Ordinal);
            return original == current;
        }
        return false;
    }
}
