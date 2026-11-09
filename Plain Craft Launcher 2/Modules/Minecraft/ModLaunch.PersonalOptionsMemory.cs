using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.PersonalOptions;

namespace PCL;

public static partial class ModLaunch
{
    private static InstancePersonalOptionsMemory.PersonalOptionsRunSession? PreparePersonalOptionsMemory(McInstance instance)
    {
        try
        {
            var categories = Config.Launch.PersonalOptionsSync ? _GetPersonalOptionCategories() : PersonalOptionCategories.None;
            var request = new PersonalOptionsMemoryRequest(instance.PathInstance, instance.PathIndie,
                Path.Combine(Paths.SharedData, "Minecraft", "PersonalOptions", "profile.json"),
                Config.Launch.RememberInstanceControls, categories,
                instance.Info.vanilla >= new Version(1, 13), instance.Info.vanilla >= new Version(1, 16));
            return InstancePersonalOptionsMemory.Prepare(request, ReviewPersonalOptions,
                () => IsOptionsDirectoryInUse(instance.PathIndie, null),
                detail => ModBase.Log("[PersonalOptions] " + detail, ModBase.LogLevel.Hint));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "个人设置记忆/同步未完成；保留现存设置并继续启动", ModBase.LogLevel.Hint);
            return null;
        }
    }

    private static PersonalOptionsApplyChoice ReviewPersonalOptions(PersonalOptionsChangePreview preview)
    {
        var choice = PersonalOptionsApplyChoice.Skip;
        var lines = preview.Differences.Take(18).Select(item => Lang.Text("Minecraft.Launch.Controls.Entry",
            item.Key, item.Current ?? "—", item.Instance ?? "—", item.Shared ?? "—"));
        var detail = string.Join(Environment.NewLine, lines);
        if (preview.Differences.Count > 18)
            detail += Environment.NewLine + Lang.Text("Minecraft.Launch.Dependency.More", preview.Differences.Count - 18);
        ModBase.RunInUiWait(() =>
        {
            var message = Lang.Text("Minecraft.Launch.Controls.Preview", detail);
            if (preview.HasConflict)
            {
                var result = ModMain.MyMsgBox(message, Lang.Text("Minecraft.Launch.Controls.Title"),
                    Lang.Text("Minecraft.Launch.Controls.UseInstance"), Lang.Text("Minecraft.Launch.Controls.UseShared"),
                    Lang.Text("Minecraft.Launch.Controls.Skip"));
                choice = result == 1 ? PersonalOptionsApplyChoice.PreferInstance
                    : result == 2 ? PersonalOptionsApplyChoice.PreferShared : PersonalOptionsApplyChoice.Skip;
            }
            else
            {
                if (ModMain.MyMsgBox(message, Lang.Text("Minecraft.Launch.Controls.Title"),
                    Lang.Text("Minecraft.Launch.Controls.Apply"), Lang.Text("Minecraft.Launch.Controls.Skip")) == 1)
                    choice = PersonalOptionsApplyChoice.PreferInstance;
            }
        });
        return choice;
    }

    private static bool IsOptionsDirectoryInUse(string gameDirectory, Process? except)
    {
        try
        {
            var expected = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return ModWatcher.mcWatcherList.ToArray().Any(watcher => watcher.gameProcess is not null
                && !ReferenceEquals(watcher.gameProcess, except) && !watcher.gameProcess.HasExited
                && string.Equals(Path.GetFullPath(watcher.version.PathIndie).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "无法确认个人设置目录占用，跳过写入", ModBase.LogLevel.Developer);
            return true;
        }
    }

    private static void FinishPersonalOptionsMemory(InstancePersonalOptionsMemory.PersonalOptionsRunSession? session,
        McInstance instance, Process process, bool launchAborted)
    {
        if (session is null) return;
        try
        {
            var watcher = ModWatcher.mcWatcherList.ToArray().FirstOrDefault(item => ReferenceEquals(item.gameProcess, process));
            var reliable = !launchAborted && process.ExitCode == 0 && watcher is not null && watcher.HasReachedRunningState
                && (watcher.State is ModWatcher.Watcher.MinecraftState.Running or ModWatcher.Watcher.MinecraftState.Ended);
            session.Complete(reliable, () => IsOptionsDirectoryInUse(instance.PathIndie, process),
                Config.Launch.RememberInstanceControls, Config.Launch.PersonalOptionsSync, _GetPersonalOptionCategories());
        }
        catch (Exception ex) { ModBase.Log(ex, "保存个人设置记忆失败；保留已有记忆", ModBase.LogLevel.Hint); }
        finally { session.Dispose(); }
    }
}
