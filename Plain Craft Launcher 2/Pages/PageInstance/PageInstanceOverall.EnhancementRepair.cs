using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Enhancements;
using PCL.Core.Minecraft.Instances;
using PCL.Core.Minecraft.Packs;
using PCL.Network;

namespace PCL;

public partial class PageInstanceOverall
{
    private static string FillMissingLibraries(string instanceDirectory, string gameRoot,
        InstanceResolvedReport preview, CancellationToken token)
    {
        using var operation = InstanceOperationCoordinator.Acquire(Path.Combine(Paths.SharedLocalData, "InstanceOperations"),
            gameRoot, new McInstance(instanceDirectory).PathIndie);
        if (ModWatcher.mcWatcherList.Any(watcher => watcher.State is ModWatcher.Watcher.MinecraftState.Loading or ModWatcher.Watcher.MinecraftState.Running
            && watcher.version is not null && watcher.version.PathInstance.StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException(Lang.Text("Instance.PackUpdate.Error.GameRunning"));
        if (!string.Equals(gameRoot, ModFolder.mcFolderSelected, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Lang.Text("Instance.Enhancement.InputChanged"));
        var current = AnalyzeInstance(instanceDirectory, gameRoot, token);
        if (!current.InputsStable || current.Fingerprint != preview.Fingerprint)
            throw new IOException(Lang.Text("Instance.Enhancement.InputChanged"));
        var currentMissing = current.Libraries.Where(entry => entry.Status == LibraryHealthStatus.Missing)
            .Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declared = ModLibrary.McLibListGet(new McInstance(instanceDirectory), true);
        var targets = declared.Where(library => currentMissing.Contains(library.LocalPath)).ToArray();
        var result = new StringBuilder();
        var copied = 0;
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            string temporary = null;
            try
            {
                // SHA-1 is accepted only when supplied by the existing effective library declaration.
                if (target.IsLocal || target.Sha1 is null || target.Sha1.Length != 40 || !target.Sha1.All(char.IsAsciiHexDigit)
                    || !Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    throw new InvalidDataException(Lang.Text("Instance.Enhancement.NoVerifiedSource"));
                var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
                var destination = Path.GetFullPath(target.LocalPath);
                if (!destination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Lang.Text("Instance.Enhancement.NoVerifiedSource"));
                PackExportContent.CheckAncestors(fullRoot, destination);
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".pcl-library-" + Guid.NewGuid().ToString("N") + ".tmp");
                FileDownloader.DownloadAsync(ModDownload.DlSourceLibraryGet(target.Url), temporary,
                    cancellationToken: token).GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                using (var downloaded = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                    if (target.size > 0 && downloaded.Length != target.size
                        || !Convert.ToHexString(SHA1.HashData(downloaded)).Equals(target.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(Lang.Text("Instance.Enhancement.DownloadInvalid"));
                PackExportContent.CheckAncestors(fullRoot, destination);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
                copied++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                result.AppendLine().Append(target.OriginalName ?? Path.GetFileName(target.LocalPath)).Append(": ").Append(error.Message);
                ModBase.Log(error, "[Enhancements] Missing library was not installed: " + target.LocalPath, ModBase.LogLevel.Normal);
            }
            finally
            {
                if (temporary is not null)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        return Lang.Text("Instance.Enhancement.FillResult", copied, targets.Length - copied) + result;
    }
}
