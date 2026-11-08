using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;
using CompFile = PCL.ModLocalComp.LocalCompFile;

namespace PCL;

/// <summary>Starts the regular PCL launch only after the user has reviewed required mod dependencies.</summary>
internal static class ModLaunchDependencyPrecheck
{
    private sealed record Issue(CompFile Owner, ModJarInJarIndex.DepRow Dependency, JijDepStatus Status,
        IReadOnlyList<CompFile> DisabledProviders);

    public static bool Confirm(McInstance instance)
    {
        if (instance?.Info is null || !(instance.Info.HasForge || instance.Info.HasNeoForge ||
                                        instance.Info.HasFabric || instance.Info.HasQuilt || instance.Info.HasLabyMod))
            return true;

        var modsPath = instance.Info.HasLabyMod
            ? Path.Combine(instance.PathIndie, "labymod-neo", "fabric", instance.Info.VanillaName, "mods")
            : Path.Combine(instance.PathIndie, "mods");
        if (!Directory.Exists(modsPath)) return true;

        var issues = Scan(instance, modsPath);
        if (issues.Count == 0) return true;

        var enableCandidates = issues
            .Where(issue => issue.Status == JijDepStatus.Disabled && issue.DisabledProviders.Count == 1)
            .Select(issue => issue.DisabledProviders[0])
            .Distinct()
            .ToList();

        var choice = ShowPrompt(instance, issues, enableCandidates.Count > 0);
        if (choice == 0) return false;
        if (choice == 2) return true;
        if (choice != 1 || enableCandidates.Count == 0) return false;

        var failedEnables = new List<string>();
        foreach (var provider in enableCandidates)
        {
            try
            {
                var destination = provider.RawPath;
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    failedEnables.Add(provider.FileName);
                    continue;
                }
                File.Move(provider.path, destination);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "启动前启用模组前置失败：" + provider.path, ModBase.LogLevel.Hint);
                failedEnables.Add(provider.FileName);
            }
        }

        var remainingIssues = Scan(instance, modsPath);
        if (remainingIssues.Count == 0) return true;
        var details = BuildDetails(remainingIssues);
        if (failedEnables.Count > 0)
            details += Environment.NewLine + Environment.NewLine +
                       Lang.Text("Minecraft.Launch.Dependency.EnableFailed", string.Join(", ", failedEnables));

        var proceed = false;
        ModBase.RunInUiWait(() =>
            proceed = ModMain.MyMsgBox(details,
                Lang.Text("Minecraft.Launch.Dependency.Title"),
                Lang.Text("Minecraft.Launch.Dependency.Continue"),
                Lang.Text("Common.Action.Cancel")) == 1);
        return proceed;
    }

    private static List<Issue> Scan(McInstance instance, string modsPath)
    {
        var mods = new List<CompFile>();
        ModJarInJarCache.UseInstance(instance.PathInstance);
        ModLocalComp.JijScanContext = new ModJarInJar.ScanContext();
        try
        {
            foreach (var path in Directory.EnumerateFiles(modsPath)
                         .Where(CompFile.IsModFile))
            {
                var mod = new CompFile(path);
                mod.Load();
                if (mod.State == CompFile.LocalFileStatus.Unavailable) continue;
                if (mod.JijPending && !mod.ResolveJijNow())
                    ModBase.Log("启动前未能完整解析内嵌模组：" + path, ModBase.LogLevel.Developer);
                mods.Add(mod);
            }
            ModJarInJarCache.Flush();
        }
        finally
        {
            ModLocalComp.JijScanContext = null;
            ModJarInJarCache.UseInstance(null);
        }

        var index = new ModJarInJarIndex(mods, instance.Info.VanillaName);
        var issues = new List<Issue>();
        foreach (var mod in mods.Where(mod => mod.State == CompFile.LocalFileStatus.Fine))
        foreach (var dependency in index.GetDependencies(mod)
                     .Where(dependency => !dependency.Optional && !ModDependencyIds.IsPlatform(dependency.DepId)))
        {
            var status = index.Analyze(mod, dependency);
            if (status is JijDepStatus.Installed or JijDepStatus.Bundled) continue;
            issues.Add(new Issue(mod, dependency, status,
                status == JijDepStatus.Disabled ? index.GetDisabledProviders(mod, dependency) : Array.Empty<CompFile>()));
        }
        return issues;
    }

    private static int ShowPrompt(McInstance instance, List<Issue> issues, bool canEnable)
    {
        var result = 0;
        var details = BuildDetails(issues);
        var message = Lang.Text("Minecraft.Launch.Dependency.Message", instance.Name, details);
        ModBase.RunInUiWait(() =>
        {
            result = canEnable
                ? ModMain.MyMsgBox(message, Lang.Text("Minecraft.Launch.Dependency.Title"),
                    Lang.Text("Minecraft.Launch.Dependency.Enable"),
                    Lang.Text("Minecraft.Launch.Dependency.Continue"), Lang.Text("Common.Action.Cancel"))
                : ModMain.MyMsgBox(message, Lang.Text("Minecraft.Launch.Dependency.Title"),
                    Lang.Text("Minecraft.Launch.Dependency.Continue"), Lang.Text("Common.Action.Cancel"));
            if (!canEnable && result == 1) result = 2;
            else if (!canEnable && result == 2) result = 0;
        });
        return result;
    }

    private static string BuildDetails(List<Issue> issues)
    {
        var lines = issues.Take(12).Select(issue =>
        {
            var owner = string.IsNullOrWhiteSpace(issue.Owner.Name) ? issue.Owner.FileName : issue.Owner.Name;
            var status = Lang.Text(issue.Status switch
            {
                JijDepStatus.Disabled => "Instance.Resource.Mod.JarInJar.Dep.Disabled",
                JijDepStatus.VersionMismatch => "Instance.Resource.Mod.JarInJar.Dep.VersionMismatch",
                _ => "Instance.Resource.Mod.JarInJar.Dep.Missing"
            });
            return Lang.Text("Minecraft.Launch.Dependency.Entry", owner, issue.Dependency.DepId,
                issue.Dependency.Raw ?? "*", status);
        }).ToList();
        if (issues.Count > lines.Count)
            lines.Add(Lang.Text("Minecraft.Launch.Dependency.More", issues.Count - lines.Count));
        return string.Join(Environment.NewLine, lines);
    }
}
