using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.App.Lunar;

/// <summary>Updates Lunar native files by content hash and never writes an identical library.</summary>
internal static class LunarNativeArchiveExtractor
{
    internal static async Task ExtractChangedFilesAsync(string archivePath, string nativesDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(nativesDirectory);
        var stagingDirectory = Path.Combine(nativesDirectory, ".pcl-native-stage-" + Guid.NewGuid().ToString("N"));
        var unavailable = new List<string>();
        try
        {
            // Keep ZipFile's built-in traversal checks, but unpack away from the live
            // natives before replacing any files used by another Minecraft process.
            ZipFile.ExtractToDirectory(archivePath, stagingDirectory);
            var root = Path.GetFullPath(nativesDirectory);
            foreach (var stagedFile in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(stagingDirectory, stagedFile);
                var destination = Path.GetFullPath(Path.Combine(root, relative));
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A native archive entry resolves outside its target directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var stagedHash = await HashAsync(stagedFile, cancellationToken).ConfigureAwait(false);
                if (File.Exists(destination)
                    && string.Equals(stagedHash,
                        await HashAsync(destination, cancellationToken).ConfigureAwait(false),
                        StringComparison.Ordinal))
                    continue;

                // Write a sibling first so replacement is atomic when the OS permits it.
                // If another process has the DLL loaded without delete sharing, leave its
                // file untouched and report the path for a friendly retry-after-close prompt.
                var temporary = destination + ".pcl-native-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var source = new FileStream(stagedFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                                     81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                     81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    TryDelete(temporary);
                    throw;
                }

                try
                {
                    File.Move(temporary, destination, overwrite: true);
                }
                catch (IOException)
                {
                    unavailable.Add(relative);
                }
                catch (UnauthorizedAccessException)
                {
                    unavailable.Add(relative);
                }
                finally
                {
                    TryDelete(temporary);
                }
            }

            if (unavailable.Count > 0) throw new LunarNativeFilesUnavailableException(unavailable);
        }
        finally
        {
            try { if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Friendly, path-only diagnostic for native libraries that another process prevents from replacing.</summary>
public sealed class LunarNativeFilesUnavailableException(IReadOnlyList<string> files)
    : IOException("部分 Lunar 原生库与所需版本不同，但文件无法替换。请关闭正在运行的相关游戏后重试："
                  + string.Join("、", files.Take(6))
                  + (files.Count > 6 ? $"……另有 {files.Count - 6} 个文件" : string.Empty))
{
    public IReadOnlyList<string> Files { get; } = files;
}
