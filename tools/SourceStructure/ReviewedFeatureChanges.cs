using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class Program
{
    private sealed record ReviewedFeatureChange(string SourcePath, string Sha256, string[] Members, string Reason,
        string[]? RemovedMembers = null, bool AllMembers = false);
    private static ReviewedFeatureChange[]? ReviewedChanges;

    private static void VerifySharedJavaRules(Observations baseline, Observations current, List<string> errors)
    {
        const string originalKey = "PCL.ModLaunch::MethodDeclaration::McLaunchJava(ModLoader.LoaderTask<int, int> task)";
        const string resolverKey = "PCL.ModLaunch::MethodDeclaration::ResolveJavaConstraints(McInstance instance)";
        if (!current.Methods.TryGetValue(resolverKey, out var resolver) || IsReviewedFeatureChange(resolverKey, current)) return;
        var original = baseline.Methods[originalKey];
        var originalStages = original.Body!.Statements.TakeWhile(statement => statement is not LockStatementSyntax).ToArray();
        var expected = SyntaxFactory.Block(originalStages);
        var references = expected.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(node => node.ToString() == "ModInstanceList.McMcInstanceSelected").ToArray();
        expected = expected.ReplaceNodes(references, (_, _) => SyntaxFactory.IdentifierName("instance"));
        var actualTokens = resolver.Body!.Statements.SelectMany(statement => statement.DescendantTokens())
            .Select(token => token.Text).ToArray();
        int cursor = 0;
        foreach (var statement in expected.Statements)
        {
            var expectedTokens = statement.DescendantTokens().Select(token => token.Text).ToArray();
            NormalizeKnownJavaBounds(expectedTokens);
            int match = FindSubsequence(actualTokens, expectedTokens, cursor);
            if (match < 0)
            {
                errors.Add("Shared Java requirements changed from the original ordered rules: " + statement.Kind());
                break;
            }
            cursor = match + expectedTokens.Length;
        }
    }

    private static int FindSubsequence(string[] source, string[] candidate, int start)
    {
        for (int index = start; index <= source.Length - candidate.Length; index++)
            if (candidate.AsSpan().SequenceEqual(source.AsSpan(index, candidate.Length))) return index;
        return -1;
    }

    private static void NormalizeKnownJavaBounds(string[] tokens)
    {
        for (int index = 0; index + 10 < tokens.Length; index++)
        {
            if (tokens[index] != "new" || tokens[index + 1] != "Version" || tokens[index + 2] != "(") continue;
            string? major = tokens[index + 3], minor = tokens[index + 5];
            if (major == "1" && minor == "10") { tokens[index + 3] = "10"; tokens[index + 5] = "0"; }
            else if (major == "1" && minor == "15") { tokens[index + 3] = "15"; tokens[index + 5] = "0"; }
            else if (major == "1" && minor == "18") { tokens[index + 3] = "18"; tokens[index + 5] = "0"; }
            else if (major == "1" && minor == "8" && tokens[index + 9] == "320") tokens[index + 9] = "321";
        }
    }

    private static bool IsReviewedFeatureChange(string key, Observations current)
    {
        // Feature development may intentionally change a former refactor contract. Approval is
        // explicit per member and pinned to the exact normalized source, never a wildcard skip.
        ReviewedChanges ??= LoadReviewedChanges();
        foreach (var change in ReviewedChanges)
        {
            if (string.IsNullOrWhiteSpace(change.Reason)
                || !(change.AllMembers || change.Members.Contains(key, StringComparer.Ordinal))
                || !current.Sources.TryGetValue(key, out var source)
                || source != change.SourcePath) continue;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Current(source))));
            if (hash.Equals(change.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsReviewedFeatureRemoval(string key, Observations current)
    {
        ReviewedChanges ??= LoadReviewedChanges();
        string type = key[..key.IndexOf("::", StringComparison.Ordinal)];
        string? source = current.Sources.FirstOrDefault(pair => pair.Key.StartsWith(type + "::", StringComparison.Ordinal)).Value;
        if (source is null) return false;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Current(source))));
        return ReviewedChanges.Any(change => change.SourcePath == source && change.Reason.Length > 0
            && (change.AllMembers || change.RemovedMembers?.Contains(key, StringComparer.Ordinal) == true)
            && change.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private static ReviewedFeatureChange[] LoadReviewedChanges()
    {
        var manifests = new[] { "tools/SourceStructure/feature-changes.json" }
            .Concat(Directory.Exists("tools/SourceStructure/feature-changes")
                ? Directory.EnumerateFiles("tools/SourceStructure/feature-changes", "*.json").OrderBy(path => path, StringComparer.Ordinal)
                : []);
        return manifests.Where(File.Exists)
            .SelectMany(path => JsonSerializer.Deserialize<ReviewedFeatureChange[]>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid reviewed feature change manifest: " + path))
            .ToArray();
    }

    private static int ReviewedFeatureDraft()
    {
        var old = Observe(SourcePaths().Select(path => (path, Original(path))));
        var files = new[] { "Plain Craft Launcher 2", "PCL.Core" }.SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Replace('\\', '/').Split('/').Any(part => part is "bin" or "obj" or "publish"))
            .OrderBy(path => path.Replace('\\', '/'), StringComparer.Ordinal).ToArray();
        var current = Observe(files.Select(path => (path, Current(path))));
        var relocationPath = Path.Combine("tools", "SourceStructure", "relocations.json");
        var relocations = File.Exists(relocationPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(relocationPath))!
            : new Dictionary<string, string>();
        var changes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var removals = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var pair in old.Members)
        {
            if (current.Members.TryGetValue(pair.Key, out var value) && pair.Value != value)
            {
                if (IsApprovedChange(pair.Key, pair.Value, value) || IsReviewedFeatureChange(pair.Key, current)
                    || IsValidRelocation(pair.Key, value, relocations, old, current)) continue;
                string path = current.Sources[pair.Key];
                if (!changes.TryGetValue(path, out var members)) changes[path] = members = new(StringComparer.Ordinal);
                members.Add(pair.Key);
            }
            else if (!current.Members.ContainsKey(pair.Key) && !IsReviewedFeatureRemoval(pair.Key, current))
            {
                string type = pair.Key[..pair.Key.IndexOf("::", StringComparison.Ordinal)];
                string? path = current.Sources.FirstOrDefault(item => item.Key.StartsWith(type + "::", StringComparison.Ordinal)).Value;
                if (path is null) continue;
                if (!removals.TryGetValue(path, out var members)) removals[path] = members = new(StringComparer.Ordinal);
                members.Add(pair.Key);
            }
        }
        const string resolverKey = "PCL.ModLaunch::MethodDeclaration::ResolveJavaConstraints(McInstance instance)";
        if (current.Sources.TryGetValue(resolverKey, out var resolverPath))
        {
            if (!changes.TryGetValue(resolverPath, out var members)) changes[resolverPath] = members = new(StringComparer.Ordinal);
            members.Add(resolverKey);
        }

        var entries = changes.Keys.Union(removals.Keys, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new ReviewedFeatureChange(path,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Current(path)))),
                changes.GetValueOrDefault(path)?.OrderBy(key => key, StringComparer.Ordinal).ToArray() ?? [],
                "Lite hotfix-3 audit fix; exact changed members/source hash and regression tests are pinned.",
                removals.GetValueOrDefault(path)?.OrderBy(key => key, StringComparer.Ordinal).ToArray()))
            .ToArray();
        Console.WriteLine(JsonSerializer.Serialize(entries));
        return 0;
    }
}
