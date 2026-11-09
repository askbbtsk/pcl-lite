using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class Program
{
    private sealed record ReviewedFeatureChange(string SourcePath, string Sha256, string[] Members, string Reason);
    private static ReviewedFeatureChange[]? ReviewedChanges;

    private static void VerifySharedJavaRules(Observations baseline, Observations current, List<string> errors)
    {
        const string originalKey = "PCL.ModLaunch::MethodDeclaration::McLaunchJava(ModLoader.LoaderTask<int, int> task)";
        const string resolverKey = "PCL.ModLaunch::MethodDeclaration::ResolveJavaConstraints(McInstance instance)";
        if (!current.Methods.TryGetValue(resolverKey, out var resolver)) return;
        var original = baseline.Methods[originalKey];
        var originalStages = original.Body!.Statements.TakeWhile(statement => statement is not LockStatementSyntax).ToArray();
        var expected = SyntaxFactory.Block(originalStages);
        var references = expected.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(node => node.ToString() == "ModInstanceList.McMcInstanceSelected").ToArray();
        expected = expected.ReplaceNodes(references, (_, _) => SyntaxFactory.IdentifierName("instance"));
        var actualStages = resolver.Body!.Statements.Take(originalStages.Length).ToArray();
        if (Tokens(expected) != Tokens(SyntaxFactory.Block(actualStages))
            || resolver.Body.Statements.Count != originalStages.Length + 1
            || resolver.Body.Statements.Last() is not ReturnStatementSyntax)
            errors.Add("Shared Java requirements changed from the original ordered rules.");
    }

    private static bool IsReviewedFeatureChange(string key, Observations current)
    {
        // Feature development may intentionally change a former refactor contract. Approval is
        // explicit per member and pinned to the exact normalized source, never a wildcard skip.
        ReviewedChanges ??= File.Exists("tools/SourceStructure/feature-changes.json")
            ? JsonSerializer.Deserialize<ReviewedFeatureChange[]>(File.ReadAllText("tools/SourceStructure/feature-changes.json"))
              ?? throw new InvalidDataException("Invalid reviewed feature change manifest.")
            : [];
        foreach (var change in ReviewedChanges)
        {
            if (string.IsNullOrWhiteSpace(change.Reason)
                || !change.Members.Contains(key, StringComparer.Ordinal)
                || !current.Sources.TryGetValue(key, out var source)
                || source != change.SourcePath) continue;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Current(source))));
            if (hash.Equals(change.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
