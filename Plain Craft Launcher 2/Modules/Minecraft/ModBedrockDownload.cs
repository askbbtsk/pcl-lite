using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.Bedrock;

namespace PCL;

/// <summary>
///     基岩版安装包的下载与本地存放。
///     <para>
///         目录结构（默认在启动器数据目录下，删启动器时一起走）：
///         <c>Bedrock/packages</c> 存下载到的原包，<c>Bedrock/versions</c> 存解包后的 GDK 版本，
///         <c>Bedrock/extracted</c> 存 UWP 包解出来的、等待注册的目录。
///     </para>
///     <para>
///         根目录可以用 <c>Config.Launch.BedrockDownloadDirectory</c> 换位置
///         （GDK 解包后 2 GB 起，放系统盘容易撑爆），留空时用 <c>%LOCALAPPDATA%\PCLLite\Bedrock</c>。
///     </para>
/// </summary>
public static class ModBedrockDownload
{
    private static IReadOnlyList<BedrockVersionEntry> _cache = [];

    public static string RootDirectory
    {
        get
        {
            var custom = Config.Launch.BedrockDownloadDirectory;
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Paths.SharedLocalData, "Bedrock")
                : custom.Trim();
        }
    }
    public static string PackagesDirectory => Path.Combine(RootDirectory, "packages");
    public static string VersionsDirectory => Path.Combine(RootDirectory, "versions");
    public static string ExtractedDirectory => Path.Combine(RootDirectory, "extracted");

    /// <summary>最近一次拉到的版本列表（可能为空）。</summary>
    public static IReadOnlyList<BedrockVersionEntry> Cached => _cache;

    /// <summary>拉取版本列表并缓存。force 为 false 且已有缓存时直接返回。</summary>
    public static async Task<IReadOnlyList<BedrockVersionEntry>> LoadAsync(bool force = false,
        CancellationToken token = default)
    {
        if (!force && _cache.Count > 0) return _cache;

        _cache = await BedrockVersionCatalog.FetchAsync(token).ConfigureAwait(false);
        ModBase.Log($"[Bedrock] 版本列表已更新，共 {_cache.Count} 条");
        return _cache;
    }

    /// <summary>这个版本的原包是否已经在本地。</summary>
    public static bool IsDownloaded(BedrockVersionEntry entry) => File.Exists(GetPackagePath(entry));

    /// <summary>原包在本地应该叫什么。扩展名从地址推断，推断不出时按形态给默认值。</summary>
    public static string GetPackagePath(BedrockVersionEntry entry)
    {
        Directory.CreateDirectory(PackagesDirectory);

        var extension = entry.Kind == BedrockPackageKind.Gdk ? ".msixvc" : ".appxbundle";
        if (entry.HasDirectUrl)
        {
            var fromUrl = Path.GetExtension(new Uri(entry.Meta).AbsolutePath);
            if (fromUrl is ".msixvc" or ".appx" or ".appxbundle" or ".msix" or ".msixbundle")
                extension = fromUrl;
        }

        // 键里可能带括号等字符，统一清洗成安全文件名
        var safeKey = string.Concat(entry.Key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(PackagesDirectory, $"{safeKey}{extension}");
    }

    /// <summary>
    ///     把版本解析成真实下载地址。GDK 直接用列表里的地址，UWP 走 FE3。
    ///     <para>
    ///         解析不出来时直接抛异常，把「下架」「接口报错」「连不上」三种情况分开说 ——
    ///         以前一律返回 null，上层只能提示「可能被网络拦截了」，
    ///         而实测 1.18.10.21 那种老 Beta 是微软把它从下载服务里删了，跟网络无关。
    ///     </para>
    /// </summary>
    public static async Task<string> ResolveUrlAsync(BedrockVersionEntry entry)
    {
        if (entry.HasDirectUrl) return entry.Meta;

        var result = await ModBedrockUwp.ResolveDownloadUrlAsync(entry.Meta).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Url)) return result.Url!;

        ModBase.Log($"[Bedrock] 未能解析 {entry.Key} 的下载地址（UpdateID {entry.Meta}）：" +
                    $"{result.Failure} {result.Detail}", ModBase.LogLevel.Normal);

        throw new InvalidOperationException(result.Failure switch
        {
            ModBedrockUwp.Fe3ResolveFailure.Delisted =>
                Lang.Text("Bedrock.Download.Err.Delisted", entry.Key),
            ModBedrockUwp.Fe3ResolveFailure.HttpError =>
                Lang.Text("Bedrock.Download.Err.Fe3", result.Detail),
            _ => Lang.Text("Bedrock.Download.Err.NoFe3", result.Detail)
        });
    }

    /// <summary>
    ///     用 PCL 自己的下载器把原包拉下来，返回本地路径。
    ///     <para>
    ///         走 <c>LoaderDownload</c> + <c>DownloadFile</c> 而不是自己开 <c>HttpClient</c>：
    ///         这样才会出现在右下角的下载任务栏里（进度、速度、线程数、可中止），
    ///         也才吃得到分段并发、失败重试、镜像与代理这一整套。
    ///         以前这里是一条裸 <c>ReadAsync</c> 循环，断流就只能整包重下，
    ///         用户也完全看不出在下载什么。
    ///     </para>
    /// </summary>
    /// <param name="entry">目标版本。</param>
    /// <param name="url">已经解析好的直链。</param>
    /// <param name="onProgress">进度回调，参数是 0～100 的百分比，可能在任意线程触发。</param>
    /// <param name="token">取消令牌。</param>
    public static async Task<string> DownloadWithLoaderAsync(BedrockVersionEntry entry, string url,
        Action<double>? onProgress = null, CancellationToken token = default)
    {
        var target = GetPackagePath(entry);
        Directory.CreateDirectory(PackagesDirectory);

        // 列表给了 MD5 就按 MD5 复用已存在的文件（FileChecker 会按哈希长度自己认出 MD5）；
        // 没给就只要求「文件非空」，否则每次点都会整包重下。
        var checker = string.IsNullOrWhiteSpace(entry.Md5)
            ? new ModBase.FileChecker(minSize: 1)
            : new ModBase.FileChecker(hash: entry.Md5);

        var file = new PCL.Network.DownloadFile([url], target, checker);
        var download = new PCL.Network.Loaders.LoaderDownload(
            Lang.Text("Bedrock.Download.Task.Download", entry.Key), [file]) { ProgressWeight = 10d };

        var loader = new ModLoader.LoaderCombo<string>(
            Lang.Text("Bedrock.Download.Task.Download", entry.Key), [download]);

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (onProgress is not null)
            download.ProgressChanged += (newProgress, _) => onProgress(newProgress * 100d);

        loader.OnStateChangedThread += (_, state, _) =>
        {
            switch (state)
            {
                case ModBase.LoadState.Finished:
                    completion.TrySetResult(target);
                    break;
                case ModBase.LoadState.Failed:
                    completion.TrySetException(loader.Error ?? new Exception(Lang.Text("Bedrock.Download.Md5Failed")));
                    break;
                case ModBase.LoadState.Aborted:
                    completion.TrySetCanceled();
                    break;
            }
        };
        loader.hasOnStateChangedThread = true;

        using var registration = token.Register(loader.Abort);

        ModLoader.LoaderTaskbarAdd(loader);
        loader.Start();

        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>下载后按官方 MD5 校验。列表没给 MD5 时直接放行。</summary>
    public static bool VerifyMd5(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;

        try
        {
            using var stream = File.OpenRead(path);
            var hash = System.Security.Cryptography.MD5.HashData(stream);
            var actual = Convert.ToHexString(hash);
            var ok = string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!ok)
                ModBase.Log($"[Bedrock] MD5 不匹配：期望 {expected}，实际 {actual}", ModBase.LogLevel.Normal);
            return ok;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Bedrock] MD5 校验失败", ModBase.LogLevel.Normal);
            return false;
        }
    }
}
