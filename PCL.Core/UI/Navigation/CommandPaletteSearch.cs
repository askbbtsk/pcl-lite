using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;

namespace PCL.Core.UI.Navigation;

public sealed record CommandPaletteEntry(string Id, string Title, string Description, string Category, string Keywords = "");

public readonly record struct CommandPalettePageVisibility(
    bool HideFunctionSelect = false,
    bool HidePageDownload = false,
    bool HidePageSetup = false,
    bool HidePagePlugins = false,
    bool HideSetupLaunch = false,
    bool HideSetupJava = false,
    bool HideSetupUi = false,
    bool HideSetupLauncherLanguage = false,
    bool HideSetupGameManage = false,
    bool HideSetupLauncherMisc = false,
    bool HideSetupUpdate = false,
    bool HideSetupAbout = false,
    bool HiddenForceShow = false);

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

    public static bool IsPageHidden(string commandId, CommandPalettePageVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        if (visibility.HiddenForceShow) return false;
        return commandId switch
        {
            "instances" => visibility.HideFunctionSelect,
            "install" or "mods" => visibility.HidePageDownload,
            "plugins" => visibility.HidePagePlugins,
            "java" => visibility.HidePageSetup || visibility.HideSetupJava,
            "launch-settings" => visibility.HidePageSetup || visibility.HideSetupLaunch,
            "appearance" => visibility.HidePageSetup || visibility.HideSetupUi,
            "language" => visibility.HidePageSetup || visibility.HideSetupLauncherLanguage,
            "game-settings" => visibility.HidePageSetup || visibility.HideSetupGameManage,
            "misc" => visibility.HidePageSetup || visibility.HideSetupLauncherMisc,
            "update" => visibility.HidePageSetup || visibility.HideSetupUpdate,
            "about" => visibility.HidePageSetup || visibility.HideSetupAbout,
            _ => false
        };
    }

    public static IReadOnlyList<CommandPaletteEntry> FilterVisiblePages(
        IEnumerable<CommandPaletteEntry> entries, CommandPalettePageVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(entry => !IsPageHidden(entry.Id, visibility)).ToArray();
    }

    public static void SwitchManagedInstance<TInstance>(
        TInstance instance,
        bool isAlreadyInInstanceSetup,
        Action<TInstance> bindInstance,
        Action navigateToOverview,
        Action? refreshSidebar = null,
        Action? refreshOverview = null,
        Action? refreshTitle = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(bindInstance);
        ArgumentNullException.ThrowIfNull(navigateToOverview);

        bindInstance(instance);
        navigateToOverview();
        if (!isAlreadyInInstanceSetup) return;
        refreshSidebar?.Invoke();
        refreshOverview?.Invoke();
        refreshTitle?.Invoke();
    }

    public static bool ShouldActivateCloseButtonOnKey(Key key, bool isCloseButtonFocused) =>
        isCloseButtonFocused && key is Key.Enter or Key.Space;

    public static IReadOnlyList<TResult> ScanModFilesWithDeferredNestedArchive<TResult>(
        string directory,
        Func<string, bool> isModFile,
        Func<string, TResult> readModMetadata,
        Func<TResult, string> getSortTitle,
        Action enterDeferredMode,
        Action exitDeferredMode,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(isModFile);
        ArgumentNullException.ThrowIfNull(readModMetadata);
        ArgumentNullException.ThrowIfNull(getSortTitle);
        ArgumentNullException.ThrowIfNull(enterDeferredMode);
        ArgumentNullException.ThrowIfNull(exitDeferredMode);

        if (!Directory.Exists(directory)) return Array.Empty<TResult>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Directory,
            IgnoreInaccessible = false
        };

        enterDeferredMode();
        try
        {
            var results = new List<TResult>();
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                token.ThrowIfCancellationRequested();
                if (!isModFile(file)) continue;
                var item = readModMetadata(file);
                token.ThrowIfCancellationRequested();
                results.Add(item);
            }
            token.ThrowIfCancellationRequested();
            return results.OrderBy(getSortTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally
        {
            exitDeferredMode();
        }
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
