using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    private sealed record ReviewedFeatureChange(string SourcePath, string Sha256, string[] Members, string Reason);
    private static ReviewedFeatureChange[]? ReviewedChanges;

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
