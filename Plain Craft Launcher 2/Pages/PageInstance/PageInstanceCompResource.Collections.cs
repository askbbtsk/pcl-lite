using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Instances;
using PCL.Core.Minecraft.ResourceCollections;

namespace PCL;

public partial class PageInstanceCompResource
{
    private bool _collectionOperation;

    private void CollectionButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is MyButton button)
            button.Visibility = currentCompType == ModComp.CompType.Mod ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SaveResourceCollection_Click(object sender, MouseButtonEventArgs e)
    {
        if (_collectionOperation || currentCompType != ModComp.CompType.Mod) return;
        var sources = modItems.Values.Select(item => item.Entry)
            .Where(entry => !entry.IsFolder && selectedMods.Contains(entry.RawPath))
            .Select(entry => entry.path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Length == 0)
        {
            HintService.Hint(Lang.Text("Instance.Collections.SelectFiles"));
            return;
        }
        var name = ModMain.MyMsgBoxInput(Lang.Text("Instance.Collections.Save"), Lang.Text("Instance.Collections.Name"),
            PageInstanceLeft.McInstance.Name + " Mods");
        if (string.IsNullOrWhiteSpace(name)) return;
        _collectionOperation = true;
        try
        {
            var document = await Task.Run(() =>
            {
                var value = ResourceCollectionIndexer.Capture(name.Trim(), sources);
                new ResourceCollectionStore(Paths.Data).Save(value);
                return value;
            });
            ModMain.MyMsgBox(Lang.Text("Instance.Collections.Saved", document.Name, document.Entries.Count)
                + FormatCollectionEntries(document), Lang.Text("Instance.Collections.Title"));
        }
        catch (Exception error) { ShowCollectionError(error); }
        finally { _collectionOperation = false; }
    }

    private async void BrowseResourceCollections_Click(object sender, MouseButtonEventArgs e)
    {
        if (_collectionOperation || currentCompType != ModComp.CompType.Mod) return;
        _collectionOperation = true;
        var target = PageInstanceLeft.McInstance;
        var gameRoot = ModFolder.mcFolderSelected;
        using var cancellation = new CancellationTokenSource();
        void CancelOnUnload(object _, RoutedEventArgs __) => cancellation.Cancel();
        Unloaded += CancelOnUnload;
        try
        {
            var store = new ResourceCollectionStore(Paths.Data);
            var records = await Task.Run(store.List);
            if (!IsLoaded || cancellation.IsCancellationRequested) return;
            var available = records.Where(record => record.Status == ResourceCollectionReadStatus.Valid).ToArray();
            var invalid = records.Count - available.Length;
            if (invalid > 0) HintService.Hint(Lang.Text("Instance.Collections.Unreadable", invalid), HintType.Warning);
            if (available.Length == 0)
            {
                HintService.Hint(Lang.Text("Instance.Collections.Empty"));
                return;
            }
            var choices = available.Select(record => (IMyRadio)new MyRadioBox
            {
                Text = record.Document.Name + " (" + record.Document.Entries.Count + ")", MinHeight = 28,
            }).ToList();
            var selected = ModMain.MyMsgBoxSelect(choices, Lang.Text("Instance.Collections.Title"),
                Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel"));
            if (selected is null || selected < 0 || selected >= available.Length) return;
            var loaded = available[selected.Value];
            var collection = await Task.Run(() => ResourceCollectionIndexer.Refresh(loaded.Document, cancellation.Token), cancellation.Token);
            if (!IsLoaded || cancellation.IsCancellationRequested || !ReferenceEquals(target, PageInstanceLeft.McInstance)) return;
            // This is an explicit refresh. Never overwrite an unknown schema or an externally edited index.
            await Task.Run(() => store.Save(collection, loaded.ContentSha256, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsLoaded || !ReferenceEquals(target, PageInstanceLeft.McInstance)) return;
            var preview = Lang.Text("Instance.Collections.ApplyPreview", collection.Name, target.Name)
                + FormatCollectionEntries(collection);
            if (ModMain.MyMsgBox(preview, Lang.Text("Instance.Collections.Title"), Lang.Text("Instance.Collections.Copy"),
                    Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
            var destination = target.Info.HasLabyMod
                ? Path.Combine(target.PathIndie, "labymod-neo", "fabric", target.Info.VanillaName, "mods")
                : Path.Combine(target.PathIndie, "mods");
            ResourceCopyReport report;
            using (InstanceOperationCoordinator.Acquire(Path.Combine(Paths.SharedLocalData, "InstanceOperations"), gameRoot, target.PathIndie))
            {
                if (ModWatcher.mcWatcherList.Any(watcher => watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running
                    && watcher.version is not null && string.Equals(watcher.version.PathIndie, target.PathIndie, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException(Lang.Text("Instance.PackUpdate.Error.GameRunning"));
                report = await ResourceCollectionCopyService.CopyAsync(collection.Entries.Select(entry => entry.CanonicalPath).ToArray(),
                    destination, cancellation.Token);
            }
            ModBase.Log("[Collections] " + System.Text.Json.JsonSerializer.Serialize(report), ModBase.LogLevel.Normal);
            if (!IsLoaded || !ReferenceEquals(target, PageInstanceLeft.McInstance)) return;
            var result = new StringBuilder(Lang.Text("Instance.Collections.Result", report.CopiedCount, report.AlreadyPresentCount, report.FailedCount));
            if (report.Cancelled) result.AppendLine().AppendLine(Lang.Text("Instance.Collections.Cancelled"));
            foreach (var item in report.Items.Where(item => item.Status == ResourceCopyStatus.Failed).Take(35))
                result.AppendLine().Append(Path.GetFileName(item.SourcePath)).Append(": ")
                    .Append(Lang.Text("Instance.Collections.Failure." + item.Failure));
            ModMain.MyMsgBox(result.ToString(), Lang.Text("Instance.Collections.Title"));
            LoaderRun(ModLoader.LoaderFolderRunType.ForceRun);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowCollectionError(error); }
        finally { Unloaded -= CancelOnUnload; _collectionOperation = false; }
    }

    private static string FormatCollectionEntries(ResourceCollectionDocument document)
    {
        var text = new StringBuilder();
        foreach (var entry in document.Entries.Take(30))
            text.AppendLine().Append(Path.GetFileName(entry.CanonicalPath)).Append(" — ")
                .Append(Lang.Text("Instance.Collections.Availability." + entry.Availability));
        if (document.Entries.Count > 30) text.AppendLine().Append(Lang.Text("Instance.Enhancement.MoreInLog"));
        if (document.Notices.Count > 0)
            text.AppendLine().AppendLine(Lang.Text("Instance.Collections.AliasNotice", document.Notices.Count));
        ModBase.Log("[Collections] " + System.Text.Json.JsonSerializer.Serialize(document), ModBase.LogLevel.Normal);
        return text.ToString();
    }

    private static void ShowCollectionError(Exception error)
    {
        ModBase.Log(error, "[Collections] Collection operation failed", ModBase.LogLevel.Normal);
        HintService.Hint(Lang.Text("Instance.Collections.Error", error.Message), HintType.Error);
    }
}
