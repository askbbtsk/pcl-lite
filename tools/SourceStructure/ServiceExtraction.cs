using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class Program
{
    private static int Extract(string path, string service)
    {
        path = path.Replace('\\', '/');
        var original = Current(path);
        var root = Parse(original);
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var methods = type.Members.OfType<MethodDeclarationSyntax>().ToArray();
        if (methods.Any(method => !method.Modifiers.Any(SyntaxKind.StaticKeyword) || method.Modifiers.Any(SyntaxKind.AsyncKeyword)))
            throw new InvalidDataException("Extraction requires static synchronous methods.");
        var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Single();
        var imports = original[..ns.SpanStart];
        var content = imports + "using static PCL." + type.Identifier.Text + ";\n\nnamespace " + ns.Name + ";\n\ninternal static class " + service + "\n{\n";
        foreach (var method in methods) content += Implementation(method).ToFullString() + "\n";
        content += "}\n";
        var replacement = original;
        foreach (var method in methods.Reverse())
            replacement = replacement.Remove(method.FullSpan.Start, method.FullSpan.Length).Insert(method.FullSpan.Start, Forward(method, service).ToFullString());
        var destination = Path.GetDirectoryName(path)!.Replace('\\', '/') + "/" + service + ".cs";
        if (File.Exists(destination)) throw new IOException("Service exists: " + destination);
        var mappingPath = "tools/SourceStructure/relocations.json";
        var mapping = File.Exists(mappingPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(Current(mappingPath))! : new();
        foreach (var method in methods) mapping[TypeName(type) + "::" + method.Kind() + "::" + MemberName(method)] = service;
        var patches = new List<string> { "*** Begin Patch\n*** Delete File: " + path + "\n*** End Patch" };
        AddFilePatches(patches, path, replacement);
        AddFilePatches(patches, destination, content);
        if (File.Exists(mappingPath)) patches.Add("*** Begin Patch\n*** Delete File: " + mappingPath + "\n*** End Patch");
        AddFilePatches(patches, mappingPath, JsonSerializer.Serialize(mapping, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(patches));
        return 0;
    }

    private static MethodDeclarationSyntax Implementation(MethodDeclarationSyntax method)
    {
        var modifiers = method.Modifiers.Select(token => token.IsKind(SyntaxKind.PrivateKeyword)
            ? SyntaxFactory.Token(token.LeadingTrivia, SyntaxKind.InternalKeyword, token.TrailingTrivia) : token);
        return method.WithModifiers(SyntaxFactory.TokenList(modifiers));
    }

    private static MethodDeclarationSyntax Forward(MethodDeclarationSyntax method, string service)
    {
        var arguments = string.Join(", ", method.ParameterList.Parameters.Select(parameter =>
            string.Join(" ", parameter.Modifiers.Where(token => token.IsKind(SyntaxKind.RefKeyword) || token.IsKind(SyntaxKind.OutKeyword)
                || token.IsKind(SyntaxKind.InKeyword)).Select(token => token.Text).Append(parameter.Identifier.Text))));
        var generic = method.TypeParameterList is null ? "" : "<" + string.Join(",", method.TypeParameterList.Parameters.Select(parameter => parameter.Identifier.Text)) + ">";
        var expression = SyntaxFactory.ParseExpression(service + "." + method.Identifier.Text + generic + "(" + arguments + ")");
        return method.WithBody(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expression))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
    }
}
