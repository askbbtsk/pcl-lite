using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.ResourceCollections;

/// <summary>Explicit, non-overwriting copy of selected local mods. Successful earlier items survive cancellation.</summary>
public static class ResourceCollectionCopyService
{
    public static Task<ResourceCopyReport> CopyAsync(IReadOnlyList<string> selectedSources, string destinationDirectory,
        CancellationToken cancellationToken = default, IProgress<ResourceCopyItem>? progress = null)
        // Do not cancel Task.Run admission: callers always receive the partial/cancelled report.
        => Task.Run(() => Copy(selectedSources, destinationDirectory, cancellationToken, progress));

    public static ResourceCopyReport Copy(IReadOnlyList<string> selectedSources, string destinationDirectory,
        CancellationToken cancellationToken = default, IProgress<ResourceCopyItem>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(selectedSources);
        if (selectedSources.Count > 10000) throw new ArgumentException("Too many selected source files.", nameof(selectedSources));
        var results = new List<ResourceCopyItem>();
        if (selectedSources.Count == 0) return new(results, cancellationToken.IsCancellationRequested);
        if (cancellationToken.IsCancellationRequested) return CancelRemaining(selectedSources, 0, results);
        string destination;
        FileStream? gate = null;
        try
        {
            destination = ResourceCollectionPaths.Normalize(destinationDirectory);
            ResourceCollectionPaths.RejectLinks(destination);
            Directory.CreateDirectory(destination);
            ResourceCollectionPaths.RejectLinks(destination);
            var lockPath = Path.Combine(destination, ".pcl-resource-copy-lock.tmp");
            ResourceCollectionPaths.RejectLinks(lockPath);
            gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ResourceCollectionPaths.ExpectedFailure(ex))
        {
            gate?.Dispose();
            foreach (var source in selectedSources)
                results.Add(new(source, null, ResourceCopyStatus.Failed,
                    ex is InvalidDataException ? ResourceCopyFailure.Linked : ResourceCopyFailure.OccupiedOrUnreadable, ex.Message));
            return new(results, false);
        }
        using (gate)
        {
            for (var index = 0; index < selectedSources.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested) return CancelRemaining(selectedSources, index, results);
                ResourceCopyItem item;
                try { item = CopyOne(selectedSources[index], destination, cancellationToken); }
                catch (OperationCanceledException) { return CancelRemaining(selectedSources, index, results); }
                results.Add(item);
                progress?.Report(item);
            }
        }
        return new(results, cancellationToken.IsCancellationRequested);
    }

    private static ResourceCopyItem CopyOne(string source, string destination, CancellationToken token)
    {
        string? target = null;
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var fullSource = ResourceCollectionPaths.Normalize(source);
            if (ResourceCollectionPaths.HasLinkAncestor(fullSource))
                return Failed(source, null, ResourceCopyFailure.Linked, "The source or an ancestor is a link.");
            if (!File.Exists(fullSource)) return Failed(source, null, ResourceCopyFailure.Missing, "The source file is missing.");
            if (!ResourceCollectionPaths.IsSupported(fullSource))
                return Failed(source, null, ResourceCopyFailure.Unsupported, "Only JAR, ZIP or LiteMod files and their disabled variants can be copied.");
            fullSource = ResourceCollectionPaths.ExpandLongName(fullSource, out _);
            target = Path.Combine(destination, Path.GetFileName(fullSource));
            ResourceCollectionPaths.RejectLinks(target);
            // Denies regular writes, deletes and renames for the duration of capture + verified commit.
            using var input = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = input.Length;
            var modified = File.GetLastWriteTimeUtc(fullSource);
            temporary = Path.Combine(destination, ".pcl-resource-copy-" + Guid.NewGuid().ToString("N") + ".tmp");
            string copiedHash;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                copiedHash = CopyAndHash(input, output, token);
                output.Flush(flushToDisk: true);
            }
            if (input.Length != length || File.GetLastWriteTimeUtc(fullSource) != modified || new FileInfo(temporary).Length != length)
                return Failed(source, target, ResourceCopyFailure.SourceChanged, "Source content changed during the copy.");
            using (var verified = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (Hash(verified, token) != copiedHash)
                    return Failed(source, target, ResourceCopyFailure.VerificationFailed, "Staged file hash does not match the copied bytes.");
                verified.Position = 0;
                try
                {
                    using var archive = new ZipArchive(verified, ZipArchiveMode.Read, leaveOpen: true);
                    _ = archive.Entries.Count; // Check the archive directory without extracting or executing content.
                }
                catch (InvalidDataException ex)
                { return Failed(source, target, ResourceCopyFailure.InvalidArchive, ex.Message); }
            }
            token.ThrowIfCancellationRequested();
            ResourceCollectionPaths.RejectLinks(fullSource);
            ResourceCollectionPaths.RejectLinks(target);
            if (input.Length != length || File.GetLastWriteTimeUtc(fullSource) != modified)
                return Failed(source, target, ResourceCopyFailure.SourceChanged, "Source content changed before commit.");
            if (File.Exists(target)) return CompareExisting(source, target, copiedHash, token);
            if (Directory.Exists(target)) return Failed(source, target, ResourceCopyFailure.NameConflict, "A directory occupies the target name.");
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target))
            {
                // Another process may have populated the target after the existence check.
                ResourceCollectionPaths.RejectLinks(target);
                return CompareExisting(source, target, copiedHash, token);
            }
            temporary = null;
            return new(source, target, ResourceCopyStatus.Copied, Sha256: copiedHash);
        }
        catch (Exception ex) when (ResourceCollectionPaths.ExpectedFailure(ex))
        { return Failed(source, target, ex is InvalidDataException ? ResourceCopyFailure.Linked : ResourceCopyFailure.OccupiedOrUnreadable, ex.Message); }
        finally
        {
            // This task only owns its uncommitted, randomly named staging file.
            if (temporary is not null)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ResourceCollectionPaths.ExpectedFailure(ex)) { /* Preserve on cleanup failure; never touch the source. */ }
            }
        }
    }

    private static ResourceCopyItem CompareExisting(string source, string target, string hash, CancellationToken token)
    {
        using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Hash(stream, token) == hash
            ? new(source, target, ResourceCopyStatus.AlreadyPresent, Sha256: hash)
            : Failed(source, target, ResourceCopyFailure.NameConflict, "A file with different content already exists; it was not replaced.");
    }

    private static string CopyAndHash(Stream source, Stream destination, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Hash(Stream source, CancellationToken token) => CopyAndHash(source, Stream.Null, token);

    private static ResourceCopyItem Failed(string source, string? target, ResourceCopyFailure failure, string error)
        => new(source, target, ResourceCopyStatus.Failed, failure, error);

    private static ResourceCopyReport CancelRemaining(IReadOnlyList<string> sources, int start, List<ResourceCopyItem> results)
    {
        for (var index = start; index < sources.Count; index++)
            results.Add(new(sources[index], null, ResourceCopyStatus.Cancelled, ResourceCopyFailure.Cancelled));
        return new(results, true);
    }
}
