using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Instances;

namespace PCL.Core.Test.Minecraft.Instances;

/// <summary>Compile the actual shipped getter, replacing only launcher startup/file/log dependencies.</summary>
[TestClass]
public sealed class InstanceJsonInheritanceTests
{
    private string _root = null!;
    private AssemblyLoadContext _context = null!;
    private Type _instance = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLJsonInheritanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        using var input = typeof(InstanceJsonInheritanceTests).Assembly.GetManifestResourceStream("McInstanceJsonSource");
        Assert.IsNotNull(input);
        using var reader = new StreamReader(input);
        var syntax = CSharpSyntaxTree.ParseText(reader.ReadToEnd(), new CSharpParseOptions(LanguageVersion.Preview));
        var property = syntax.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "JsonObject");
        var stub = """
            using System;
            using System.IO;
            using System.Collections.Generic;
            using System.Linq;
            using System.Text.Json.Nodes;
            using PCL.Core.Utils;
            namespace PCL.InheritanceFixture;
            public class McInstance
            {
                public static readonly Dictionary<string, McInstance> Created = new(StringComparer.OrdinalIgnoreCase);
                public string PathInstance { get; }
                public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(PathInstance));
                public McInstance(string directory)
                {
                    PathInstance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
                    Created[PathInstance] = this;
                }
                public string JsonText => File.ReadAllText(Path.Combine(PathInstance, Name + ".json"));
                public bool IsHmclFormatJson { get; set; }
                private string _parent;
                public string InheritInstanceName => _parent ??= (JsonObject["inheritsFrom"] ?? "").ToString();
            """ + property.ToFullString() + """
            }
            internal static class ModBase
            {
                public enum LogLevel { Debug }
                public static void Log(params object[] ignored) {}
                public static JsonNode GetJson(string text) => JsonNode.Parse(text);
                public static double Val(string value) => double.TryParse(value, out var number) ? number : 0;
                public static string GetPathFromFullPath(string directory) =>
                    Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) + Path.DirectorySeparatorChar;
            }
            internal static class Lang
            {
                public static string Text(string key, params object[] args) => key + ": " + string.Join(", ", args);
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(VersionInheritanceGuard).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("InstanceJsonGetter_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(stub, new CSharpParseOptions(LanguageVersion.Preview))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        output.Position = 0;
        _context = new AssemblyLoadContext(compilation.AssemblyName!, isCollectible: true);
        _instance = _context.LoadFromStream(output).GetType("PCL.InheritanceFixture.McInstance")!;
    }

    private void Write(string name, string json)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
    }
    private object Create(string name) => Activator.CreateInstance(_instance, Path.Combine(_root, name))!;
    private JsonObject Resolve(object instance) => (JsonObject)_instance.GetProperty("JsonObject")!.GetValue(instance)!;

    [TestMethod]
    public void ActualGetterRejectsCyclesAndDoesNotCacheThePartiallyMergedObject()
    {
        Write("A", """{"id":"A","inheritsFrom":"B","childOnly":true}""");
        Write("B", """{"id":"B","inheritsFrom":"A"}""");
        var source = Create("A");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.Throws<TargetInvocationException>(() => Resolve(source));
            Assert.IsInstanceOfType<VersionInheritanceException>(error.InnerException);
        }
        Write("B", """{"id":"B","parentOnly":true}""");
        Assert.AreEqual("A", Resolve(source)["id"]!.ToString());
        Assert.IsTrue(Resolve(source)["parentOnly"]!.GetValue<bool>());
    }

    [TestMethod]
    public void ActualGetterRejectsSelfInheritanceAndDepthOverflow()
    {
        Write("self", """{"id":"self","inheritsFrom":"self"}""");
        Assert.IsInstanceOfType<VersionInheritanceException>(
            Assert.Throws<TargetInvocationException>(() => Resolve(Create("self"))).InnerException);
        for (int index = 0; index < 66; index++)
            Write(index.ToString(), $$"""{"id":"{{index}}","inheritsFrom":"{{index + 1}}"}""");
        Assert.IsInstanceOfType<VersionInheritanceException>(
            Assert.Throws<TargetInvocationException>(() => Resolve(Create("0"))).InnerException);
    }

    [TestMethod]
    public void NormalInheritancePreservesChildOverridesAndParentBytes()
    {
        const string parent = """{"id":"base","same":"parent","parentOnly":true,"libraries":[{"name":"base-lib"}]}""";
        Write("base", parent);
        Write("child", """{"id":"child","inheritsFrom":"base","same":"child","libraries":[{"name":"child-lib"}]}""");
        var result = Resolve(Create("child"));
        Assert.AreEqual("child", result["id"]!.ToString());
        Assert.AreEqual("child", result["same"]!.ToString());
        Assert.IsTrue(result["parentOnly"]!.GetValue<bool>());
        Assert.AreEqual(2, result["libraries"]!.AsArray().Count);
        var constructed = (System.Collections.IDictionary)_instance.GetField("Created")!.GetValue(null)!;
        var parentInstance = constructed[Path.Combine(_root, "base") + Path.DirectorySeparatorChar]!;
        Assert.AreEqual("base", Resolve(parentInstance)["id"]!.ToString(), "Merging a child must not mutate the parent's in-memory JSON.");
        Assert.AreEqual(parent, File.ReadAllText(Path.Combine(_root, "base", "base.json")));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context?.Unload();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
