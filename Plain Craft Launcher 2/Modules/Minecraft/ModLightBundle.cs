using System.IO;
using System.Security.Cryptography;
using PCL.Core.IO.Net.Http;
using PCL.Core.Minecraft.Packs;
using PCL.Network;

namespace PCL;

internal static class ModLightBundle
{
    public static void Download(ModLoader.LoaderTask<int, int> task, string architecture, string directory)
    {
        var token = task.AbortedToken;
        using var response = HttpRequest.Create($"https://api.github.com/repos/{LightReleaseResolver.Repository}/releases/latest")
            .WithHeader("Accept", "application/vnd.github+json")
            .SendAsync(cancellationToken: token).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var release = LightReleaseResolver.Resolve(response.AsString(), architecture);
        var expected = release.Sha256;
        if (expected is null)
        {
            if (release.ChecksumsUrl is null) throw new InvalidDataException("PCL Light release has no verifiable checksum.");
            expected = ReadChecksum(release.ChecksumsUrl, release.Name, token);
        }
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, LightReleaseResolver.BundledFileName);
        var temporary = destination + ".download";
        var urls = LightReleaseResolver.DownloadCandidates(release.Url, UpdatesGitHubModel.DownloadProxies);
        var tracked = new DownloadFile(urls, temporary);
        var download = FileDownloader.DownloadAsync(urls, temporary, cancellationToken: token, trackedFile: tracked);
        try
        {
            while (!download.IsCompleted)
            {
                task.Progress = 0.1d + 0.85d * tracked.Progress;
                Thread.Sleep(50);
            }
            download.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            using var input = File.OpenRead(temporary);
            var actual = Convert.ToHexString(SHA256.HashData(input));
            input.Close();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PCL Light SHA-256 verification failed.");
            File.Move(temporary, destination);
            task.Progress = 1;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ReadChecksum(string url, string assetName, CancellationToken token)
    {
        Exception? lastError = null;
        foreach (var candidate in LightReleaseResolver.DownloadCandidates(url, UpdatesGitHubModel.DownloadProxies))
        {
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var response = HttpRequest.Create(candidate).SendAsync(retryTimes: 1, cancellationToken: timeout.Token)
                    .GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                return LightReleaseResolver.ReadChecksum(response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult(), assetName);
            }
            catch (Exception ex) when (!token.IsCancellationRequested) { lastError = ex; }
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("PCL Light checksum could not be read from GitHub or its proxy fallbacks.", lastError);
    }
}
