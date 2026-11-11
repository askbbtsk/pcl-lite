using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.UI.Navigation;

public sealed record CommandPaletteEntry(string Id, string Title, string Description, string Category, string Keywords = "");

/// <summary>Ranks a bounded view of an in-memory catalog; searching never executes a command.</summary>
public static class CommandPaletteSearch
{
    public static IReadOnlyList<CommandPaletteEntry> Search(
        IEnumerable<CommandPaletteEntry> entries, string? query, int limit = 60)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (limit <= 0) return Array.Empty<CommandPaletteEntry>();
        var tokens = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return entries.Select((entry, index) => (Entry: entry, Index: index, Score: Score(entry, tokens)))
            .Where(match => match.Score >= 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Index)
            .Take(limit)
            .Select(match => match.Entry)
            .ToArray();
    }

    private static int Score(CommandPaletteEntry entry, string[] tokens)
    {
        var score = 0;
        foreach (var token in tokens)
        {
            if (entry.Title.Equals(token, StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (entry.Title.StartsWith(token, StringComparison.OrdinalIgnoreCase)) score += 60;
            else if (entry.Title.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 40;
            else if (entry.Keywords.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 20;
            else if (entry.Category.Contains(token, StringComparison.OrdinalIgnoreCase)
                     || entry.Description.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 10;
            else return -1;
        }
        return score;
    }
}
