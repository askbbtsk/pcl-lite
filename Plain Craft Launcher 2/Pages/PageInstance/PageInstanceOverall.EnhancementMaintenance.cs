using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Enhancements;
using PCL.Core.Minecraft.Instances;
using PCL.Core.UI;

namespace PCL;

public partial class PageInstanceOverall
{
    private async void BtnEnhancementClearCache_Click(object sender, MouseButtonEventArgs e)
    {
        var instance = PageInstanceLeft.McInstance;
        var gameRoot = ModFolder.mcFolderSelected;
        if (ModMain.MyMsgBox(Lang.Text("Instance.Enhancement.ClearCache.Confirm"),
            Lang.Text("Instance.Enhancement.ClearCache.Title"), Lang.Text("Common.Action.Confirm"),
            Lang.Text("Common.Action.Cancel")) != 1) return;
        try
        {
            var cleared = await Task.Run(() =>
            {
                using var operation = InstanceOperationCoordinator.Acquire(Path.Combine(Paths.SharedLocalData, "InstanceOperations"),
                    gameRoot, instance.PathIndie);
                if (ModWatcher.mcWatcherList.Exists(watcher =>
                    (watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running)
                    && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(watcher.version.PathIndie)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.PathIndie)), StringComparison.OrdinalIgnoreCase)))
                    throw new IOException(Lang.Text("Instance.PackUpdate.Error.GameRunning"));
                var store = new InstanceEnhancementStore(instance.PathInstance);
                var preview = store.ReadResolved();
                if (preview.Status == EnhancementReadStatus.Missing) return false;
                if (preview.Status != EnhancementReadStatus.Valid || preview.ContentSha256 is null)
                    throw new IOException(Lang.Text("Instance.Enhancement.ClearCache.Preserved"));
                return store.ClearResolved(preview.ContentSha256);
            });
            if (!IsLoaded || !ReferenceEquals(instance, PageInstanceLeft.McInstance)
                || !string.Equals(gameRoot, ModFolder.mcFolderSelected, StringComparison.OrdinalIgnoreCase)) return;
            HintService.Hint(Lang.Text(cleared ? "Instance.Enhancement.ClearCache.Done" : "Instance.Enhancement.ClearCache.Empty"), HintType.Success);
        }
        catch (Exception error)
        {
            ModBase.Log(error, "[Enhancements] Cache cleanup was not applied", ModBase.LogLevel.Normal);
            if (IsLoaded && ReferenceEquals(instance, PageInstanceLeft.McInstance))
                HintService.Hint(Lang.Text("Instance.Enhancement.HealthFailure", error.Message), HintType.Error);
        }
    }
}
