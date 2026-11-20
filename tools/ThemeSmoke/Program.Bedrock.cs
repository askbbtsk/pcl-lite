using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using PCL;
using PCL.Core.App;
using PCL.Core.Link.Bedrock;
using PCL.Network;
using PCL.Network.Loaders;

internal static partial class Program
{
    private static void CheckBedrockDownloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pcl-bedrock-ui-" + Guid.NewGuid().ToString("N"));
        Config.Launch.BedrockDownloadDirectory = directory;
        try
        {
            var entry = new BedrockVersionEntry("1.21-test", "1.21", "Release",
                BedrockPackageKind.Uwp, "", "x64", "", "");
            var row = new PageBedrockDownload.VersionView(entry);
            row.SetBusy(true);
            row.SetProgress(45);
            row.SetStatus("Downloading 45%");
            var width = row.ProgressWidth;
            row.RefreshState(); // The actual Loaded/re-entry refresh.
            if (row.Status != "Downloading 45%" || row.ProgressWidth != width)
                throw new InvalidDataException("Returning to Bedrock overwrites an active download's row state.");
            CheckBedrockActiveRows(entry, row);

            var file = new DownloadFile(["http://127.0.0.1/fixture"], Path.Combine(directory, "fixture.appx"))
                { TotalSize = 1000, DownloadedBytes = 0, State = NetState.Downloading };
            var download = new LoaderDownload("Bedrock progress fixture", [file]);
            var root = new ModLoader.LoaderCombo<string>("Bedrock fixture", [download]);
            var observe = typeof(ModBedrockDownload).GetMethod("ObserveDownloadAsync",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            double observed = 0;
            var completion = (Task<string>)observe.Invoke(null,
                [root, download, file.LocalPath, new Action<double>(value => Volatile.Write(ref observed, value))])!;
            var worker = Task.Run(async () =>
            {
                file.DownloadedBytes = 450;
                await Task.Delay(350);
                if (Volatile.Read(ref observed) < 44 || Volatile.Read(ref observed) > 46)
                    throw new InvalidDataException("Bedrock receives no progress from the production download loader.");
                root.State = ModBase.LoadState.Finished;
            });
            try
            {
                worker.GetAwaiter().GetResult();
                completion.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }
            finally { root.Abort(); }

            CheckBedrockCancellation(observe, directory);
            Console.WriteLine("Bedrock downloads: re-entry/filter state, independent rows, live progress and explicit cancellation passed.");
        }
        finally
        {
            Config.Launch.BedrockDownloadDirectory = "";
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void CheckBedrockActiveRows(BedrockVersionEntry entry, PageBedrockDownload.VersionView first)
    {
        var secondEntry = entry with { Key = "1.22-test", Version = "1.22" };
        var second = new PageBedrockDownload.VersionView(secondEntry);
        second.SetBusy(true);
        second.SetProgress(70);
        var active = new Dictionary<string, PageBedrockDownload.VersionView>(StringComparer.OrdinalIgnoreCase)
        {
            [ModBedrockDownload.GetPackagePath(entry)] = first,
            [ModBedrockDownload.GetPackagePath(secondEntry)] = second
        };
        var page = (PageBedrockDownload)RuntimeHelpers.GetUninitializedObject(typeof(PageBedrockDownload));
        typeof(PageBedrockDownload).GetField("_activeVersions", PrivateInstance)!.SetValue(page, active);
        var resolve = typeof(PageBedrockDownload).GetMethod("GetVersionView", PrivateInstance)!;
        if (!ReferenceEquals(first, resolve.Invoke(page, [entry])) ||
            !ReferenceEquals(second, resolve.Invoke(page, [secondEntry])))
            throw new InvalidDataException("Filtering replaces an active Bedrock download row.");
        if (first.CanStart || second.CanStart || first.ProgressWidth == second.ProgressWidth)
            throw new InvalidDataException("Parallel Bedrock tasks do not retain independent state.");
        active.Remove(ModBedrockDownload.GetPackagePath(entry));
        var idle = (PageBedrockDownload.VersionView)resolve.Invoke(page, [entry])!;
        if (!idle.CanStart || !second.IsBusy)
            throw new InvalidDataException("Finishing one Bedrock task affects another version.");
    }

    private static void CheckBedrockCancellation(MethodInfo observe, string directory)
    {
        foreach (var state in new[] { ModBase.LoadState.Failed, ModBase.LoadState.Aborted })
        {
            var file = new DownloadFile(["http://127.0.0.1/fixture"], Path.Combine(directory, state + ".appx"));
            var download = new LoaderDownload("Bedrock cancellation fixture", [file]);
            var root = new ModLoader.LoaderCombo<string>("Bedrock cancellation fixture", [download]);
            var waiting = (Task<string>)observe.Invoke(null, [root, download, file.LocalPath, null])!;
            var worker = Task.Run(() =>
            {
                root.Error = new TaskCanceledException("Simulated network timeout");
                root.State = state;
            });
            worker.GetAwaiter().GetResult();
            try
            {
                waiting.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                throw new InvalidDataException("A failed or cancelled download returned success.");
            }
            catch (OperationCanceledException error)
            {
                var explicitCancellation = error.GetType().Name == "DownloadCanceledException";
                if (explicitCancellation != (state == ModBase.LoadState.Aborted))
                    throw new InvalidDataException("A download failure is incorrectly reported as user cancellation.");
            }
        }
    }
}
